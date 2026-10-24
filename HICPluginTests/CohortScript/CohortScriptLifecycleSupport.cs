// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FAnsi;
using FAnsi.Discovery;
using NUnit.Framework;
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Checks;
using Rdmp.Core.Startup;
using RdmpCohortScript;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// The steps the lifecycle tests share: build a cohort on fake patient data, reopen it through fresh repositories,
/// export it, rebuild it, and compare every generation with answers worked out by hand. Each check returns its
/// problems as text, so a test of a deliberate fault can see which check caught it.
/// </summary>
public abstract partial class CohortScriptLifecycleBase : DatabaseTests
{
    /// <summary>The test database that holds the fake patient tables of the current test.</summary>
    protected DiscoveredDatabase Db { get; private set; }

    protected void StartWithACleanDatabase() => Db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);

    // ---- fresh repositories

    /// <summary>New repositories on the same platform, so nothing is read from objects already in memory.</summary>
    protected IRDMPPlatformRepositoryServiceLocator FreshLocator() =>
        new LinkedRepositoryProvider(((TableRepository)CatalogueRepository).ConnectionString,
            ((TableRepository)DataExportRepository).ConnectionString);

    protected static ConsoleInputManager ActivatorFor(IRDMPPlatformRepositoryServiceLocator locator) =>
        new(locator, ThrowImmediatelyCheckNotifier.Quiet) { DisallowInput = true };

    protected CohortIdentificationConfiguration Reopen(int cohortId) =>
        FreshLocator().CatalogueRepository.GetObjectByID<CohortIdentificationConfiguration>(cohortId);

    // ---- the round trip

    /// <summary>What the round trip found, and the cohorts it made, for tests that go on to change one of them.</summary>
    protected sealed record ChainResult(List<string> Problems, string Reference, int B1, DirectoryInfo Folder);

    /// <summary>
    /// B0, export E0, rebuild B1, export E1, rebuild B2 from E1, and two more rebuilds of E0. Every cohort is reopened
    /// through fresh repositories and checked against the expected rows and B0's saved structure. Objects that
    /// existed before the round trip, B0 among them, must be unchanged afterwards. Without <paramref name="allGenerations"/>
    /// the round trip stops after the fixed point, with one rebuild instead of four, which keeps a test short.
    /// </summary>
    protected ChainResult RoundTrip(int b0Id, string[] expectedRows, Func<string, List<string>> checkB0Structure = null,
        bool allGenerations = true)
    {
        var problems = new List<string>();
        var b0 = Reopen(b0Id);
        problems.AddRange(TiedOrders(b0));
        problems.AddRange(Verify("B0", b0Id, b0.Name, expectedRows, null));
        var reference = CohortSnapshot.Read(FreshLocator(), b0Id);
        if (checkB0Structure != null)
            problems.AddRange(checkB0Structure(reference));

        // made after B0, so an authored script's build does not meet a cohort with its own name
        MakeBystanders(b0.Name);
        var before = Inventory.Take(FreshLocator());

        var folder = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"rdmp-lifecycle-{Guid.NewGuid():N}"));
        var e0 = Export(b0Id, Path.Join(folder.FullName, "e0"));
        problems.AddRange(Check("E0", e0, authored: false));

        var b1 = RebuildAndVerify("B1", e0, $"{b0.Name} B1", expectedRows, reference, problems);
        var e1 = Export(b1, Path.Join(folder.FullName, "e1"));
        problems.AddRange(CompareScripts(e0, e1));
        if (allGenerations)
        {
            RebuildAndVerify("B2", e1, $"{b0.Name} B2", expectedRows, reference, problems);
            RebuildAndVerify("B1a", e0, $"{b0.Name} B1a", expectedRows, reference, problems);
            RebuildAndVerify("B1b", e0, $"{b0.Name} B1b", expectedRows, reference, problems);
        }

        problems.AddRange(Inventory.Changed(before, Inventory.Take(FreshLocator())).Select(c => $"bystander: {c}"));

        if (problems.Count == 0)
            folder.Delete(true);
        else
            problems.Add($"exported files kept in {folder.FullName}");
        return new ChainResult(problems, reference, b1, folder);
    }

    private int RebuildAndVerify(string label, FileInfo script, string name, string[] expectedRows, string reference,
        List<string> problems)
    {
        try
        {
            var id = Rebuild(script, name);
            problems.AddRange(Verify(label, id, name, expectedRows, reference));
            return id;
        }
        catch (Exception e)
        {
            problems.Add($"rebuild: {label} failed: {e.Message}");
            return -1;
        }
    }

    /// <summary>Reopens the cohort through fresh repositories and compares its name, rows and saved structure.</summary>
    protected List<string> Verify(string label, int cohortId, string expectedName, string[] expectedRows, string reference)
    {
        var problems = new List<string>();
        var locator = FreshLocator();
        var cic = locator.CatalogueRepository.GetObjectByID<CohortIdentificationConfiguration>(cohortId);
        if (cic.Name != expectedName)
            problems.Add($"name: {label} is called \"{cic.Name}\", not \"{expectedName}\".");

        try
        {
            var rows = Rows(cic);
            var expected = expectedRows.OrderBy(r => r, StringComparer.Ordinal).ToArray();
            if (!rows.SequenceEqual(expected))
                problems.Add($"rows: {label} returned [{string.Join(", ", rows)}], expected [{string.Join(", ", expected)}].");
        }
        catch (Exception e)
        {
            problems.Add($"rows: {label}'s query failed: {e.Message}");
        }

        if (reference != null)
        {
            var snapshot = CohortSnapshot.Read(locator, cohortId);
            if (snapshot != reference)
                problems.Add($"snapshot: {label}'s saved structure differs from B0's:{Environment.NewLine}{LineDiff(reference, snapshot)}");
        }

        return problems;
    }

    /// <summary>
    /// Every row RDMP's cohort query returns, with all its columns, sorted. Duplicates are kept, so a query that
    /// returns a patient twice does not pass for one that returns them once.
    /// </summary>
    protected string[] Rows(CohortIdentificationConfiguration cic)
    {
        var sql = new CohortQueryBuilder(cic, null).SQL;
        var rows = new List<string>();
        using var con = Db.Server.GetConnection();
        con.Open();
        using var cmd = Db.Server.GetCommand(sql, con);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        return rows.OrderBy(r => r, StringComparer.Ordinal).ToArray();
    }

    protected FileInfo Export(int cohortId, string folder)
    {
        var locator = FreshLocator();
        var cic = locator.CatalogueRepository.GetObjectByID<CohortIdentificationConfiguration>(cohortId);
        new ExecuteCommandExportCohortAsScript(ActivatorFor(locator), cic, new DirectoryInfo(folder)).Execute();
        return new FileInfo(Directory.GetFiles(folder, "build.script.yaml", SearchOption.AllDirectories).Single());
    }

    protected List<string> Check(string label, FileInfo script, bool authored)
    {
        try
        {
            new ExecuteCommandCheckCohortScript(ActivatorFor(FreshLocator()), script, authored).Execute();
            return new List<string>();
        }
        catch (Exception e)
        {
            return new List<string> { $"check: {label} did not pass the check: {e.Message}" };
        }
    }

    protected int Rebuild(FileInfo script, string name)
    {
        var locator = FreshLocator();
        new ExecuteCommandBuildCohortFromScript(ActivatorFor(locator), script, name).Execute();
        return locator.CatalogueRepository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single().ID;
    }

    /// <summary>Builds a cohort from a script written by hand, as a person or an agent would.</summary>
    protected int BuildAuthored(string name, params string[] lines)
    {
        var file = new FileInfo(Path.Join(Path.GetTempPath(), $"rdmp-lifecycle-{Guid.NewGuid():N}.yaml"));
        File.WriteAllText(file.FullName, AuthoredScript(lines));
        try
        {
            var problems = Check("the authored script", file, authored: true);
            Assert.That(problems, Is.Empty, "The authored script should pass the check.");
            return Rebuild(file, name);
        }
        finally
        {
            file.Delete();
        }
    }

    protected static string AuthoredScript(IEnumerable<string> lines) =>
        "cohort-script-format: \"1\"\nCommands:\n" + string.Concat(lines.Select(l => $"  - {YamlText.DoubleQuoted(l)}\n"));

    /// <summary>
    /// Compares two exports after removing what differs on purpose between generations: the header's cohort id and
    /// name, the name on the create line, and handles built from object ids. Nothing else is changed, so a
    /// number in SQL text or a comment still counts.
    /// </summary>
    protected static List<string> CompareScripts(FileInfo first, FileInfo second)
    {
        var a = ScriptNormalizer.Normalize(File.ReadAllText(first.FullName));
        var b = ScriptNormalizer.Normalize(File.ReadAllText(second.FullName));
        return a == b
            ? new List<string>()
            : new List<string> { $"fixed point: the export of B1 differs from the export of B0:{Environment.NewLine}{LineDiff(a, b)}" };
    }

    protected static string LineDiff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in e.Except(a).Take(15))
            sb.AppendLine($"  - {line}");
        foreach (var line in a.Except(e).Take(15))
            sb.AppendLine($"  + {line}");
        if (sb.Length == 0)
            sb.AppendLine("  (the same lines in a different order)");
        return sb.ToString();
    }

    // ---- objects that must not change

    /// <summary>A cohort with the same name, an unrelated cohort with a parameter, and a project linked to it.</summary>
    protected void MakeBystanders(string name)
    {
        var sameName = new CohortIdentificationConfiguration(CatalogueRepository, name);
        sameName.CreateRootContainerIfNotExists();
        var unrelated = new CohortIdentificationConfiguration(CatalogueRepository, $"Unrelated {Guid.NewGuid():N}");
        unrelated.CreateRootContainerIfNotExists();
        new AnyTableSqlParameter(CatalogueRepository, unrelated, "DECLARE @since AS date") { Value = "'1999-01-01'" }.SaveToDatabase();
        var project = new Project(DataExportRepository, $"Bystander project {Guid.NewGuid():N}") { ProjectNumber = NextProjectNumber() };
        project.SaveToDatabase();
        _ = new ProjectCohortIdentificationConfigurationAssociation(DataExportRepository, project, unrelated);
    }

    private int _projectNumber = 9000;
    protected int NextProjectNumber() => ++_projectNumber;

    // ---- building fixtures

    protected ICatalogue Import(string name, (string Column, Type Type)[] columns, IEnumerable<object[]> rows,
        params string[] identifiers)
    {
        var dt = new DataTable(name);
        foreach (var (column, type) in columns)
            dt.Columns.Add(column, type);
        foreach (var row in rows)
            dt.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());

        var cata = Import(Db.CreateTable(name, dt));
        foreach (var ei in cata.GetAllExtractionInformation(ExtractionCategory.Any)
                     .Where(e => identifiers.Append("chi").Contains(e.GetRuntimeName(), StringComparer.OrdinalIgnoreCase)))
        {
            ei.IsExtractionIdentifier = true;
            ei.SaveToDatabase();
        }

        return cata;
    }

    protected static readonly (string, Type) Chi = ("chi", typeof(string));

    protected ICatalogue Patients(string name, IEnumerable<int> patients) =>
        Import(name, new[] { Chi }, patients.Select(p => new object[] { P(p) }));

    protected AggregateConfiguration AddSet(CohortIdentificationConfiguration cic, CohortAggregateContainer container,
        ICatalogue cata, int order, string identifier = "chi")
    {
        var set = new AggregateConfiguration(CatalogueRepository, cata, cata.Name) { CountSQL = null };
        set.SaveToDatabase();
        var column = cata.GetAllExtractionInformation(ExtractionCategory.Any)
            .Single(e => e.GetRuntimeName().Equals(identifier, StringComparison.OrdinalIgnoreCase));
        _ = new AggregateDimension(CatalogueRepository, column, set);
        cic.EnsureNamingConvention(set);
        container.AddChild(set, order);

        var fc = new AggregateFilterContainer(CatalogueRepository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        return set;
    }

    protected AggregateFilter AddFilter(AggregateFilterContainer container, string name, string whereSql)
    {
        var filter = new AggregateFilter(CatalogueRepository, name, container) { WhereSQL = whereSql };
        filter.SaveToDatabase();
        return filter;
    }

    protected AggregateFilterContainer AddFilterContainer(AggregateFilterContainer parent, FilterContainerOperation operation)
    {
        var child = new AggregateFilterContainer(CatalogueRepository, operation);
        parent.AddChild(child);
        return child;
    }

    protected CohortAggregateContainer AddContainer(CohortAggregateContainer parent, SetOperation operation, string name, int order)
    {
        var child = new CohortAggregateContainer(CatalogueRepository, operation) { Name = name, Order = order };
        child.SaveToDatabase();
        parent.AddChild(child);
        return child;
    }

    /// <summary>A patient index table on the catalogue, with the identifier and the given columns.</summary>
    protected JoinableCohortAggregateConfiguration AddPatientIndexTable(CohortIdentificationConfiguration cic,
        ICatalogue cata, params string[] columns)
    {
        var table = new AggregateConfiguration(CatalogueRepository, cata, cata.Name) { CountSQL = null };
        table.SaveToDatabase();
        foreach (var ei in cata.GetAllExtractionInformation(ExtractionCategory.Any)
                     .Where(e => columns.Append("chi").Contains(e.GetRuntimeName(), StringComparer.OrdinalIgnoreCase)))
            _ = new AggregateDimension(CatalogueRepository, ei, table);
        cic.EnsureNamingConvention(table);
        return new JoinableCohortAggregateConfiguration(CatalogueRepository, cic, table);
    }

    protected static void Join(JoinableCohortAggregateConfiguration joinable, AggregateConfiguration set)
    {
        var use = joinable.AddUser(set);
        use.JoinType = ExtractionJoinType.Inner;
        use.SaveToDatabase();
    }

    /// <summary>
    /// Saves each child's position as its order. RDMP's AddChild makes room at the order the child had before, not
    /// at the order it is given, so two sets added at 0 and 1 both end up at 1. The fixtures set every order once
    /// all children are in place.
    /// </summary>
    protected static void SetOrders(params IOrderable[] children)
    {
        for (var i = 0; i < children.Length; i++)
        {
            children[i].Order = i;
            ((ISaveable)children[i]).SaveToDatabase();
        }
    }

    /// <summary>Containers of the cohort in which two children have the same order, so their order is not fixed.</summary>
    protected static IEnumerable<string> TiedOrders(CohortIdentificationConfiguration cic) =>
        cic.RootCohortAggregateContainer.GetAllSubContainersRecursively().Append(cic.RootCohortAggregateContainer)
            .Where(c => c.GetOrderedContents().GroupBy(o => o.Order).Any(g => g.Count() > 1))
            .Select(c => $"orders: two children of container \"{c.Name}\" have the same order, so their order is not fixed.");

    /// <summary>Swaps the saved order of the first two children of the root container, and saves them.</summary>
    protected void SwapTheFirstTwoChildren(int cohortId)
    {
        var root = Reopen(cohortId).RootCohortAggregateContainer;
        var children = root.GetOrderedContents().ToArray();
        (children[0].Order, children[1].Order) = (children[1].Order, children[0].Order);
        foreach (var child in children.Cast<ISaveable>())
            child.SaveToDatabase();
    }

    protected static string P(int i) => $"P{i:00}";

    protected static string[] Ps(params int[] patients) => patients.Select(P).ToArray();

    protected static string Unique(string tag) => $"lc_{tag}_{Guid.NewGuid():N}"[..20];
}

