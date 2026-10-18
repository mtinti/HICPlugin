// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Reflection;
using FAnsi;
using FAnsi.Discovery.QuerySyntax;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.QueryBuilding.Parameters;
using Rdmp.Core.ReusableLibraryCode.Settings;
using Rdmp.Core.MapsDirectlyToDatabaseTable;

namespace RdmpLinkedServerCohortSql;

internal sealed class LinkedServerCohortSqlBuilder
{
    internal static readonly StringComparer ServerComparer = StringComparer.CurrentCultureIgnoreCase;

    public LinkedServerCohortSqlResult Build(CohortIdentificationConfiguration cic) => Build(cic, staged: false);

    /// <summary>Renders the cohort as the composite (one statement, for reading) or the staged script (to run), plan 0.7.</summary>
    public LinkedServerCohortSqlResult Build(CohortIdentificationConfiguration cic, bool staged)
    {
        var rendered = Render(cic, staged);
        return staged ? StagedScriptWriter.Write(rendered) : CompositeScriptWriter.Write(rendered);
    }

    /// <summary>
    /// Phases 1, 4 and 5, shared by both shapes. With <paramref name="staged"/> the staged refusals run in phase 1 and the
    /// result also carries what the staged writer needs, so one render feeds both writers.
    /// </summary>
    internal RenderedCohort Render(CohortIdentificationConfiguration cic, bool staged)
    {
        ArgumentNullException.ThrowIfNull(cic);
        var root = cic.RootCohortAggregateContainer;
        if (root == null) throw Error(LinkedServerRenderReason.EmptyContainer, cic, "create a root container and add a set");
        var sets = Walk(root).OrderBy(s => s.Order).ToArray();
        // Validate API/PIT structure before any core rendering can instantiate third-party compilers.
        foreach (var set in sets)
        {
            CheckApi(set);
            var join = GetJoin(set);
            var pit = join?.JoinableCohortAggregateConfiguration.AggregateConfiguration;
            if (pit == null) continue;
            CheckApi(pit);
            if (pit.PatientIndexJoinablesUsed.Any())
                throw Error(LinkedServerRenderReason.NestedPatientIndexTable, set, $"patient index {pit} itself uses a patient index; flatten that dependency");
        }

        var first = sets.FirstOrDefault(s => s.Catalogue.GetTableInfoList(false).Any());
        if (first == null)
        {
            // Preserve core's detailed invalid-aggregate error for non-API sets without tables.
            foreach (var set in sets) CohortQueryBuilderHelper.GetSQLForAggregate(set, new QueryBuilderArgs(null, cic.GetAllParameters()));
            throw Error(LinkedServerRenderReason.EmptyContainer, cic, "add an enabled set with table metadata");
        }
        var home = Tables(first).First();
        if (home.DatabaseType != DatabaseType.MicrosoftSQLServer)
            throw Error(LinkedServerRenderReason.UnsupportedHomeDatabaseType, first, $"home server {home.Server} uses {home.DatabaseType}; use SQL Server tables");
        ValidateGroup(first, Tables(first));
        CheckDatabase(home, true);
        var syntax = home.GetQuerySyntaxHelper();
        var homeServer = home.Server.Trim();
        var homeDatabase = home.GetDatabaseRuntimeName();
        var placements = sets.Select(s => Classify(s, home)).ToArray();
        var remotes = placements.Where(p => p.Kind != SetPlacementKind.Home).ToArray();
        var warnings = new List<string>();
        warnings.AddRange(TopXWarnings(placements));
        if (staged) CheckStaged(placements);

        // Prefer the EXEC USE form for every remote block: it does not depend on the link login's
        // default database, which in practice is often master (first real run, 2026-10-17: a cohort
        // with a single far-side database hit the guard with "is master, expected <db>"). The guarded
        // form is only the fallback when a result column's type or collation cannot be derived from
        // the catalogue; it still fails loudly rather than running in the wrong database.
        foreach (var placement in remotes)
        {
            try
            {
                placement.ResultColumns = new SqlServerResultColumnResolver(placement.RemoteAggregate).Resolve();
                placement.RemoteBatchForm = RemoteBatchForm.UseDatabase;
            }
            catch (LinkedServerRenderException ex) when (ex.Reason == LinkedServerRenderReason.RemoteResultTypeUnknown)
            {
                placement.RemoteBatchForm = RemoteBatchForm.Guarded;
                warnings.Add($"Set {placement.Set}: guarded form used because {ex.Message} The link login's default database must be " +
                             $"{placement.RemoteTables[0].GetDatabaseRuntimeName()} or the batch fails with Msg 50000.");
            }
        }

        var globals = cic.GetAllParameters();
        var pm = new ParameterManager(globals);
        foreach (var placement in placements)
        {
            CohortQueryBuilderDependencySql pitBlock = null;
            if (placement.Pit != null)
                pitBlock = CohortQueryBuilderHelper.GetSQLForAggregate(placement.Pit,
                    new QueryBuilderArgs(new QueryBuilderCustomArgs(), globals));

            if (placement.Kind == SetPlacementKind.HomeWithRemotePit)
            {
                // Even SELECT * needs a named identifier for the enclosing core join.
                _ = SqlServerResultColumnResolver.DimensionName(Identifier(placement.Pit));
                placement.Collation = CollationClause.Decide(Identifier(placement.Set), Identifier(placement.Pit));
                Wrap(placement, pitBlock, warnings);
                pitBlock = new CohortQueryBuilderDependencySql(SelectRemotePit(placement, syntax), new ParameterManager());
            }

            var block = CohortQueryBuilderHelper.GetSQLForAggregate(placement.Set,
                new QueryBuilderArgs(placement.Join, placement.Pit, pitBlock, null, globals));
            if (placement.Kind == SetPlacementKind.RemoteWhole)
            {
                var id = Identifier(placement.Set);
                var name = SqlServerOpenQueryWrapper.QuoteIdentifier(SqlServerResultColumnResolver.DimensionName(id), syntax);
                placement.Collation = CollationClause.Decide(Identifier(first), id);
                Wrap(placement, block, warnings);
                placement.HomeSql = $"SELECT {name}{placement.Collation} AS {name} FROM {placement.Placeholder}";
            }
            else placement.HomeSql = block.Use(pm);
        }

        var touched = placements.SelectMany(p => p.SetTables.Concat(p.PitTables)).Distinct().ToArray();
        return new RenderedCohort(cic, root, placements, homeServer, homeDatabase,
            QueryBuilder.GetParameterDeclarationSQL(pm.GetFinalResolvedParametersList()), warnings, touched, syntax, staged);
    }

