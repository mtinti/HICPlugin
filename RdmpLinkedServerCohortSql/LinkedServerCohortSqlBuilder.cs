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

    /// <summary>Writes the cohort as the staged script (to run) or as the script for inspection only (one statement).</summary>
    public LinkedServerCohortSqlResult Build(CohortIdentificationConfiguration cic, bool staged)
    {
        var rendered = Render(cic, staged);
        return staged ? StagedScriptWriter.Write(rendered) : CompositeScriptWriter.Write(rendered);
    }

    /// <summary>
    /// Checks the cohort, decides where each set runs, and writes the SQL of each set. Both scripts start from this result.
    /// With <paramref name="staged"/>, the checks that only the staged script needs also run, before any SQL is written.
    /// </summary>
    internal RenderedCohort Render(CohortIdentificationConfiguration cic, bool staged)
    {
        ArgumentNullException.ThrowIfNull(cic);
        var root = cic.RootCohortAggregateContainer;
        if (root == null) throw Error(LinkedServerRenderReason.EmptyContainer, cic,
            "The cohort has no sets yet (it has no top-level UNION, INTERSECT or EXCEPT container).", "Add a set first.");
        var sets = Walk(root).OrderBy(s => s.Order).ToArray();
        // Check API sets and patient index tables first: RDMP's query builder can load plugin code when it writes SQL.
        foreach (var set in sets)
        {
            CheckApi(set);
            var join = GetJoin(set);
            var pit = join?.JoinableCohortAggregateConfiguration.AggregateConfiguration;
            if (pit == null) continue;
            CheckApi(pit);
            if (pit.PatientIndexJoinablesUsed.Any())
                throw Error(LinkedServerRenderReason.NestedPatientIndexTable, set,
                    $"It uses patient index table {pit}, which itself uses another patient index table. This plugin supports only one level.",
                    $"Change {pit} so that it does not use another patient index table, or use RDMP's cached build for this cohort.");
        }

        var first = sets.FirstOrDefault(s => s.Catalogue.GetTableInfoList(false).Any());
        if (first == null)
        {
            // RDMP's query builder gives a more precise error for a set without tables, so let it speak first.
            foreach (var set in sets) CohortQueryBuilderHelper.GetSQLForAggregate(set, new QueryBuilderArgs(null, cic.GetAllParameters()));
            throw Error(LinkedServerRenderReason.EmptyContainer, cic,
                "None of the cohort's enabled sets reads a table that RDMP's catalogue knows.", "Add or enable a set that reads a table.");
        }
        var home = Tables(first).First();
        if (home.DatabaseType != DatabaseType.MicrosoftSQLServer)
            throw Error(LinkedServerRenderReason.UnsupportedHomeDatabaseType, first,
                $"The home server {home.Server}, where the script would run, is a {home.DatabaseType} server. This plugin writes SQL only for Microsoft SQL Server.");
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

        // Each query on a linked server uses the USE form when it can. That form switches to the right database itself,
        // so it works whatever the link login's default database is, and that default is often master. The plugin falls
        // back to the checked form only when it cannot describe the set's columns. The checked form stops with an error,
        // rather than running in the wrong database.
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
                warnings.Add($"{Capitalise(Describe(placement.RemoteAggregate))}: the plugin could not describe its columns. {ex.Detail} " +
                             $"So the query on linked server {placement.RemoteTables[0].Server.Trim()} runs in the login's default database, " +
                             $"which must be {placement.RemoteTables[0].GetDatabaseRuntimeName()}. If it is not, the query stops with an error that names both databases.");
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
                // RDMP joins the set to its patient index table by the identifier's name, so the identifier needs a name
                // even when the patient index table is read with SELECT *.
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

    /// <summary>RDMP writes TOP X without ORDER BY, so SQL Server may keep different rows of such a set on each run.</summary>
    private static IEnumerable<string> TopXWarnings(SetPlacement[] placements) => placements
        .SelectMany(p => p.Pit == null ? new[] { p.Set } : new[] { p.Set, p.Pit }).Distinct()
        .Select(a => (Aggregate: a, TopX: a.GetTopXIfAny())).Where(x => x.TopX != null)
        .Select(x => $"{Capitalise(Describe(x.Aggregate))} keeps only {x.TopX.TopX} rows (TOP {x.TopX.TopX}) but has no ORDER BY, so SQL Server " +
                     "may pick different rows on each run. The patients in this set, and so in the cohort, can differ between runs, " +
                     "between the two scripts and from the cached build.");

    /// <summary>
    /// The checks that only the staged script needs, run before any SQL is written. The staged script copies each set into a
    /// temp table with SELECT INTO, so every column needs a name. It then reads the columns back by position, so it needs
    /// RDMP's exact column order. For sets on the home server, each column must also keep its collation in the temp table.
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
                        "The staged script needs the exact order of this set's columns. " + orderProblem, InspectionHint);
                CheckNames(placement.Set, columns);
                var resolver = new SqlServerResultColumnResolver(placement.Set);
                foreach (var column in columns)
                {
                    var problem = resolver.HomeCollationProblem(column);
                    if (problem == null) continue;
                    if (SqlServerResultColumnResolver.HasInnerCollate(column.Expression))
                        throw Error(LinkedServerRenderReason.StagedCollationUnknown, placement.Set,
                            $"Column {column.Expression} has a COLLATE inside the expression, and a temp table does not keep its effect. " +
                            "Adding another COLLATE at the end does not help.",
                            "Use RDMP's cached build for this cohort. The plugin's documentation explains the options.");
                    throw Error(LinkedServerRenderReason.StagedCollationUnknown, placement.Set,
                        $"The plugin cannot verify that column {column.Expression} compares the same way after it is copied into a temp table. {problem}",
                        "Add COLLATE <collation name> at the end of the column's SQL, using the collation of the columns it is compared with. " +
                        InspectionHint);
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
                $"Column {unnamed.Expression} has no name. The staged script copies each set into a temp table, and every column of a table needs a name.",
                $"Give the column an alias, for example {unnamed.Expression} AS name.");
        var duplicate = columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw Error(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias, aggregate,
                $"{duplicate.Count()} columns are named {duplicate.Key} (SQL Server ignores case in column names). A temp table cannot have two columns with the same name.",
                "Give them different aliases.");
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
                    $"Table {table} is on server {table.Server}, which is a {table.DatabaseType} server. This plugin reads only from Microsoft SQL Server.");
            CheckDatabase(table, SameServer(table, home));
        }
        if (!atHome && pitTables.Length > 0 && !SameServer(tables[0], pitTables[0]))
            throw Error(LinkedServerRenderReason.JoinSpansServers, set,
                $"The set is on server {tables[0].Server}, but its patient index table is on server {pitTables[0].Server}.",
                $"Put both on the same server: the home server {home.Server}, or one linked server.");
        var placement = new SetPlacement(set, tables, join, pit, pitTables, kind);
        if (kind != SetPlacementKind.Home && placement.RemoteTables.Select(t => t.GetDatabaseRuntimeName()).Distinct(ServerComparer).Count() != 1)
            throw Error(LinkedServerRenderReason.MultipleRemoteDatabases, set,
                $"{(pit == null ? "This set reads" : "This set and its patient index table read")} tables from more than one database on linked server " +
                $"{placement.RemoteTables.FirstOrDefault()?.Server} ({string.Join(", ", placement.RemoteTables.Select(t => t.Database))}). This plugin " +
                "sends one query per set to a linked server, and that query uses one database. This is a limit of the plugin, not of SQL Server.",
                "Use RDMP's cached build for this cohort.");
        return placement;
    }

    /// <summary>The enabled sets in the order the cohort tree combines them, which is the order <see cref="WriteContainer"/> writes them.</summary>
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
            throw Error(LinkedServerRenderReason.EmptyContainer, container, "It has no enabled set.",
                "Add or enable a set, or disable the empty container.");
        // RDMP orders the sets in two steps, and the plugin repeats both. RDMP's tree lists a container's children sorted by
        // display name, containers first (CatalogueChildProvider.GetChildren). RDMP's query builder then sorts that list by
        // Order, keeping the earlier order for equal values. The result is: display name, then Order, then containers first.
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

    /// <summary>Writes the cohort tree with the same brackets, set operations and indentation as RDMP's query builder. Both
    /// scripts use it. <paramref name="setText"/> gives the text for one set, which must be a single line, because this method
    /// indents it.</summary>
    internal static string WriteContainer(CohortAggregateContainer container, Func<AggregateConfiguration, string> setText, int tabs = 0)
    {
        var children = EnabledChildren(container);
        if (children.Length == 0)
            throw Error(LinkedServerRenderReason.EmptyContainer, container, "It has no enabled set.", "Add or enable a set, or disable the empty container.");
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
            sql += children[i] is CohortAggregateContainer sub ? WriteContainer(sub, setText, tabs + 1) :
                CohortQueryBuilderHelper.TabIn(setText((AggregateConfiguration)children[i]), tabs);
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
                throw Error(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias, p.Pit,
                    "Its count column has no alias. The script must name every column when it adds COLLATE to the patient identifier.",
                    "Give the count an alias, for example COUNT(*) AS n.");
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
        catch (LinkedServerRenderException ex) { throw ex.About(Describe(p.RemoteAggregate)); }
    }

    private static JoinableCohortAggregateConfigurationUse GetJoin(AggregateConfiguration set)
    {
        var joins = set.PatientIndexJoinablesUsed.ToArray();
        if (joins.Length > 1)
            throw Error(LinkedServerRenderReason.MultiplePatientIndexTables, set,
                $"It joins {joins.Length} patient index tables. This plugin supports one patient index table per set.", "Use RDMP's cached build for this cohort.");
        return joins.SingleOrDefault();
    }

    private static void CheckApi(AggregateConfiguration set)
    {
        if (set.Catalogue.IsApiCall())
            throw Error(LinkedServerRenderReason.ApiSetRequiresCache, set, "It reads from an API, not from a table.",
                "Build this cohort with RDMP's normal cached build.");
    }

    private static void CheckDatabase(ITableInfo table, bool home)
    {
        if (string.IsNullOrWhiteSpace(table.Database) || string.IsNullOrWhiteSpace(table.GetDatabaseRuntimeName()))
            throw Error(home ? LinkedServerRenderReason.HomeDatabaseUnknown : LinkedServerRenderReason.RemoteDatabaseUnknown, table,
                $"RDMP does not record which database this table on server {table.Server} is in.",
                "Synchronise the table in RDMP so its database is recorded.");
    }

    private static ITableInfo[] Tables(AggregateConfiguration set) => set.Catalogue.GetTableInfoList(false).OrderBy(t => t.ID).ToArray();
    private static AggregateDimension Identifier(AggregateConfiguration set) => set.AggregateDimensions.Single(d => d.IsExtractionIdentifier);
    private static bool SameServer(ITableInfo a, ITableInfo b) => ServerComparer.Equals(a.Server?.Trim(), b.Server?.Trim()) && a.DatabaseType == b.DatabaseType;
    private static void ValidateGroup(AggregateConfiguration set, ITableInfo[] tables)
    {
        foreach (var table in tables)
        {
            if (string.IsNullOrWhiteSpace(table.Server))
                throw Error(LinkedServerRenderReason.ServerUnknown, set, $"RDMP does not record a server for its table {table}.",
                    "Set the table's server in RDMP.");
            if (!SameServer(tables[0], table))
                throw Error(LinkedServerRenderReason.SetSpansServers, set,
                    $"Its tables are on different servers: {table} is on {table.Server} ({table.DatabaseType}), but {tables[0]} is on " +
                    $"{tables[0].Server} ({tables[0].DatabaseType}).", "Put every table of a set on one server.");
        }
    }

    /// <summary>Points to the script for inspection, which still shows the cohort's SQL when the staged script cannot.</summary>
    private static string InspectionHint =>
        $"To see this cohort's SQL, open \"{ExecuteCommandViewCohortLinkedServerSql.Composite}\"; that script is for inspection only.";

    internal static LinkedServerRenderException Error(LinkedServerRenderReason reason, object subject, string problem, string fix = null) =>
        new(reason, problem, fix, Describe(subject));

    /// <summary>Names what a message is about, as the user sees it in RDMP: "set X", "patient index table X", "cohort X".</summary>
    internal static string Describe(object subject) => subject switch
    {
        AggregateConfiguration a => (a.IsJoinablePatientIndexTable() ? "patient index table " : "set ") + a,
        CohortIdentificationConfiguration c => "cohort " + c.Name,
        CohortAggregateContainer c => "container " + c,
        ITableInfo t => "table " + t,
        _ => subject?.ToString()
    };

    internal static string Capitalise(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