/// <summary>
/// The saved structure of a cohort, read from the repositories as plain text, one object per line. It is read
/// directly, not through the export writer, so it does not repeat the writer's omissions. Ids of objects that
/// every rebuild makes again are left out; ids of shared objects (catalogues, columns, tables, projects) stay.
/// </summary>
internal static class CohortSnapshot
{
    public static string Read(IRDMPPlatformRepositoryServiceLocator locator, int cohortId)
    {
        var repository = locator.CatalogueRepository;
        var cic = repository.GetObjectByID<CohortIdentificationConfiguration>(cohortId);
        var joinables = cic.GetAllJoinables();
        var lines = new List<string>();

        string SetName(string name) => Regex.Replace(name ?? "", $"^cic_{cic.ID}_", "cic_{cohort}_");
        string TableName(int joinableId) =>
            SetName(joinables.Single(j => j.ID == joinableId).AggregateConfiguration.Name);
        string Sql(string sql) => sql == null
            ? "<null>"
            : joinables.Aggregate(sql, (s, j) => Regex.Replace(s, $@"\bix{j.ID}\b", $"ix{{{TableName(j.ID)}}}"));

        lines.Add($"cohort description={Text(cic.Description)} ticket={Text(cic.Ticket)} folder={Text(cic.Folder)}");
        foreach (var parameter in cic.GetAllParameters().OrderBy(p => p.ParameterName, StringComparer.Ordinal))
            lines.Add($"  parameter {Parameter(parameter, Sql)}");
        foreach (var link in locator.DataExportRepository
                     .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", cic.ID)
                     .OrderBy(l => l.Project_ID))
            lines.Add($"  project {link.Project_ID}");

        foreach (var joinable in joinables.OrderBy(j => TableName(j.ID), StringComparer.Ordinal))
        {
            lines.Add("  patient index table");
            Aggregate(joinable.AggregateConfiguration, "    ");
        }

        Container(cic.RootCohortAggregateContainer, "  ");
        return string.Join("\n", lines);

        void Container(CohortAggregateContainer container, string indent)
        {
            lines.Add($"{indent}container {container.Operation} name={Text(container.Name)} order={container.Order} disabled={container.IsDisabled}");
            foreach (var content in container.GetOrderedContents())
                switch (content)
                {
                    case AggregateConfiguration set:
                        Aggregate(set, indent + "  ");
                        break;
                    case CohortAggregateContainer child:
                        Container(child, indent + "  ");
                        break;
                }
        }

        void Aggregate(AggregateConfiguration a, string indent)
        {
            lines.Add($"{indent}set {SetName(a.Name)} catalogue={a.Catalogue_ID} order={(a.GetCohortAggregateContainerIfAny() == null ? "-" : a.Order.ToString())} disabled={a.IsDisabled} description={Text(a.Description)} count={Sql(a.CountSQL)} having={Sql(a.HavingSQL)}");
            foreach (var d in a.AggregateDimensions.OrderBy(d => d.ExtractionInformation_ID))
                lines.Add($"{indent}  column {d.ExtractionInformation_ID} select={Sql(d.SelectSQL)} alias={Text(d.Alias)} groupby={d.GroupBy} identifier={d.IsExtractionIdentifier}");
            if (a.GetTopXIfAny() is { } topX)
                lines.Add($"{indent}  top {topX.TopX} by={topX.OrderByDimensionIfAny?.ExtractionInformation_ID.ToString() ?? "count"} {topX.OrderByDirection}");
            foreach (var table in a.ForcedJoins.OrderBy(t => t.ID))
                lines.Add($"{indent}  forced join to table {table.ID}");
            foreach (var parameter in repository.GetAllObjects<AnyTableSqlParameter>()
                         .Where(p => p.ReferencedObjectType == nameof(AggregateConfiguration) && p.ReferencedObjectID == a.ID)
                         .OrderBy(p => p.ParameterName, StringComparer.Ordinal))
                lines.Add($"{indent}  parameter {Parameter(parameter, Sql)}");
            foreach (var use in a.PatientIndexJoinablesUsed.OrderBy(u => TableName(u.JoinableCohortAggregateConfiguration_ID), StringComparer.Ordinal))
                lines.Add($"{indent}  joins {TableName(use.JoinableCohortAggregateConfiguration_ID)} {use.JoinType}");
            if (a.RootFilterContainer is AggregateFilterContainer root)
                lines.AddRange(FilterContainer(root, indent + "  ", Sql));
        }
    }