    /// <summary>Plan 0.7.3b: core writes TOP X without ORDER BY, so which rows a TOP X set or patient index keeps is not deterministic.</summary>
    private static IEnumerable<string> TopXWarnings(SetPlacement[] placements) => placements
        .SelectMany(p => p.Pit == null ? new[] { p.Set } : new[] { p.Set, p.Pit }).Distinct()
        .Select(a => (Aggregate: a, TopX: a.GetTopXIfAny())).Where(x => x.TopX != null)
        .Select(x => $"{x.Aggregate} uses TOP {x.TopX.TopX} without ORDER BY; membership is not deterministic and can differ between runs, between shapes and from the cache.");

    /// <summary>
    /// Plan 0.7.3, staged shape only, before any SQL is rendered: every leaf column must have a name (SELECT INTO), a
    /// known position (the projections are positional) and, for home leaves, a collation a temp table keeps.
    /// </summary>
    private static void CheckStaged(SetPlacement[] placements)
    {
        foreach (var placement in placements)
        {
            if (placement.Kind != SetPlacementKind.RemoteWhole)
            {
                var columns = CoreSelectList.For(placement.Set, out var orderProblem);
                if (orderProblem != null)
                    throw Error(LinkedServerRenderReason.StagedColumnOrderAmbiguous, placement.Set,
                        orderProblem + " (the staged shape needs core's column order; the composite still renders, to read)");
                CheckNames(placement.Set, columns);
                var resolver = new SqlServerResultColumnResolver(placement.Set);
                foreach (var column in columns)
                {
                    var problem = resolver.HomeCollationProblem(column);
                    if (problem != null)
                        throw Error(LinkedServerRenderReason.StagedCollationUnknown, placement.Set,
                            $"column {column.Expression} cannot be staged because {problem}; add a trailing COLLATE, or use the composite to read");
                }
                placement.HomeColumns = columns;
            }
            if (placement.Kind != SetPlacementKind.Home)
                CheckNames(placement.RemoteAggregate, CoreSelectList.For(placement.RemoteAggregate, out _));
        }
    }

    private static void CheckNames(AggregateConfiguration aggregate, IReadOnlyList<CoreColumn> columns)
    {
        var unnamed = columns.FirstOrDefault(c => c.Name == null);
        if (unnamed != null)
            throw Error(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias, aggregate,
                $"expression {unnamed.Expression} needs an explicit alias, because SELECT INTO needs a name for every column");
        var duplicate = columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw Error(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias, aggregate,
                $"{duplicate.Count()} columns are named {duplicate.Key} (names are compared ignoring case); alias them apart, because SELECT INTO needs distinct names");
    }

