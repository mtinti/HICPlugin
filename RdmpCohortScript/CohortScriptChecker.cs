// Checks a build script before anything is built, and collects every problem rather than stopping at the first.
// Offline checks need no RDMP: commands, argument counts, handles and their roles, Set and Delete on handles only,
// allowed properties, every parameter declared where RDMP gives it and declared once, handles not used after their
// Delete line, joins RDMP allows, and markers only for tables the set joins. Instance
// checks read the platform: every existing object a line names exists and still has the name the script gives, and
// the new cohort's name is free. For a script written by a person or an agent rather than exported, the check also
// warns when a line leaves out a catalogue's mandatory filters, because a new cohort normally needs them.

using System;
using System.Collections.Generic;
using System.Linq;
using FAnsi.Discovery;
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

    // the file line of the Delete line that removes each handle's object
    private readonly Dictionary<string, int> _deletedBy = new(StringComparer.Ordinal);

    // the parameters each owner declares with AddCohortParameter lines, to refuse a second declaration
    private readonly HashSet<(string Owner, string Name)> _declaredByLine = new();

    // for forced joins: the tables of each set's columns, and each forced join with its line
    private readonly Dictionary<string, HashSet<int>> _columnTablesOf = new(StringComparer.Ordinal);
    private readonly List<(int FileLine, string Set, TableInfo Table)> _forcedJoins = new();

    // the sets that have a top filter container: from a filter container line, or from the mandatory filters a set
    // line copies
    private readonly HashSet<string> _hasTopFilterContainer = new(StringComparer.Ordinal);

    // for the parameter check: the set each filter container and filter belongs to, the set of each patient index
    // table, the patient index tables each set joins, the parameters each owner declares, and the filters with the
    // SQL they use
    private readonly Dictionary<string, string> _setOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _setOfJoinable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _joinedBy = new(StringComparer.Ordinal);
    private readonly HashSet<(string Owner, string Name)> _declared = new();

    // the cohort's parameters declared so far, as written: RDMP does not copy a mandatory filter's parameter that the
    // cohort already declares
    private readonly List<string> _cohortParameters = new();

    // the SQL that may use parameters: each filter's, and the HAVING, count and column SQL of each set. Owner is the
    // filter or set the SQL belongs to, Set the set (null for a filter, whose set is looked up at the end).
    private readonly List<(int FileLine, string Owner, string Set, string What, string Sql)> _sqlUses = new();

    private readonly bool _acceptRenamed;
    private readonly bool _authored;

    /// <summary>
    /// Points worth a look that do not stop a rebuild: renamed objects, when the option to accept them is on, a Top X,
    /// whose patients are not fixed, and mandatory filters left out of an authored script.
    /// </summary>
    public List<CohortScriptProblem> Warnings { get; } = new();

    public CohortScriptChecker(IRDMPPlatformRepositoryServiceLocator locator, List<CohortScriptProblem> problems,
        bool acceptRenamed = false, bool authored = false)
    {
        _locator = locator;
        _problems = problems;
        _acceptRenamed = acceptRenamed;
        _authored = authored;
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

        CheckParametersDeclared();
        CheckMarkersJoined();
        CheckForcedJoins();

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

        // the rebuild reads the flag with bool.Parse, which also takes TRUE, but this check compares the text, so
        // only the two spellings the export writes are allowed
        if (ImportArgument(l) is { Quoted: false, IsNull: false } import && import.Text is not ("true" or "false"))
            Problem(line.FileLine, CohortScriptRefusal.WrongArgumentKind,
                $"The last argument of {l.Command} copies the catalogue's mandatory filters or not. Write true or false, in lower case, not {import.Text}.");

        if (definition.Reference is { } reference)
            CheckReference(line, reference);

        if (l.Command is "Set" or "SetText")
            CheckSet(line, l.Command == "Set" ? CohortScriptFormat.WordProperties : CohortScriptFormat.TextProperties);

        CheckOutputs(line, definition);
        CheckTopFilterContainer(line);
        CheckJoinUse(line);
        RecordForParameters(line);

        if (l.Command == "Delete" && HandleIn(l.Arguments[0]) is { } deleted)
            _deletedBy.TryAdd(deleted, line.FileLine);

        // the column to order by is kept for the set's graph; the cohort query has no ORDER BY
        if (l.Command == "SetTopX")
            Warnings.Add(new CohortScriptProblem(line.FileLine, CohortScriptRefusal.TopXNotFixed,
                "RDMP 9.3.0 builds a cohort set's Top X without an ORDER BY, so the patients it keeps are not fixed, and a rebuild may return other patients. The column to order by is kept for the set's graph and does not change this."));
    }

    // the parameters the copies of the mandatory filters get, by RDMP 9.3.0's rules (ParameterCreator.CreateAll):
    // only those a filter's SQL uses and the cohort does not declare, in the order RDMP copies the filters, with a
    // number added to a name that an earlier copy already has (@code, then @code2). A saved parameter that the SQL
    // no longer uses is not copied.
    private IEnumerable<string> CopiedParameterNames(IEnumerable<ExtractionFilter> mandatory)
    {
        var earlier = new List<string>();
        foreach (var filter in mandatory)
        {
            var required = QuerySyntaxHelper.GetAllParameterNamesFromQuery(filter.WhereSQL);
            foreach (var global in _cohortParameters)
                required.Remove(global);

            // RDMP compares with the parameters of the copies made before this one, not with this copy's own
            var inScope = earlier.ToArray();
            foreach (var name in required)
            {
                var proposed = name;
                if (inScope.Contains(proposed))
                {
                    var number = 2;
                    while (inScope.Contains(proposed + number))
                        number++;
                    proposed += number;
                }

                earlier.Add(proposed);
            }
        }

        return earlier;
    }

    // the argument that says whether a set line copies the catalogue's mandatory filters, or null for other lines
    private static ScriptArgument ImportArgument(ScriptLine line) => line.Command switch
    {
        "AddCatalogueToCohortSet" => line.Arguments[4],
        "AddPatientIndexTable" => line.Arguments[5],
        _ => null
    };

    // a set has one top filter container. RDMP makes it when a set line copies mandatory filters, and
    // AddNewFilterContainer then fails, so the script binds that one with UseTopFilterContainer.
    private void CheckTopFilterContainer(ScriptFileLine line)
    {
        var l = line.Parsed;
        if (l.Command is not ("AddNewFilterContainer" or "UseTopFilterContainer") || HandleIn(l.Arguments[0]) is not { } set)
            return;

        if (!_hasTopFilterContainer.Add(set) && l.Command == "AddNewFilterContainer")
            Problem(line.FileLine, CohortScriptRefusal.TopFilterContainerExists,
                $"Set {set} already has a top filter container, made by an earlier line or with the catalogue's mandatory filters. Use UseTopFilterContainer AggregateConfiguration:{set} instead, which binds the container the set has.");
    }

    // a handle argument, or null when the argument is not a well-formed handle (the argument check reports that)
    private static string HandleIn(ScriptArgument argument) =>
        !argument.Quoted && !argument.IsNull && CohortScriptFormat.TrySplitTyped(argument.Text, out _, out var rest) &&
        rest.StartsWith('$')
            ? rest
            : null;

    private void RecordForParameters(ScriptFileLine line)
    {
        var l = line.Parsed;
        var target = HandleIn(l.Arguments[0]);
        if (target == null)
            return;

        switch (l.Command)
        {
            case "AddNewFilterContainer" or "UseTopFilterContainer" when l.Handles.Count == 1:
                _setOf[l.Handles[0]] = target;
                break;
            case "AddPatientIndexTable" when l.Handles.Count >= 2:
                _setOfJoinable[l.Handles[1]] = l.Handles[0];
                break;
            case "UsePatientIndexTable" when HandleIn(l.Arguments[1]) is { } joinable:
                if (!_joinedBy.TryGetValue(target, out var joined))
                    _joinedBy[target] = joined = new List<string>();
                joined.Add(joinable);
                break;
            case "AddChildFilterContainer" when l.Handles.Count == 1 && _setOf.TryGetValue(target, out var parentSet):
                _setOf[l.Handles[0]] = parentSet;
                break;
            case "AddCohortFilter" when l.Handles.Count == 1:
                if (_setOf.TryGetValue(target, out var set))
                    _setOf[l.Handles[0]] = set;
                if (!l.Arguments[2].IsNull)
                    _sqlUses.Add((line.FileLine, l.Handles[0], null, $"Filter \"{l.Arguments[1].Text}\" ({l.Handles[0]})", l.Arguments[2].Text));
                break;
            case "SetText" when CohortScriptFormat.SqlProperties.Contains(l.Arguments[1].Text) && !l.Arguments[2].IsNull:
                var ownerSet = CohortScriptFormat.RoleOf(target) == HandleRole.Dimension ? CohortScriptFormat.SplitDimension(target).SetHandle : target;
                _sqlUses.Add((line.FileLine, target, ownerSet, $"The {l.Arguments[1].Text} of {target}", l.Arguments[2].Text));
                break;
            case "AddCohortParameter" when !l.Arguments[1].IsNull:
                if (!_declaredByLine.Add((target, l.Arguments[1].Text.ToLowerInvariant())))
                    Problem(line.FileLine, CohortScriptRefusal.ParameterDeclaredTwice,
                        $"An earlier line already declares {l.Arguments[1].Text} on {target}. Declare each parameter once on its owner.");
                _declared.Add((target, l.Arguments[1].Text.ToLowerInvariant()));
                if (CohortScriptFormat.RoleOf(target) == HandleRole.Cohort)
                    _cohortParameters.Add(l.Arguments[1].Text);
                break;
        }
    }

    // SQL Server compares parameter names without case, so the check does too. RDMP declares, in one query, the
    // parameters of the cohort, of the set, of every filter of the set, and of each patient index table the set
    // joins (its set and its filters), so a filter, a HAVING, a count column and a column's SQL see all of them. A
    // parameter of another set is declared only when the whole cohort runs, and a set run alone fails on it, so it
    // does not count.
    private void CheckParametersDeclared()
    {
        var cohortHandles = _created.Where(kv => kv.Value == HandleRole.Cohort).Select(kv => kv.Key).ToArray();
        foreach (var (fileLine, owner, ownerSet, what, sql) in _sqlUses)
        {
            var owners = new HashSet<string>(cohortHandles, StringComparer.Ordinal) { owner };
            var set = ownerSet ?? _setOf.GetValueOrDefault(owner);
            if (set != null)
            {
                var sets = new List<string> { set };
                if (_joinedBy.TryGetValue(set, out var joinables))
                    sets.AddRange(joinables.Where(_setOfJoinable.ContainsKey).Select(j => _setOfJoinable[j]));
                foreach (var s in sets)
                {
                    owners.Add(s);
                    // the filters of the set (and its filter containers, which declare nothing)
                    owners.UnionWith(_setOf.Where(kv => kv.Value == s).Select(kv => kv.Key));
                }
            }

            foreach (var parameter in SqlText.ParameterNames(sql).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!owners.Any(o => _declared.Contains((o, parameter.ToLowerInvariant()))))
                    Problem(fileLine, CohortScriptRefusal.ParameterNotDeclared,
                        $"{what} uses {parameter}, but no line declares it. Add an AddCohortParameter line for {parameter} on the cohort, on the set or one of its filters, or on a patient index table the set joins.");
        }
    }

    // RDMP lets a set join one patient index table, and a patient index table join none, itself included
    private void CheckJoinUse(ScriptFileLine line)
    {
        var l = line.Parsed;
        if (l.Command != "UsePatientIndexTable" || HandleIn(l.Arguments[0]) is not { } set || HandleIn(l.Arguments[1]) is not { } joinable)
            return;

        if (_setOfJoinable.GetValueOrDefault(joinable) == set)
            Problem(line.FileLine, CohortScriptRefusal.JoinsItself,
                $"Set {set} is patient index table {joinable} itself, so it cannot join it.");
        else if (_setOfJoinable.ContainsValue(set))
            Problem(line.FileLine, CohortScriptRefusal.JoinFromAPatientIndexTable,
                $"Set {set} is itself a patient index table, and RDMP does not let a patient index table join another one.");
        else if (_joinedBy.TryGetValue(set, out var joined) && joined.Count > 0)
            Problem(line.FileLine, CohortScriptRefusal.AlreadyJoinsATable,
                $"Set {set} already joins patient index table {joined[0]}. RDMP allows one per set.");
    }

    // a {$j} marker becomes the alias of a joined table, so the set must join that table, or SQL Server cannot bind
    // the name
    private void CheckMarkersJoined()
    {
        foreach (var (fileLine, owner, ownerSet, what, sql) in _sqlUses)
        {
            var set = ownerSet ?? _setOf.GetValueOrDefault(owner);
            if (set == null)
                continue;

            var joined = _joinedBy.GetValueOrDefault(set) ?? new List<string>();
            foreach (var marker in CohortScriptFormat.SqlMarker.Matches(sql).Select(m => m.Groups["handle"].Value).Distinct())
                if (_created.GetValueOrDefault(marker) == HandleRole.Joinable && !joined.Contains(marker))
                    Problem(fileLine, CohortScriptRefusal.MarkerForTableNotJoined,
                        $"{what} uses ix{{{marker}}}, but set {set} does not join patient index table {marker}. Add a UsePatientIndexTable line for it, or remove the marker.");
        }
    }

    // RDMP joins the tables of a set's query through JoinInfo rows. A forced table that is not a table of the set's
    // columns needs a join to another table of the query, or RDMP cannot build it. Lookups can also join tables, so
    // only a table with no join at all is refused.
    private void CheckForcedJoins()
    {
        if (_forcedJoins.Count == 0)
            return;

        var repository = _locator.CatalogueRepository;
        var joins = repository.GetAllObjects<JoinInfo>();
        foreach (var (fileLine, set, table) in _forcedJoins)
        {
            var columnTables = _columnTablesOf.GetValueOrDefault(set) ?? new HashSet<int>();
            if (columnTables.Contains(table.ID))
                continue;

            var columns = table.ColumnInfos.Select(c => c.ID).ToHashSet();
            if (columns.Count == 0)
            {
                Problem(fileLine, CohortScriptRefusal.ForcedJoinWithoutJoin,
                    $"Table {table.ID} \"{table.Name}\" has no columns, so RDMP cannot join it to the set's other tables.");
                continue;
            }

            var otherTables = columnTables.Concat(_forcedJoins.Where(f => f.Set == set && f.Table.ID != table.ID).Select(f => f.Table.ID)).ToHashSet();
            var otherColumns = repository.GetAllObjectsInIDList<TableInfo>(otherTables).SelectMany(t => t.ColumnInfos).Select(c => c.ID).ToHashSet();
            if (!joins.Any(j => (columns.Contains(j.ForeignKey_ID) && otherColumns.Contains(j.PrimaryKey_ID)) ||
                                (columns.Contains(j.PrimaryKey_ID) && otherColumns.Contains(j.ForeignKey_ID))))
                Problem(fileLine, CohortScriptRefusal.ForcedJoinWithoutJoin,
                    $"Table {table.ID} \"{table.Name}\" has no join to the other tables of set {set}, so RDMP cannot build the set's query. Add the join in the catalogue (a JoinInfo), or remove the line.");
        }
    }

    private HashSet<int> ColumnTables(string set)
    {
        if (!_columnTablesOf.TryGetValue(set, out var tables))
            _columnTablesOf[set] = tables = new HashSet<int>();
        return tables;
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
        var mismatch = ExistingObjectName.Mismatch(reference.TypeName, existing, current, nameArgument.Text, inBuildScript: true);
        if (mismatch != null && _acceptRenamed)
            Warnings.Add(new CohortScriptProblem(line.FileLine, CohortScriptRefusal.NameMismatch,
                $"{reference.TypeName} {id} was renamed from \"{nameArgument.Text}\" to \"{current}\". The rebuild uses it, as asked."));
        else if (mismatch != null)
            Problem(line.FileLine, CohortScriptRefusal.NameMismatch, mismatch);

        if (existing is Project project)
            _linkedProjects.Add(project.ID);

        if (existing is TableInfo table && line.Parsed.Command == "AddForcedJoin" && HandleIn(line.Parsed.Arguments[0]) is { } forcedSet)
            _forcedJoins.Add((line.FileLine, forcedSet, table));

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
        else if (line.Parsed.Handles.Count > 0)
            ColumnTables(line.Parsed.Handles[0]).Add(identifier.ColumnInfo.TableInfo_ID);

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
                else if (line.Parsed.Handles.Count > 0)
                    ColumnTables(line.Parsed.Handles[0]).Add(column.ColumnInfo.TableInfo_ID);
            }
        }

        // an exported script always leaves them out, because the saved filters are written as they are. When the line
        // copies them, RDMP makes the set's top filter container, and the copies bring their parameters, which the
        // set's other filters see like any filter's
        var importArgument = ImportArgument(line.Parsed);
        var mandatory = catalogue.GetAllMandatoryFilters();
        if (importArgument.Text == "true" && mandatory.Length > 0 && line.Parsed.Handles.Count > 0)
        {
            _hasTopFilterContainer.Add(line.Parsed.Handles[0]);
            foreach (var name in CopiedParameterNames(mandatory))
                _declared.Add((line.Parsed.Handles[0], name.ToLowerInvariant()));
        }
        if (_authored && importArgument.Text == "false" && mandatory.Length > 0)
            Warnings.Add(new CohortScriptProblem(line.FileLine, CohortScriptRefusal.MandatoryFiltersLeftOut,
                $"Catalogue {catalogue.ID} \"{catalogue.Name}\" has mandatory filters ({string.Join(", ", mandatory.Select(f => $"\"{f.Name}\""))}), but the line leaves them out. A new cohort normally needs them: write true as the last argument."));

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
        else if (_deletedBy.TryGetValue(handle, out var deletedOn))
            Problem(line.FileLine, CohortScriptRefusal.HandleUsedAfterDelete,
                $"Handle {handle} stands for a {Describe(created)} that the Delete line on line {deletedOn} removes. Use it before that line, or delete it later.");
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