    private static List<string> FilterContainer(AggregateFilterContainer container, string indent, Func<string, string> sql)
    {
        var lines = new List<string> { $"{indent}filters {container.Operation} disabled={container.IsDisabled}" };
        foreach (var filter in container.GetFilters().Cast<AggregateFilter>().OrderBy(f => f.Order).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            lines.Add($"{indent}  filter {Text(filter.Name)} order={filter.Order} where={sql(filter.WhereSQL)} disabled={filter.IsDisabled} description={Text(filter.Description)} source={filter.ClonedFromExtractionFilter_ID?.ToString() ?? "-"}");
            foreach (var parameter in filter.GetAllParameters().OrderBy(p => p.ParameterName, StringComparer.Ordinal))
                lines.Add($"{indent}    parameter {Parameter(parameter, sql)}");
        }

        // a sub-container has no order of its own, so the children are sorted by what they hold
        foreach (var block in container.GetSubContainers().Cast<AggregateFilterContainer>()
                     .Select(c => string.Join("\n", FilterContainer(c, indent + "  ", sql)))
                     .OrderBy(b => b, StringComparer.Ordinal))
            lines.Add(block);
        return lines;
    }

    private static string Parameter(ISqlParameter p, Func<string, string> sql) =>
        $"{p.ParameterName} declared={sql(p.ParameterSQL)} value={sql(p.Value)} comment={Text(p.Comment)}";