    private static SetPlacement Classify(AggregateConfiguration set, ITableInfo home)
    {
        var tables = Tables(set);
        ValidateGroup(set, tables);
        var join = GetJoin(set);
        var pit = join?.JoinableCohortAggregateConfiguration.AggregateConfiguration;
        var pitTables = pit == null ? Array.Empty<ITableInfo>() : Tables(pit);
        if (pit != null) ValidateGroup(pit, pitTables);
        var atHome = tables.Length == 0 || SameServer(tables[0], home);
        var pitAtHome = pitTables.Length == 0 || SameServer(pitTables[0], home);
        var kind = atHome ? (pitAtHome ? SetPlacementKind.Home : SetPlacementKind.HomeWithRemotePit) : SetPlacementKind.RemoteWhole;
        foreach (var table in tables.Concat(pitTables))
        {
            if (table.DatabaseType != DatabaseType.MicrosoftSQLServer)
                throw Error(LinkedServerRenderReason.UnsupportedRemoteDatabaseType, set,
                    $"table {table} on {table.Server} uses {table.DatabaseType}; use SQL Server tables");
            CheckDatabase(table, SameServer(table, home));
        }
        if (!atHome && pitTables.Length > 0 && !SameServer(tables[0], pitTables[0]))
            throw Error(LinkedServerRenderReason.JoinSpansServers, set,
                $"set server {tables[0].Server} and patient index server {pitTables[0].Server} differ; place the set and its patient index on one remote server, or make the set home");
        var placement = new SetPlacement(set, tables, join, pit, pitTables, kind);
        if (kind != SetPlacementKind.Home && placement.RemoteTables.Select(t => t.GetDatabaseRuntimeName()).Distinct(ServerComparer).Count() != 1)
            throw Error(LinkedServerRenderReason.MultipleRemoteDatabases, set,
                $"remote block on {placement.RemoteTables.FirstOrDefault()?.Server} spans databases {string.Join(", ", placement.RemoteTables.Select(t => t.Database))}; keep each remote set/PIT block in one database");
        return placement;
    }

    /// <summary>The enabled sets in cohort tree order, the order the container walk writes them.</summary>
    internal static IReadOnlyList<AggregateConfiguration> TreeOrder(CohortAggregateContainer root)
    {
        var sets = new List<AggregateConfiguration>();
        WriteContainer(root, set => { sets.Add(set); return ""; });
        return sets;
    }

    private static IEnumerable<AggregateConfiguration> Walk(CohortAggregateContainer container)
    {
        var children = EnabledChildren(container);
        if (container.IsDisabled || children.Length == 0)
            throw Error(LinkedServerRenderReason.EmptyContainer, container, "add an enabled set or disable the empty child container");
        // Core's CohortQueryBuilderResult.BuildFor takes the child provider's list and does a stable
        // OrderBy(Order). That list is produced by CatalogueChildProvider.GetChildren, which sorts by
        // ToString() (a tree display sort, not a contract; CatalogueChildProvider.cs:1614) over a set
        // built containers-first. Reproduce the same effective key: ToString, then Order, then
        // containers before sets.
        foreach (var child in children.OrderBy(c => c.ToString())
                     .ThenBy(c => c.Order).ThenBy(c => c is CohortAggregateContainer ? 0 : 1))
            if (child is CohortAggregateContainer sub)
                foreach (var set in Walk(sub)) yield return set;
            else if (child is AggregateConfiguration aggregate) yield return aggregate;
    }

    private static IOrderable[] EnabledChildren(CohortAggregateContainer container) => container.GetOrderedContents()
        .Where(c => c is not IDisableable { IsDisabled: true })
        .Where(c => c is not CohortAggregateContainer sub || UserSettings.StrictValidationForCohortBuilderContainers || sub.GetOrderedContents().Any())
        .ToArray();

