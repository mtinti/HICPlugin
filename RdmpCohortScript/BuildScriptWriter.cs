// Writes a cohort as a format 1 build script (COHORT-SCRIPT-FORMAT.md). The lines follow the order of section 8:
// the create line, project links, cohort settings and parameters, patient index tables, the tree from the root
// down with each container's order lines after its children, deletes of unused automatic containers, and the
// disabled flags last. Text is written exactly as RDMP stores it; the only change is the {$j} marker for the
// ix<joinable id> alias in SQL, which the rebuild replaces with the new id.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.Repositories;

namespace RdmpCohortScript;

/// <summary>Raised when a cohort has something a build script cannot carry.</summary>
public sealed class CohortScriptExportException : Exception
{
    public CohortScriptRefusal Reason { get; }

    public CohortScriptExportException(CohortScriptRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }
}

/// <summary>Writes one cohort as a format 1 build script.</summary>
internal sealed class BuildScriptWriter
{
    public const string FormatVersion = "1";
    private const string DefaultFolder = "\\";

    private readonly CohortIdentificationConfiguration _cic;
    private readonly IRDMPPlatformRepositoryServiceLocator _locator;
    private readonly HashSet<string> _joinableIds;
    private readonly List<string> _lines = new();
    private readonly Dictionary<int, string> _comments = new();
    private readonly List<string> _disabled = new();
    private readonly HashSet<string> _handles = new();

    public BuildScriptWriter(CohortIdentificationConfiguration cic, IRDMPPlatformRepositoryServiceLocator locator)
    {
        _cic = cic;
        _locator = locator;
        _joinableIds = cic.GetAllJoinables().Select(j => j.ID.ToString()).ToHashSet();
    }

    /// <summary>The script lines, without the YAML around them.</summary>
    public IReadOnlyList<string> Lines()
    {
        if (_lines.Count == 0)
            Build();
        return _lines;
    }