    private static string Text(string s) => s == null ? "<null>" : $"\"{s.Replace("\r", "\\r").Replace("\n", "\\n")}\"";
}

/// <summary>
/// The saved fields of every cohort-related and catalogue object on the platform, keyed by type and id, with the
/// links between them that are not fields: which container holds a set, and the forced joins. A check before and
/// after a run finds an object that the run changed or deleted, which a count of rows cannot.
/// </summary>
internal static class Inventory
{
    private static readonly Type[] CatalogueTypes =
    {
        typeof(CohortIdentificationConfiguration), typeof(CohortAggregateContainer), typeof(AggregateConfiguration),
        typeof(AggregateDimension), typeof(AggregateFilterContainer), typeof(AggregateFilter), typeof(AggregateFilterParameter),
        typeof(AnyTableSqlParameter), typeof(JoinableCohortAggregateConfiguration), typeof(JoinableCohortAggregateConfigurationUse),
        typeof(AggregateTopX), typeof(Catalogue), typeof(CatalogueItem), typeof(ExtractionInformation), typeof(ExtractionFilter),
        typeof(ExtractionFilterParameter), typeof(TableInfo), typeof(ColumnInfo)
    };

    private static readonly Type[] DataExportTypes = { typeof(Project), typeof(ProjectCohortIdentificationConfigurationAssociation) };

