// Checks a build script before anything is built, and collects every problem rather than stopping at the first.
// Offline checks need no RDMP: commands, argument counts, handles and their roles, Set and Delete on handles only,
// allowed properties. Instance checks read the platform: every existing object a line names exists and still has
// the name the script gives, and the new cohort's name is free.

using System;
using System.Collections.Generic;
using System.Linq;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.Repositories;

namespace RdmpCohortScript;

/// <summary>Checks build script lines before a rebuild.</summary>
internal sealed class CohortScriptChecker
{
    private readonly IRDMPPlatformRepositoryServiceLocator _locator;
    private readonly List<CohortScriptProblem> _problems;
    private readonly Dictionary<string, HandleRole> _created = new(StringComparer.Ordinal);
    private readonly HashSet<int> _linkedProjects = new();

    public CohortScriptChecker(IRDMPPlatformRepositoryServiceLocator locator, List<CohortScriptProblem> problems)
    {
        _locator = locator;
        _problems = problems;
    }

    public void Check(IReadOnlyList<ScriptFileLine> lines, string newCohortName)
    {
        var parsed = lines.Parsed().ToArray();
        if (parsed.Length == 0)
        {
            Problem(0, CohortScriptRefusal.NotFormat1, "The Commands list has no lines.");
            return;
        }

        if (parsed[0].Parsed.Command != "CreateNewCohortIdentificationConfiguration")
            Problem(parsed[0].FileLine, CohortScriptRefusal.CreateLineNotFirst,
                "The first line must create the cohort (CreateNewCohortIdentificationConfiguration).");

        foreach (var line in parsed)
            CheckLine(line, line == parsed[0]);

        if (!string.IsNullOrWhiteSpace(newCohortName) && _locator.CatalogueRepository
                .GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", newCohortName).Any())
            Problem(0, CohortScriptRefusal.CohortNameTaken,
                $"A cohort called \"{newCohortName}\" already exists. Give the rebuilt cohort another name.");
    }

    private void CheckLine(ScriptFileLine line, bool isFirst)
    {
        var l = line.Parsed;
        if (!CohortScriptFormat.Commands.TryGetValue(l.Command, out var definition))
        {
            Problem(line.FileLine, CohortScriptRefusal.UnknownCommand,
                $"{l.Command} is not a build script command. The format document lists the commands a script may use.");
            return;
        }

        if (l.Command == "CreateNewCohortIdentificationConfiguration" && !isFirst)
            Problem(line.FileLine, CohortScriptRefusal.CreateLineNotFirst, "A build script creates one cohort, on its first line only.");

        if (l.Arguments.Count != definition.Arguments)
        {
            Problem(line.FileLine, CohortScriptRefusal.WrongArgumentCount,
                $"{l.Command} takes {definition.Arguments} argument(s), but the line has {l.Arguments.Count}.");
            return;
        }

        for (var i = 0; i < l.Arguments.Count; i++)
            CheckArgument(line, definition, i);

        if (definition.Reference is { } reference)
            CheckReference(line, reference);

        if (l.Command is "Set" or "SetText")
            CheckSet(line, l.Command == "Set" ? CohortScriptFormat.WordProperties : CohortScriptFormat.TextProperties);

        CheckOutputs(line, definition);
    }

    // every argument is checked by what its position holds, whatever its quoting
    private void CheckArgument(ScriptFileLine line, ScriptCommandDefinition definition, int index)
    {
        var argument = line.Parsed.Arguments[index];
        var spec = definition.ArgumentSpecs[index];
        var position = $"Argument {index + 1} of {line.Parsed.Command}";

        switch (spec.Kind)
        {
            case ArgumentKind.Handle:
                if (argument.IsNull && spec.OptionalHandle)
                    return;
                if (argument.Quoted || !CohortScriptFormat.TrySplitTyped(argument.Text, out var typeName, out var rest) ||
                    !spec.Types.Contains(typeName))
                {
                    Problem(line.FileLine, CohortScriptRefusal.WrongArgumentKind,
                        $"{position} must be one of this script's objects, written bare as {string.Join(" or ", spec.Types.Select(t => $"{t}:$..."))}, not {Shown(argument)}.");
                    return;
                }

                if (!rest.StartsWith('$'))
                {
                    Problem(line.FileLine, CohortScriptRefusal.IdInsteadOfHandle,
                        $"{argument.Text} refers to an existing {typeName} by id. A build script changes only what it creates, so it refers to such objects by handle ({typeName}:$...).");
                    return;
                }

                RequireCreated(line, rest, CohortScriptFormat.HandleTypes[typeName]);
                return;

            case ArgumentKind.Reference:
                if (argument.Quoted)
                    Problem(line.FileLine, CohortScriptRefusal.WrongArgumentKind,
                        $"{position} refers to an existing object, written bare as Type:id, not in quotes.");
                return;

            case ArgumentKind.Text:
                RequireTextOrNull(line, argument, position);
                return;

            case ArgumentKind.Sql:
                if (RequireTextOrNull(line, argument, position))
                    CheckMarkers(line, argument);
                return;

            case ArgumentKind.Word:
                if (argument.Quoted || argument.IsNull)
                    Problem(line.FileLine, CohortScriptRefusal.WrongArgumentKind,
                        $"{position} is a plain word (true, false, a number or a name such as INTERSECT), written without quotes, not {Shown(argument)}.");
                return;

            case ArgumentKind.SetValue:
                if (RequireTextOrNull(line, argument, position) &&
                    CohortScriptFormat.SqlProperties.Contains(line.Parsed.Arguments[1].Text))
                    CheckMarkers(line, argument);
                return;
        }
    }

    private bool RequireTextOrNull(ScriptFileLine line, ScriptArgument argument, string position)
    {
        if (argument.Quoted || argument.IsNull)
            return argument.Quoted;

        Problem(line.FileLine, CohortScriptRefusal.WrongArgumentKind,
            $"{position} is text, written in double quotes (or the bare word null), not {argument.Text}.");
        return false;
    }

    // a marker inside SQL must name a patient index table made earlier
    private void CheckMarkers(ScriptFileLine line, ScriptArgument argument)
    {
        foreach (System.Text.RegularExpressions.Match m in CohortScriptFormat.SqlMarker.Matches(argument.Text))
            RequireCreated(line, m.Groups["handle"].Value, HandleRole.Joinable);
    }

    private static string Shown(ScriptArgument argument) => argument.Quoted ? $"\"{argument.Text}\" in quotes" : argument.Text;

    private void CheckReference(ScriptFileLine line, ExistingReference reference)
    {
        var idArgument = line.Parsed.Arguments[reference.IdArgument];
        var nameArgument = line.Parsed.Arguments[reference.NameArgument];

        if (!CohortScriptFormat.TrySplitTyped(idArgument.Text, out var typeName, out var idText) ||
            typeName != reference.TypeName || !int.TryParse(idText, out var id))
        {
            Problem(line.FileLine, CohortScriptRefusal.BadReference,
                $"{line.Parsed.Command} refers to the existing {reference.TypeName} as {reference.TypeName}:<id>, not {idArgument.Text}.");
            return;
        }

        if (nameArgument.IsNull)
        {
            Problem(line.FileLine, CohortScriptRefusal.MissingName,
                $"The line gives no name for {reference.TypeName} {id}. A build script always gives the name beside the id, so a wrong id is caught.");
            return;
        }

        var existing = Find(reference.TypeName, id);
        if (existing == null)
        {
            Problem(line.FileLine, CohortScriptRefusal.ReferenceNotFound,
                $"There is no {reference.TypeName} {id} (\"{nameArgument.Text}\") on this platform.");
            return;
        }

        var current = ((INamed)existing).Name;
        var mismatch = ExistingObjectName.Mismatch(reference.TypeName, existing, current, nameArgument.Text);
        if (mismatch != null)
            Problem(line.FileLine, CohortScriptRefusal.NameMismatch, mismatch);

        if (existing is Project project)
            _linkedProjects.Add(project.ID);

        if (existing is Catalogue catalogue)
            CheckCatalogueLine(line, catalogue);
    }

    // the identifier and extra columns exist, and a project-specific catalogue's project is linked earlier in the script
    private void CheckCatalogueLine(ScriptFileLine line, Catalogue catalogue)
    {
        var identifierArgument = line.Parsed.Arguments[3];
        var identifier = IdentifierColumn.Find(catalogue, identifierArgument.IsNull ? null : identifierArgument.Text,
            out var refusal, out var columnProblem);
        if (identifier == null)
            Problem(line.FileLine, refusal, columnProblem);

        if (line.Parsed.Command == "AddPatientIndexTable" && !line.Parsed.Arguments[4].IsNull)
        {
            var columns = catalogue.GetAllExtractionInformation(ExtractionCategory.Any);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in line.Parsed.Arguments[4].Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var column = columns.FirstOrDefault(e => string.Equals(e.GetRuntimeName(), name, StringComparison.OrdinalIgnoreCase));
                if (column == null)
                    Problem(line.FileLine, CohortScriptRefusal.UnknownColumn,
                        $"Catalogue {catalogue.ID} \"{catalogue.Name}\" has no column called \"{name}\".");
                else if (column.IsExtractionIdentifier || !seen.Add(name) ||
                         string.Equals(name, identifier?.GetRuntimeName(), StringComparison.OrdinalIgnoreCase))
                    Problem(line.FileLine, CohortScriptRefusal.DuplicateColumn,
                        $"Column \"{name}\" is listed twice, or is an identifier column. A patient index table has one identifier column and each other column once.");
            }
        }

        var dataExport = _locator.DataExportRepository;
        if (dataExport == null)
            return;

        var projects = dataExport.GetAllObjectsWhere<ExtractableDataSet>("Catalogue_ID", catalogue.ID)
            .SelectMany(eds => eds.Projects).ToArray();
        if (projects.Length > 0 && !projects.Any(p => _linkedProjects.Contains(p.ID)))
            Problem(line.FileLine, CohortScriptRefusal.ProjectNotLinkedInScript,
                $"Catalogue {catalogue.ID} \"{catalogue.Name}\" is specific to project {string.Join(", ", projects.Select(p => $"{p.ID} \"{p.Name}\""))}. Link the cohort to it with a LinkCohortToProject line before this one.");
    }

    private void CheckSet(ScriptFileLine line, IReadOnlyDictionary<string, string[]> settable)
    {
        var target = line.Parsed.Arguments[0];
        var property = line.Parsed.Arguments[1].Text;
        if (target.Quoted || !CohortScriptFormat.TrySplitTyped(target.Text, out var typeName, out _) ||
            !settable.TryGetValue(typeName, out var allowed))
            return; // the argument check has reported it

        if (!allowed.Contains(property, StringComparer.Ordinal))
            Problem(line.FileLine, CohortScriptRefusal.PropertyNotAllowed, line.Parsed.Command == "Set"
                ? $"Set changes only plain values of {typeName} in a build script ({string.Join(", ", allowed)}); text goes through SetText."
                : $"SetText changes only the text of {typeName} ({string.Join(", ", allowed)}).");
    }

    private void CheckOutputs(ScriptFileLine line, ScriptCommandDefinition definition)
    {
        var handles = line.Parsed.Handles;
        var expected = definition.Outputs.Length;
        if (definition.MoreDimensions)
        {
            var extras = line.Parsed.Arguments[4];
            expected += extras.IsNull ? 0 : extras.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        if (handles.Count != expected)
        {
            Problem(line.FileLine, CohortScriptRefusal.WrongHandleCount,
                $"{line.Parsed.Command} binds {expected} handle(s) here, but the line gives {handles.Count}.");
            return;
        }

        for (var i = 0; i < handles.Count; i++)
        {
            var role = i < definition.Outputs.Length ? definition.Outputs[i] : HandleRole.Dimension;
            var actual = CohortScriptFormat.RoleOf(handles[i]);
            if (actual != role)
            {
                Problem(line.FileLine, CohortScriptRefusal.BadHandle,
                    $"Handle {handles[i]} should stand for a {Describe(role)}, so it should look like {Example(role)}.");
                continue;
            }

            if (role == HandleRole.Dimension && CohortScriptFormat.SplitDimension(handles[i]).SetHandle != handles[0])
                Problem(line.FileLine, CohortScriptRefusal.BadHandle,
                    $"Dimension handle {handles[i]} must start with the line's set handle {handles[0]}.");

            if (!_created.TryAdd(handles[i], role))
                Problem(line.FileLine, CohortScriptRefusal.HandleCreatedTwice,
                    $"Handle {handles[i]} is already bound by an earlier line. Each handle is created once.");
        }
    }

    private void RequireCreated(ScriptFileLine line, string handle, HandleRole role)
    {
        var actual = CohortScriptFormat.RoleOf(handle);
        if (actual == null)
        {
            Problem(line.FileLine, CohortScriptRefusal.BadHandle, $"{handle} is not a valid handle.");
            return;
        }

        if (!_created.TryGetValue(handle, out var created))
        {
            Problem(line.FileLine, CohortScriptRefusal.HandleUsedBeforeCreated,
                $"Handle {handle} is used before any line creates it.");
            return;
        }

        if (created != role)
            Problem(line.FileLine, CohortScriptRefusal.BadHandle,
                $"Handle {handle} stands for a {Describe(created)}, but the line uses it as a {Describe(role)}.");
    }

    private IMapsDirectlyToDatabaseTable Find(string typeName, int id) => typeName switch
    {
        "Catalogue" => _locator.CatalogueRepository.GetAllObjectsInIDList<Catalogue>(new[] { id }).SingleOrDefault(),
        "TableInfo" => _locator.CatalogueRepository.GetAllObjectsInIDList<TableInfo>(new[] { id }).SingleOrDefault(),
        "ExtractionFilter" => _locator.CatalogueRepository.GetAllObjectsInIDList<ExtractionFilter>(new[] { id }).SingleOrDefault(),
        "Project" => _locator.DataExportRepository?.GetAllObjectsInIDList<Project>(new[] { id }).SingleOrDefault(),
        _ => null
    };

    private static string Describe(HandleRole role) => role switch
    {
        HandleRole.Cohort => "cohort",
        HandleRole.Container => "container",
        HandleRole.Set => "set",
        HandleRole.Joinable => "patient index table (joinable)",
        HandleRole.FilterContainer => "filter container",
        HandleRole.Filter => "filter",
        _ => "column of a set (dimension)"
    };

    private static string Example(HandleRole role) => role switch
    {
        HandleRole.Cohort => "$cic12",
        HandleRole.Container => "$c12",
        HandleRole.Set => "$a12",
        HandleRole.Joinable => "$j12",
        HandleRole.FilterContainer => "$fc12",
        HandleRole.Filter => "$f12",
        _ => "$a12.column"
    };

    private void Problem(int fileLine, CohortScriptRefusal reason, string message) =>
        _problems.Add(new CohortScriptProblem(fileLine, reason, message));
}