    /// <summary>The whole build.script.yaml document.</summary>
    public string Document()
    {
        var sb = new StringBuilder();
        sb.Append("cohort-script-format: ").AppendLine(YamlText.DoubleQuoted(FormatVersion));
        sb.AppendLine("exported-from:");
        sb.Append("  cohort-id: ").AppendLine(YamlText.DoubleQuoted(_cic.ID.ToString()));
        sb.Append("  cohort-name: ").AppendLine(YamlText.DoubleQuoted(_cic.Name));
        sb.Append("  rdmp-version: ").AppendLine(YamlText.DoubleQuoted(VersionOf(typeof(Catalogue).Assembly)));
        sb.Append("  plugin-version: ").AppendLine(YamlText.DoubleQuoted(VersionOf(typeof(BuildScriptWriter).Assembly)));
        sb.AppendLine("Commands:");
        var lines = Lines();
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append("  - ").Append(YamlText.DoubleQuoted(lines[i]));
            if (_comments.TryGetValue(i, out var comment))
                sb.Append("   # ").Append(comment.Replace('\n', ' ').Replace('\r', ' '));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void Build()
    {
        var root = _cic.RootCohortAggregateContainer ??
                   throw new CohortScriptExportException(CohortScriptRefusal.NoRootContainer,
                       $"Cohort {_cic.ID} \"{_cic.Name}\" has no root container, so there is nothing to export.");

        // RDMP makes a root and two child containers with every new cohort. The root's first two child
        // containers take their place, by position; a missing one is deleted after the tree is built.
        var rootChildren = root.GetOrderedContents().OfType<CohortAggregateContainer>().ToArray();
        var automatic = new[] { rootChildren.ElementAtOrDefault(0), rootChildren.ElementAtOrDefault(1) };
        var automaticHandles = new[]
        {
            automatic[0] != null ? Handle("c", automatic[0].ID) : Reserve("$cx1"),
            automatic[1] != null ? Handle("c", automatic[1].ID) : Reserve("$cx2")
        };

        var cicHandle = Handle("cic", _cic.ID);
        var rootHandle = Handle("c", root.ID);
        Add("CreateNewCohortIdentificationConfiguration", new[] { ScriptLine.QuoteText(_cic.Name) },
            cicHandle, rootHandle, automaticHandles[0], automaticHandles[1]);

        foreach (var project in LinkedProjects())
            Add("LinkCohortToProject", new[]
            {
                $"CohortIdentificationConfiguration:{cicHandle}", $"Project:{project.ID}", ScriptLine.QuoteText(project.Name)
            });

        SetIfGiven($"CohortIdentificationConfiguration:{cicHandle}", "Description", _cic.Description);
        SetIfGiven($"CohortIdentificationConfiguration:{cicHandle}", "Ticket", _cic.Ticket);
        if (!string.IsNullOrEmpty(_cic.Folder) && _cic.Folder != DefaultFolder)
            Set($"CohortIdentificationConfiguration:{cicHandle}", "Folder", ScriptLine.QuoteText(_cic.Folder));

        foreach (var parameter in _cic.GetAllParameters().OrderBy(p => p.ParameterName, StringComparer.Ordinal))
            AddParameter($"CohortIdentificationConfiguration:{cicHandle}", parameter);

        foreach (var joinable in _cic.GetAllJoinables().OrderBy(j => j.ID))
            WritePatientIndexTable(joinable, cicHandle);

        var automaticByHandle = new Dictionary<int, string>();
        for (var i = 0; i < 2; i++)
            if (automatic[i] != null)
                automaticByHandle[automatic[i].ID] = automaticHandles[i];

        WriteContainerSettings(root, rootHandle);
        WriteContainerContents(root, rootHandle, automaticByHandle);

        for (var i = 0; i < 2; i++)
            if (automatic[i] == null)
                Add("Delete", new[] { $"CohortAggregateContainer:{automaticHandles[i]}" });

        _lines.AddRange(_disabled);
    }

    private void WriteContainerSettings(CohortAggregateContainer container, string handle)
    {
        Set($"CohortAggregateContainer:{handle}", "Operation", container.Operation.ToString());
        Set($"CohortAggregateContainer:{handle}", "Name", ScriptLine.QuoteText(container.Name));
        if (container.IsDisabled)
            _disabled.Add(SetLine($"CohortAggregateContainer:{handle}", "IsDisabled", "true"));
    }

    private void WriteContainerContents(CohortAggregateContainer container, string handle,
        IReadOnlyDictionary<int, string> automatic)
    {
        var orderLines = new List<string>();
        foreach (var content in container.GetOrderedContents())
            switch (content)
            {
                case AggregateConfiguration set:
                    var setHandle = WriteSet(set, handle);
                    orderLines.Add(SetLine($"AggregateConfiguration:{setHandle}", "Order", set.Order.ToString()));
                    break;
                case CohortAggregateContainer child:
                    string childHandle;
                    if (container.ID == _cic.RootCohortAggregateContainer_ID && automatic.TryGetValue(child.ID, out var reused))
                    {
                        childHandle = reused;
                    }
                    else
                    {
                        childHandle = Handle("c", child.ID);
                        Add("AddCohortSubContainer", new[] { $"CohortAggregateContainer:{handle}" }, childHandle);
                    }

                    WriteContainerSettings(child, childHandle);
                    WriteContainerContents(child, childHandle, automatic);
                    orderLines.Add(SetLine($"CohortAggregateContainer:{childHandle}", "Order", child.Order.ToString()));
                    break;
            }

        // RDMP puts each new child at the top, so the original order is set once every child exists
        _lines.AddRange(orderLines);
    }

    private string WriteSet(AggregateConfiguration set, string containerHandle)
    {
        var catalogue = CatalogueOf(set);
        RefuseWhatASetCannotHave(set, catalogue);

        if (!string.IsNullOrWhiteSpace(set.CountSQL))
            throw new CohortScriptExportException(CohortScriptRefusal.CountColumnOnSet,
                $"Set {set.ID} \"{set.Name}\" has a count column, which RDMP allows only on a patient index table. Remove it from the set.");

        var dimensions = set.AggregateDimensions;
        var identifier = IdentifierDimension(set, dimensions);
        if (dimensions.Length != 1)
            throw new CohortScriptExportException(CohortScriptRefusal.ExtraColumnsOnSet,
                $"Set {set.ID} \"{set.Name}\" selects {dimensions.Length} columns. A cohort set selects only its identifier column; remove the others.");

        var handle = Handle("a", set.ID);
        var identifierName = identifier.ExtractionInformation.GetRuntimeName();
        Add("AddCatalogueToCohortSet", new[]
        {
            $"CohortAggregateContainer:{containerHandle}", $"Catalogue:{catalogue.ID}", ScriptLine.QuoteText(catalogue.Name),
            ScriptLine.QuoteText(identifierName), "false"
        }, handle, DimensionHandle(handle, identifier));

        foreach (var use in set.PatientIndexJoinablesUsed)
            Add("UsePatientIndexTable", new[]
            {
                $"AggregateConfiguration:{handle}",
                $"JoinableCohortAggregateConfiguration:$j{use.JoinableCohortAggregateConfiguration_ID}",
                use.JoinType.ToString()
            });

        WriteAggregateBody(set, handle, catalogue);

        if (set.IsDisabled)
            _disabled.Add(SetLine($"AggregateConfiguration:{handle}", "IsDisabled", "true"));
        return handle;
    }

    private void WritePatientIndexTable(JoinableCohortAggregateConfiguration joinable, string cicHandle)
    {
        var table = joinable.AggregateConfiguration;
        var catalogue = CatalogueOf(table);
        RefuseWhatASetCannotHave(table, catalogue);

        var dimensions = table.AggregateDimensions;
        var identifier = IdentifierDimension(table, dimensions);
        var extras = dimensions.Where(d => d.ID != identifier.ID).OrderBy(d => d.Order).ThenBy(d => d.ID).ToArray();
        var extraNames = extras.Select(d => d.ExtractionInformation.GetRuntimeName()).ToArray();
        var allNames = extraNames.Append(identifier.ExtractionInformation.GetRuntimeName()).ToArray();
        var repeated = allNames.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (repeated != null)
            throw new CohortScriptExportException(CohortScriptRefusal.RepeatedSourceColumn,
                $"Patient index table {joinable.ID} uses column \"{repeated.Key}\" of its catalogue more than once, for example as both a first and a last date. A build script names each column once, so it cannot rebuild this table. Base each output on its own catalogue column.");
        if (extraNames.Any(n => n.Contains(',')))
            throw new CohortScriptExportException(CohortScriptRefusal.CommaInColumnName,
                $"Patient index table {joinable.ID} uses a column whose name contains a comma, which a build script cannot list. Rename the column in the catalogue.");

        var handle = Handle("a", table.ID);
        var handles = new List<string> { handle, Handle("j", joinable.ID), DimensionHandle(handle, identifier) };
        handles.AddRange(extras.Select(d => DimensionHandle(handle, d)));
        Add("AddPatientIndexTable", new[]
        {
            $"CohortIdentificationConfiguration:{cicHandle}", $"Catalogue:{catalogue.ID}", ScriptLine.QuoteText(catalogue.Name),
            ScriptLine.QuoteText(identifier.ExtractionInformation.GetRuntimeName()),
            extraNames.Length == 0 ? "null" : ScriptLine.QuoteText(string.Join(",", extraNames)),
            "false"
        }, handles.ToArray());

        WriteAggregateBody(table, handle, catalogue);
        if (!string.IsNullOrWhiteSpace(table.CountSQL))
            Set($"AggregateConfiguration:{handle}", "CountSQL", ScriptLine.QuoteText(Mark(table.CountSQL)));
    }

    private void WriteAggregateBody(AggregateConfiguration aggregate, string handle, Catalogue catalogue)
    {
        // the rename comes straight after the set is made, so only one set at a time holds the default name
        var shortName = Regex.Replace(aggregate.Name ?? "", @"^cic_\d+_", "");
        if (shortName.StartsWith("cic_", StringComparison.OrdinalIgnoreCase))
            throw new CohortScriptExportException(CohortScriptRefusal.SetNameLooksPrefixed,
                $"Set {aggregate.ID} is called \"{aggregate.Name}\", which still starts with cic_ after RDMP's prefix. RenameCohortSet refuses such a name, so the set could not be rebuilt with it. Rename the set.");
        if (shortName != $"People in {catalogue}")
            Add("RenameCohortSet", new[] { $"AggregateConfiguration:{handle}", ScriptLine.QuoteText(shortName) });

        SetIfGiven($"AggregateConfiguration:{handle}", "Description", aggregate.Description);

        foreach (var dimension in aggregate.AggregateDimensions)
        {
            var column = dimension.ExtractionInformation;
            var target = $"AggregateDimension:{DimensionHandle(handle, dimension)}";
            // written even when equal to the column's, so a later change in the catalogue does not change the
            // rebuilt cohort: the script is the saved cohort
            Set(target, "SelectSQL", ScriptLine.QuoteText(Mark(dimension.SelectSQL)));
            if (dimension.Alias != null || column.Alias != null)
                Set(target, "Alias", ScriptLine.QuoteText(dimension.Alias));
            if (!dimension.GroupBy)
                Set(target, "GroupBy", "false");
        }

        if (!string.IsNullOrWhiteSpace(aggregate.HavingSQL))
            Set($"AggregateConfiguration:{handle}", "HavingSQL", ScriptLine.QuoteText(Mark(aggregate.HavingSQL)));

        foreach (var table in aggregate.ForcedJoins.OrderBy(t => t.ID))
            Add("AddForcedJoin", new[] { $"AggregateConfiguration:{handle}", $"TableInfo:{table.ID}", ScriptLine.QuoteText(table.Name) });

        foreach (var parameter in _locator.CatalogueRepository.GetAllObjects<AnyTableSqlParameter>()
                     .Where(p => p.ReferencedObjectType == nameof(AggregateConfiguration) && p.ReferencedObjectID == aggregate.ID)
                     .OrderBy(p => p.ParameterName, StringComparer.Ordinal))
            AddParameter($"AggregateConfiguration:{handle}", parameter);

        if (aggregate.GetTopXIfAny() is { } topX)
        {
            var orderBy = topX.OrderByDimensionIfAny;
            Add("SetTopX", new[]
            {
                $"AggregateConfiguration:{handle}", topX.TopX.ToString(),
                orderBy == null ? "null" : $"AggregateDimension:{DimensionHandle(handle, orderBy)}",
                topX.OrderByDirection.ToString()
            });
        }

        if (aggregate.RootFilterContainer is AggregateFilterContainer root)
            WriteFilterContainer(root, $"AggregateConfiguration:{handle}", true);
    }

    private void WriteFilterContainer(AggregateFilterContainer container, string parent, bool isRoot)
    {
        var handle = Handle("fc", container.ID);
        Add(isRoot ? "AddNewFilterContainer" : "AddChildFilterContainer", new[] { parent }, handle);
        if (container.Operation != FilterContainerOperation.AND)
            Set($"AggregateFilterContainer:{handle}", "Operation", container.Operation.ToString());
        if (container.IsDisabled)
            _disabled.Add(SetLine($"AggregateFilterContainer:{handle}", "IsDisabled", "true"));

        foreach (var filter in container.GetFilters().Cast<AggregateFilter>().OrderBy(f => f.Order).ThenBy(f => f.ID))
        {
            var filterHandle = Handle("f", filter.ID);
            Add("CreateNewFilter", new[]
            {
                $"AggregateFilterContainer:{handle}", ScriptLine.QuoteText(filter.Name), ScriptLine.QuoteText(Mark(filter.WhereSQL))
            }, filterHandle);
            var filterLine = _lines.Count - 1;

            foreach (var parameter in filter.GetAllParameters().OrderBy(p => p.ParameterName, StringComparer.Ordinal))
                AddParameter($"AggregateFilter:{filterHandle}", parameter);

            if (filter.ClonedFromExtractionFilter_ID is { } sourceId)
            {
                if (_locator.CatalogueRepository.GetAllObjectsInIDList<ExtractionFilter>(new[] { sourceId }).SingleOrDefault() is { } source)
                    Add("SetFilterSource", new[]
                    {
                        $"AggregateFilter:{filterHandle}", $"ExtractionFilter:{source.ID}", ScriptLine.QuoteText(source.Name)
                    });
                else
                    _comments[filterLine] =
                        $"copied from published filter {sourceId}, which no longer exists, so no SetFilterSource line";
            }

            SetIfGiven($"AggregateFilter:{filterHandle}", "Description", filter.Description);
            if (filter.IsDisabled)
                _disabled.Add(SetLine($"AggregateFilter:{filterHandle}", "IsDisabled", "true"));
        }

        foreach (var child in container.GetSubContainers().Cast<AggregateFilterContainer>().OrderBy(c => c.ID))
            WriteFilterContainer(child, $"AggregateFilterContainer:{handle}", false);
    }

    private void AddParameter(string owner, ISqlParameter parameter) =>
        Add("AddCohortParameter", new[]
        {
            owner, ScriptLine.QuoteText(parameter.ParameterName), ScriptLine.QuoteText(Mark(parameter.ParameterSQL)),
            ScriptLine.QuoteText(Mark(parameter.Value)), parameter.Comment == null ? "null" : ScriptLine.QuoteText(parameter.Comment)
        });

    private static void RefuseWhatASetCannotHave(AggregateConfiguration aggregate, Catalogue catalogue)
    {
        if (catalogue.IsApiCall())
            throw new CohortScriptExportException(CohortScriptRefusal.ApiCatalogue,
                $"Set {aggregate.ID} \"{aggregate.Name}\" reads catalogue {catalogue.ID} \"{catalogue.Name}\", which is served by an API. A build script cannot carry it.");

        if (aggregate.PivotOnDimensionID != null)
            throw new CohortScriptExportException(CohortScriptRefusal.PivotOnSet,
                $"Set {aggregate.ID} \"{aggregate.Name}\" has a pivot, which a cohort query cannot use. Remove the pivot.");

        foreach (var dimension in aggregate.AggregateDimensions)
        {
            if (dimension.ExtractionInformation == null)
                throw new CohortScriptExportException(CohortScriptRefusal.DimensionWithoutColumn,
                    $"Set {aggregate.ID} \"{aggregate.Name}\" has a column (dimension {dimension.ID}) that is no longer in its catalogue. Remove it from the set.");

            if (dimension.AggregateContinuousDateAxis != null)
                throw new CohortScriptExportException(CohortScriptRefusal.DateAxisOnSet,
                    $"Set {aggregate.ID} \"{aggregate.Name}\" has a date axis, which a cohort query cannot use. Remove the axis.");
        }
    }

    private static AggregateDimension IdentifierDimension(AggregateConfiguration aggregate, AggregateDimension[] dimensions)
    {
        var identifiers = dimensions.Where(d => d.IsExtractionIdentifier).ToArray();
        if (identifiers.Length != 1)
            throw new CohortScriptExportException(
                identifiers.Length == 0 ? CohortScriptRefusal.NoIdentifierColumn : CohortScriptRefusal.TooManyIdentifierColumns,
                $"Set {aggregate.ID} \"{aggregate.Name}\" has {identifiers.Length} identifier columns, and a cohort set needs exactly one.");
        return identifiers[0];
    }

    private static Catalogue CatalogueOf(AggregateConfiguration aggregate) => (Catalogue)aggregate.Catalogue;

    private static string DimensionHandle(string aggregateHandle, AggregateDimension dimension)
    {
        var column = dimension.ExtractionInformation.GetRuntimeName();
        if (column.Any(c => char.IsWhiteSpace(c) || c is '"' or '.' or '{' or '}'))
            throw new CohortScriptExportException(CohortScriptRefusal.ColumnNameNotUsableInHandle,
                $"Column \"{column}\" of set {dimension.AggregateConfiguration_ID} has a space, quote, dot or brace in its name, which a build script handle cannot hold. Give the column a plain name in the catalogue.");
        return $"{aggregateHandle}.{column}";
    }

    // marks the ix<joinable id> alias of this cohort's patient index tables; refuses text that looks like a marker
    private string Mark(string sql)
    {
        if (sql == null)
            return null;

        if (sql.Contains("{$", StringComparison.Ordinal))
            throw new CohortScriptExportException(CohortScriptRefusal.MarkerTextInSql,
                $"Cohort {_cic.ID} \"{_cic.Name}\" has SQL that contains the text {{$, which a build script uses for its own markers: {sql}");

        return SqlText.MarkJoinAliases(sql, _joinableIds);
    }

    private IEnumerable<Project> LinkedProjects() =>
        _locator.DataExportRepository?
            .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", _cic.ID)
            .Select(a => (Project)a.Project).OrderBy(p => p.ID) ?? Enumerable.Empty<Project>();

    private string Handle(string role, int id)
    {
        // a filter tree shared through RDMP's filter shortcut can appear under two sets; give each copy its own handle
        var handle = $"${role}{id}";
        for (var copy = 2; _handles.Contains(handle); copy++)
            handle = $"${role}{id}x{copy}";
        _handles.Add(handle);
        return handle;
    }

    private string Reserve(string handle)
    {
        _handles.Add(handle);
        return handle;
    }

    private void SetIfGiven(string target, string property, string value)
    {
        if (!string.IsNullOrEmpty(value))
            Set(target, property, ScriptLine.QuoteText(value));
    }

    private void Set(string target, string property, string value) => _lines.Add(SetLine(target, property, value));

    private static string SetLine(string target, string property, string value) =>
        ScriptLine.Write("Set", new[] { target, property, value });

    private void Add(string command, IEnumerable<string> arguments, params string[] handles) =>
        _lines.Add(ScriptLine.Write(command, arguments, handles));

    private static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        assembly.GetName().Version?.ToString() ?? "unknown";
}