    public static Dictionary<string, string> Take(IRDMPPlatformRepositoryServiceLocator locator)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in CatalogueTypes)
            foreach (var o in locator.CatalogueRepository.GetAllObjects(type))
                all[$"{type.Name} {o.ID}"] = Fields(o) + Links(locator, o);
        foreach (var type in DataExportTypes)
            foreach (var o in locator.DataExportRepository.GetAllObjects(type))
                all[$"{type.Name} {o.ID}"] = Fields(o);
        return all;
    }

    /// <summary>Objects of <paramref name="before"/> that are gone or have different fields now.</summary>
    public static IEnumerable<string> Changed(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        foreach (var (key, fields) in before)
            if (!after.TryGetValue(key, out var now))
                yield return $"{key} was deleted.";
            else if (now != fields)
                yield return $"{key} changed from {fields} to {now}.";
    }

    /// <summary>Objects of <paramref name="after"/> that did not exist in <paramref name="before"/>.</summary>
    public static IEnumerable<string> Added(Dictionary<string, string> before, Dictionary<string, string> after) =>
        after.Keys.Except(before.Keys).OrderBy(k => k, StringComparer.Ordinal);

    private static string Fields(IMapsDirectlyToDatabaseTable o)
    {
        var sb = new StringBuilder();
        foreach (var property in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && IsPlain(p.PropertyType))
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            string value;
            try
            {
                value = Convert.ToString(property.GetValue(o), CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                value = $"<{e.GetBaseException().GetType().Name}>";
            }

            sb.Append(property.Name).Append('=').Append(value).Append("; ");
        }

        return sb.ToString();
    }

    private static string Links(IRDMPPlatformRepositoryServiceLocator locator, IMapsDirectlyToDatabaseTable o) => o switch
    {
        AggregateConfiguration a =>
            $"in container {a.GetCohortAggregateContainerIfAny()?.ID.ToString() ?? "-"}; forced joins " +
            string.Join(",", locator.CatalogueRepository.AggregateForcedJoinManager.GetAllForcedJoinsFor(a).Select(t => t.ID).OrderBy(i => i)),
        CohortAggregateContainer c => $"in container {c.GetParentContainerIfAny()?.ID.ToString() ?? "-"}",
        AggregateFilterContainer f => $"in container {f.GetParentContainerIfAny()?.ID.ToString() ?? "-"}",
        _ => ""
    };

    private static bool IsPlain(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(DateTime) || type == typeof(decimal);
    }
}