    /// <summary>The container walk shared by both shapes: same parentheses, operators and indentation as core.
    /// <paramref name="leaf"/> gives a leaf's text, which must be a single line (tokens or projections).</summary>
    internal static string WriteContainer(CohortAggregateContainer container, Func<AggregateConfiguration, string> leaf, int tabs = 0)
    {
        var children = EnabledChildren(container);
        if (children.Length == 0) throw Error(LinkedServerRenderReason.EmptyContainer, container, "add an enabled set");
        var nl = Environment.NewLine;
        var sql = nl + CohortQueryBuilderHelper.TabIn("(", tabs) + nl;
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0)
            {
                var operation = container.Operation switch
                {
                    SetOperation.UNION => "UNION", SetOperation.INTERSECT => "INTERSECT", SetOperation.EXCEPT => "EXCEPT",
                    _ => throw new ArgumentOutOfRangeException(nameof(container.Operation))
                };
                sql += nl + CohortQueryBuilderHelper.TabIn(operation + nl + nl, tabs);
            }
            sql += children[i] is CohortAggregateContainer sub ? WriteContainer(sub, leaf, tabs + 1) :
                CohortQueryBuilderHelper.TabIn(leaf((AggregateConfiguration)children[i]), tabs);
        }
        return sql + nl + CohortQueryBuilderHelper.TabIn(")", tabs) + nl;
    }

    private static string SelectRemotePit(SetPlacement p, IQuerySyntaxHelper syntax)
    {
        if (p.Collation == null) return "SELECT * FROM " + p.Placeholder;
        var columns = p.Pit.AggregateDimensions.Select(d =>
        {
            var name = SqlServerOpenQueryWrapper.QuoteIdentifier(SqlServerResultColumnResolver.DimensionName(d), syntax);
            return name + (d.IsExtractionIdentifier ? p.Collation : "") + " AS " + name;
        }).ToList();
        if (!string.IsNullOrWhiteSpace(p.Pit.CountSQL))
        {
            syntax.SplitLineIntoSelectSQLAndAlias(p.Pit.CountSQL, out _, out var alias);
            if (string.IsNullOrWhiteSpace(alias))
                throw Error(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias, p.Pit, "give CountSQL an alias so the collation projection can name it");
            columns.Add(syntax.EnsureWrapped(alias));
        }
        return "SELECT " + string.Join(", ", columns) + " FROM " + p.Placeholder;
    }

    private static void Wrap(SetPlacement p, CohortQueryBuilderDependencySql block, List<string> warnings)
    {
        try
        {
            var fragment = SqlServerOpenQueryWrapper.Wrap(block, p.RemoteTables[0], p.RemoteBatchForm, p.ResultColumns);
            p.RemoteSql = fragment.Sql;
            warnings.AddRange(fragment.Warnings.Select(w => $"Set {p.Set}: {w}"));
        }
        catch (LinkedServerRenderException ex) { throw Error(ex.Reason, p.Set, ex.Message); }
    }

    private static JoinableCohortAggregateConfigurationUse GetJoin(AggregateConfiguration set)
    {
        var joins = set.PatientIndexJoinablesUsed.ToArray();
        if (joins.Length > 1) throw Error(LinkedServerRenderReason.MultiplePatientIndexTables, set, "use at most one patient index per set");
        return joins.SingleOrDefault();
    }

    private static void CheckApi(AggregateConfiguration set)
    {
        if (set.Catalogue.IsApiCall()) throw Error(LinkedServerRenderReason.ApiSetRequiresCache, set, "API sets require the core cached cohort build");
    }

    private static void CheckDatabase(ITableInfo table, bool home)
    {
        if (string.IsNullOrWhiteSpace(table.Database) || string.IsNullOrWhiteSpace(table.GetDatabaseRuntimeName()))
            throw Error(home ? LinkedServerRenderReason.HomeDatabaseUnknown : LinkedServerRenderReason.MultipleRemoteDatabases,
                table, $"no database on server {table.Server}; synchronise the table metadata");
    }

    private static ITableInfo[] Tables(AggregateConfiguration set) => set.Catalogue.GetTableInfoList(false).OrderBy(t => t.ID).ToArray();
    private static AggregateDimension Identifier(AggregateConfiguration set) => set.AggregateDimensions.Single(d => d.IsExtractionIdentifier);
    private static bool SameServer(ITableInfo a, ITableInfo b) => ServerComparer.Equals(a.Server?.Trim(), b.Server?.Trim()) && a.DatabaseType == b.DatabaseType;
    private static void ValidateGroup(AggregateConfiguration set, ITableInfo[] tables)
    {
        foreach (var table in tables)
            if (string.IsNullOrWhiteSpace(table.Server) || !SameServer(tables[0], table))
                throw Error(LinkedServerRenderReason.SetSpansServers, set,
                    $"table {table} has server '{table.Server}' ({table.DatabaseType}), compared with '{tables[0].Server}' ({tables[0].DatabaseType}); give every table in the set one server and DBMS");
    }

    internal static LinkedServerRenderException Error(LinkedServerRenderReason reason, object subject, string detail) => new(reason, $"{subject}: {detail}.");
}