/// <summary>Removes from an export what differs on purpose between generations, and nothing else.</summary>
internal static class ScriptNormalizer
{
    // handles are named after object ids: $cic12, $c40, $a7, $a7.chi, $j3, $fc9, $f15, and the reserved $cx1
    private static readonly Regex Handle = new(@"\$(cic|cx|fc|c|a|j|f)\d+\b", RegexOptions.CultureInvariant);

    private static readonly Regex CreateName = new(@"CreateNewCohortIdentificationConfiguration \\"".*?\\"" =>",
        RegexOptions.CultureInvariant);

    public static string Normalize(string document)
    {
        var handles = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var line in document.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("  cohort-id: ", StringComparison.Ordinal))
                lines.Add("  cohort-id: {cohort}");
            else if (line.StartsWith("  cohort-name: ", StringComparison.Ordinal))
                lines.Add("  cohort-name: {cohort}");
            else
            {
                var named = CreateName.Replace(line, "CreateNewCohortIdentificationConfiguration \\\"{cohort}\\\" =>");
                lines.Add(Handle.Replace(named, m =>
                {
                    if (!handles.TryGetValue(m.Value, out var renamed))
                        handles[m.Value] = renamed = $"${m.Groups[1].Value}#{handles.Count + 1}";
                    return renamed;
                }));
            }
        }

        return string.Join("\n", lines);
    }
}
