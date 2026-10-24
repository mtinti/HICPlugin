// Runs the lines of a checked build script one by one, as a typed line would run: each command is built through
// the constructor RDMP's command line uses. The runner builds it from typed values itself, so quoted text reaches
// the command exactly; RDMP's own reader would turn the word null into "no value" and Type:id text into an object. Before a line runs,
// its handles are replaced by the ids of the objects earlier lines made: in handle arguments
// (AggregateFilter:$f9 becomes AggregateFilter:412) and, in SQL arguments only, in the marker form {$j17}. After a line
// runs, the runner checks that RDMP did not refuse it, binds its handles to the objects that this line made (found
// as the difference before and after, never "the newest object"), and checks that it had its effect. On the first
// line that fails it stops with a message that names the line.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.CommandLine.Interactive.Picking;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.MapsDirectlyToDatabaseTable.Revertable;
using Rdmp.Core.Repositories;

namespace RdmpCohortScript;

/// <summary>Raised when a line of a rebuild fails; names the line, and the cohort left behind.</summary>
public sealed class CohortScriptRebuildException : Exception
{
    public int FileLine { get; }
    public CohortScriptRefusal Reason { get; }

    /// <summary>When a plugin command refused the line, its own reason; otherwise null.</summary>
    public CohortScriptRefusal? CommandRefusal { get; }

    public CohortScriptRebuildException(int fileLine, CohortScriptRefusal reason, string message, Exception inner = null,
        CohortScriptRefusal? commandRefusal = null)
        : base(message, inner)
    {
        FileLine = fileLine;
        Reason = reason;
        CommandRefusal = commandRefusal;
    }
}

/// <summary>Runs checked build script lines and binds their handles.</summary>
internal sealed class CohortScriptRunner
{
    private static readonly Dictionary<string, Type> ObjectTypes = new(StringComparer.Ordinal)
    {
        ["CohortIdentificationConfiguration"] = typeof(CohortIdentificationConfiguration),
        ["CohortAggregateContainer"] = typeof(CohortAggregateContainer),
        ["AggregateConfiguration"] = typeof(AggregateConfiguration),
        ["JoinableCohortAggregateConfiguration"] = typeof(JoinableCohortAggregateConfiguration),
        ["AggregateFilterContainer"] = typeof(AggregateFilterContainer),
        ["AggregateFilter"] = typeof(AggregateFilter),
        ["AggregateDimension"] = typeof(AggregateDimension)
    };

    private readonly IBasicActivateItems _activator;
    private readonly ICatalogueRepository _repository;
    private readonly string _newCohortName;
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type> _commandTypes;
    private IMapsDirectlyToDatabaseTable _deleting;

    // every object this run's lines saved, in order, from the repositories' Inserting events. Each RDMP session has
    // its own repository objects, so another session's work never appears here: this is what the run owns.
    private readonly List<IMapsDirectlyToDatabaseTable> _made = new();
    private readonly List<IMapsDirectlyToDatabaseTable> _madeByLine = new();

    /// <summary>The cohort the first line made, once it has run.</summary>
    public CohortIdentificationConfiguration Cohort { get; private set; }

    private readonly bool _acceptRenamed;

    public CohortScriptRunner(IBasicActivateItems activator, string newCohortName, bool acceptRenamed = false)
    {
        _acceptRenamed = acceptRenamed;
        _activator = activator;
        _repository = activator.RepositoryLocator.CatalogueRepository;
        _newCohortName = newCohortName;

        _commandTypes = CommandTypes(activator);
    }

    /// <summary>The class that runs each build script command. When RDMP and the plugin share a name, the plugin's wins.</summary>
    internal static Dictionary<string, Type> CommandTypes(IBasicActivateItems activator)
    {
        var plugin = typeof(CohortScriptRunner).Assembly;
        return new CommandInvoker(activator).GetSupportedCommands()
            .Where(t => CohortScriptFormat.Commands.ContainsKey(BasicCommandExecution.GetCommandName(t.Name)))
            .GroupBy(t => BasicCommandExecution.GetCommandName(t.Name))
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Assembly == plugin ? 0 : 1).First(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The constructors that can take a line's arguments in order, after the activator, the one RDMP's command line
    /// uses first.
    /// </summary>
    internal static IEnumerable<ConstructorInfo> Constructors(Type type, int arguments) =>
        type.GetConstructors()
            .Where(c => c.GetParameters() is { Length: > 0 } p && typeof(IBasicActivateItems).IsAssignableFrom(p[0].ParameterType) &&
                        p.All(x => x.ParameterType != typeof(CommandLineObjectPicker)) &&
                        p.Length - 1 >= arguments && p.Skip(1 + arguments).All(x => x.HasDefaultValue))
            .OrderBy(c => c.GetCustomAttributes(typeof(Rdmp.Core.Repositories.Construction.UseWithObjectConstructorAttribute), false).Any() ? 0 : 1);

    public void Run(IEnumerable<ScriptFileLine> lines)
    {
        var first = true;
        ScriptFileLine current = null;
        try
        {
            foreach (var line in lines)
            {
                current = line;
                RunLine(line, first);
                first = false;
            }
        }
        catch (Exception e)
        {
            // whatever went wrong, the partly built cohort is removed, so a failed rebuild leaves nothing behind
            var stopped = e as CohortScriptRebuildException ??
                          Stop(current, CohortScriptRefusal.LineFailed, $"{current?.Parsed?.Command} failed: {e.Message}", e);

            // the create line may fail after RDMP saved the cohort but before the runner bound it; the cohort this
            // run saved is known from the Inserting events, never looked up by name, which another session may share
            Cohort ??= _made.OfType<CohortIdentificationConfiguration>().FirstOrDefault();
            var outcome = Cohort == null && _made.Count == 0
                ? "Nothing was built."
                : new CohortScriptCleanup(_activator.RepositoryLocator).Remove(Cohort, _made);
            Cohort = null;
            throw new CohortScriptRebuildException(stopped.FileLine, stopped.Reason, $"{stopped.Message} {outcome}",
                stopped.InnerException, stopped.CommandRefusal);
        }
    }

    private void RunLine(ScriptFileLine line, bool isFirst)
    {
        var l = line.Parsed;
        var definition = CohortScriptFormat.Commands[l.Command];
        var arguments = l.Arguments.Select((a, i) => Resolve(line, definition, i, a)).ToArray();
        if (isFirst && !string.IsNullOrWhiteSpace(_newCohortName))
            arguments[0] = _newCohortName;

        var before = Before(line, arguments);
        _deleting = line.Parsed.Command == "Delete" ? GetByTypedId(arguments[0]) : null;
        _madeByLine.Clear();
        IAtomicCommand command;
        var repositories = new IRepository[] { _repository, _activator.RepositoryLocator.DataExportRepository }
            .Where(r => r != null).Distinct().ToArray();
        foreach (var repository in repositories)
            repository.Inserting += Recorded;
        try
        {
            command = Execute(line, definition, arguments);
        }
        finally
        {
            foreach (var repository in repositories)
                repository.Inserting -= Recorded;
        }

        Bind(line, arguments, command, before);
        Verify(line, arguments);
    }

    private void Recorded(object sender, IMapsDirectlyToDatabaseTableEventArgs e)
    {
        _made.Add(e.Object);
        _madeByLine.Add(e.Object);
    }

    // handles become ids; {$j} markers become ids in SQL arguments only; text stays exactly as written
    private string Resolve(ScriptFileLine line, ScriptCommandDefinition definition, int index, ScriptArgument argument)
    {
        if (argument.IsNull)
            return null;

        // with the option to accept renamed objects, the commands skip their own name check; the check warned already
        if (_acceptRenamed && definition.Reference?.NameArgument == index)
            return null;

        var kind = definition.ArgumentSpecs[index].Kind;
        var isSql = kind == ArgumentKind.Sql ||
                    (kind == ArgumentKind.SetValue && CohortScriptFormat.SqlProperties.Contains(line.Parsed.Arguments[1].Text));
        if (isSql)
            return CohortScriptFormat.SqlMarker.Replace(argument.Text, m => IdOf(line, m.Groups["handle"].Value).ToString());

        if (kind == ArgumentKind.Handle && CohortScriptFormat.TrySplitTyped(argument.Text, out var typeName, out var rest))
        {
            // otherwise no constructor would take the argument, and the message would not say why
            var id = IdOf(line, rest);
            if (!_repository.StillExists(ObjectTypes[typeName], id))
                throw Stop(line, CohortScriptRefusal.HandleUsedAfterDelete,
                    $"Handle {rest} stood for {typeName} {id}, which no longer exists, for example because an earlier line deleted it.");
            return $"{typeName}:{id}";
        }

        return argument.Text;
    }

    private IAtomicCommand Execute(ScriptFileLine line, ScriptCommandDefinition definition, string[] arguments)
    {
        if (!_commandTypes.TryGetValue(line.Parsed.Command, out var type))
            throw Stop(line, CohortScriptRefusal.UnknownCommand,
                $"RDMP has no command {line.Parsed.Command}. Check that the cohort script plugin is installed.");

        IAtomicCommand command;
        try
        {
            command = Construct(line, type, definition, arguments);
        }
        catch (CohortScriptRebuildException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw Stop(line, CohortScriptRefusal.LineFailed, $"{line.Parsed.Command} failed: {Unwrap(e).Message}", e);
        }

        return command;
    }

    private IAtomicCommand Construct(ScriptFileLine line, Type type, ScriptCommandDefinition definition, string[] arguments)
    {
        foreach (var constructor in Constructors(type, arguments.Length))
        {
            var parameters = constructor.GetParameters();
            var values = new object[parameters.Length];
            values[0] = _activator;
            if (!TryConvert(definition, arguments, parameters, values))
                continue;
            for (var i = 1 + arguments.Length; i < parameters.Length; i++)
                values[i] = parameters[i].DefaultValue;

            var command = (IAtomicCommand)constructor.Invoke(values);
            if (command.IsImpossible)
                throw Stop(line, CohortScriptRefusal.LineRefused, $"RDMP refused {line.Parsed.Command}: {command.ReasonCommandImpossible}",
                    commandRefusal: (command as CohortScriptCommand)?.Refusal);
            command.Execute();
            return command;
        }

        throw Stop(line, CohortScriptRefusal.LineFailed, $"{line.Parsed.Command} has no constructor that takes these arguments.");
    }

    private bool TryConvert(ScriptCommandDefinition definition, string[] arguments, ParameterInfo[] parameters, object[] values)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            var parameterType = parameters[i + 1].ParameterType;
            var text = arguments[i];
            object value;
            try
            {
                value = definition.ArgumentSpecs[i].Kind switch
                {
                    _ when text == null => null,
                    ArgumentKind.Handle or ArgumentKind.Reference => ObjectFor(text, parameterType),
                    _ when parameterType == typeof(string) => text,
                    _ when parameterType == typeof(bool) => bool.Parse(text),
                    _ when parameterType == typeof(int) => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
                    _ when parameterType.IsEnum => Enum.Parse(parameterType, text, true),
                    _ => throw new FormatException()
                };
            }
            catch (Exception)
            {
                return false;
            }

            if (value == null ? parameterType.IsValueType : !parameterType.IsInstanceOfType(value))
                return false;
            values[i + 1] = value;
        }

        return true;
    }

    private object ObjectFor(string typedId, Type parameterType)
    {
        CohortScriptFormat.TrySplitTyped(typedId, out var typeName, out var idText);
        var id = int.Parse(idText);
        object found = typeName switch
        {
            "Catalogue" => _repository.GetObjectByID<Catalogue>(id),
            "TableInfo" => _repository.GetObjectByID<TableInfo>(id),
            "ExtractionFilter" => _repository.GetObjectByID<ExtractionFilter>(id),
            "Project" => _activator.RepositoryLocator.DataExportRepository.GetObjectByID<Project>(id),
            _ => _repository.GetObjectByID(ObjectTypes[typeName], id)
        };

        if (!parameterType.IsArray)
            return found;
        var array = Array.CreateInstance(parameterType.GetElementType()!, 1);
        array.SetValue(found, 0);
        return array;
    }

    private static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: { } inner } ? inner : e;

    // what a creating line's parent held before, so the new object is found as the difference
    private HashSet<int> Before(ScriptFileLine line, string[] arguments) => line.Parsed.Command switch
    {
        "AddCohortSubContainer" => Get<CohortAggregateContainer>(arguments[0]).GetSubContainers().Select(c => c.ID).ToHashSet(),
        _ => null
    };

    private void Bind(ScriptFileLine line, string[] arguments, IAtomicCommand command, HashSet<int> before)
    {
        var handles = line.Parsed.Handles;
        switch (line.Parsed.Command)
        {
            case "CreateNewCohortIdentificationConfiguration":
                Cohort = _madeByLine.OfType<CohortIdentificationConfiguration>().SingleOrDefault()
                         ?? throw Stop(line, CohortScriptRefusal.LineHadNoEffect, $"No cohort called \"{arguments[0]}\" was made.");
                var root = Cohort.RootCohortAggregateContainer;
                var automatic = root?.GetSubContainers().OrderBy(c => c.Order).ToArray() ?? Array.Empty<CohortAggregateContainer>();
                if (automatic.Length != 2)
                    throw Stop(line, CohortScriptRefusal.LineHadNoEffect,
                        $"RDMP made cohort {Cohort.ID} with {automatic.Length} child container(s) under the root, not the 2 a build script expects.");
                Bind(handles[0], Cohort.ID);
                Bind(handles[1], root.ID);
                Bind(handles[2], automatic[0].ID);
                Bind(handles[3], automatic[1].ID);
                break;

            case "AddCohortSubContainer":
                Bind(handles[0], NewOne(line, Get<CohortAggregateContainer>(arguments[0]).GetSubContainers().Select(c => c.ID), before, "container"));
                break;

            case "AddCatalogueToCohortSet":
                var set = ((ExecuteCommandAddCatalogueToCohortSet)command).CreatedSet
                          ?? throw Stop(line, CohortScriptRefusal.LineHadNoEffect, "No set was made.");
                Bind(handles[0], set.ID);
                BindDimensions(line, set, handles.Skip(1));
                break;

            case "AddPatientIndexTable":
                var pit = (ExecuteCommandAddPatientIndexTable)command;
                if (pit.CreatedSet == null || pit.CreatedJoinable == null)
                    throw Stop(line, CohortScriptRefusal.LineHadNoEffect, "No patient index table was made.");
                Bind(handles[0], pit.CreatedSet.ID);
                Bind(handles[1], pit.CreatedJoinable.ID);
                BindDimensions(line, pit.CreatedSet, handles.Skip(2));
                break;

            case "AddNewFilterContainer":
                var owner = Get<AggregateConfiguration>(arguments[0]);
                Bind(handles[0], owner.RootFilterContainer_ID ??
                                 throw Stop(line, CohortScriptRefusal.LineHadNoEffect, $"Set {owner.ID} has no filter container after the line."));
                break;

            case "UseTopFilterContainer":
                Bind(handles[0], ((ExecuteCommandUseTopFilterContainer)command).Container?.ID ??
                                 throw Stop(line, CohortScriptRefusal.LineHadNoEffect, "The set has no top filter container after the line."));
                break;

            case "AddChildFilterContainer":
                Bind(handles[0], ((ExecuteCommandAddChildFilterContainer)command).CreatedContainer?.ID ??
                                 throw Stop(line, CohortScriptRefusal.LineHadNoEffect, "No filter container was made."));
                break;

            case "AddCohortFilter":
                Bind(handles[0], ((ExecuteCommandAddCohortFilter)command).CreatedFilter?.ID ??
                                 throw Stop(line, CohortScriptRefusal.LineHadNoEffect, "No filter was made."));
                break;
        }
    }

    // binds each dimension handle ($a12.chi) to the set's column of that name
    private void BindDimensions(ScriptFileLine line, AggregateConfiguration set, IEnumerable<string> handles)
    {
        var dimensions = _repository.GetAllObjectsWhere<AggregateDimension>("AggregateConfiguration_ID", set.ID);
        foreach (var handle in handles)
        {
            var column = CohortScriptFormat.SplitDimension(handle).Column;
            var matches = dimensions.Where(d =>
                string.Equals(d.ExtractionInformation?.GetRuntimeName(), column, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw Stop(line, CohortScriptRefusal.LineHadNoEffect,
                    $"Set {set.ID} has {matches.Length} column(s) called \"{column}\", so handle {handle} cannot be bound.");
            Bind(handle, matches[0].ID);
        }
    }

    private int NewOne(ScriptFileLine line, IEnumerable<int> after, HashSet<int> before, string what)
    {
        var made = after.Where(id => !before.Contains(id)).ToArray();
        if (made.Length != 1)
            throw Stop(line, CohortScriptRefusal.LineHadNoEffect, $"The line made {made.Length} {what}(s), not 1.");
        return made[0];
    }

    // the check after a line that changes rather than creates
    private void Verify(ScriptFileLine line, string[] arguments)
    {
        string problem = null;
        switch (line.Parsed.Command)
        {
            case "AddCohortFilter":
                var made = Get<AggregateFilter>($"AggregateFilter:{_ids[line.Parsed.Handles[0]]}");
                if (made.Name != arguments[1] || made.WhereSQL != arguments[2])
                    problem = $"Filter {made.ID} is called {Show(made.Name)} with SQL {Show(made.WhereSQL)}.";
                break;
            case "Set" or "SetText":
                var target = GetByTypedId(arguments[0]);
                var property = target.GetType().GetProperty(arguments[1]);
                var actual = property?.GetValue(target);
                if (!SameValue(actual, arguments[2]))
                    problem = $"{arguments[1]} is {Show(actual)}, not {Show(arguments[2])}.";
                break;
            case "AddCohortParameter":
                var owner = GetByTypedId(arguments[0]);
                var parameters = owner is AggregateFilter f
                    ? f.GetAllParameters()
                    : _repository.GetAllObjects<AnyTableSqlParameter>()
                        .Where(p => p.ReferencedObjectType == owner.GetType().Name && p.ReferencedObjectID == owner.ID)
                        .Cast<ISqlParameter>().ToArray();
                var parameter = parameters.SingleOrDefault(p => string.Equals(p.ParameterName, arguments[1], StringComparison.OrdinalIgnoreCase));
                if (parameter == null || parameter.Value != arguments[3] || parameter.ParameterSQL != arguments[2])
                    problem = $"{arguments[0]} has no parameter {arguments[1]} with the declaration and value of the line.";
                break;
            case "Delete":
                if (_deleting is IRevertable revertable && revertable.Exists())
                    problem = $"{arguments[0]} still exists. If you were asked to confirm the delete, the rebuild needs a yes.";
                break;
            case "LinkCohortToProject":
                var projectId = int.Parse(arguments[1][(arguments[1].IndexOf(':') + 1)..]);
                if (!_activator.RepositoryLocator.DataExportRepository
                        .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", Cohort.ID)
                        .Any(a => a.Project_ID == projectId))
                    problem = $"Cohort {Cohort.ID} is not linked to project {projectId}.";
                break;
            case "RenameCohortSet":
                var renamed = Get<AggregateConfiguration>(arguments[0]);
                if (renamed.Name != $"cic_{Cohort.ID}_{arguments[1]}")
                    problem = $"Set {renamed.ID} is called \"{renamed.Name}\".";
                break;
            case "SetFilterSource":
                var filter = Get<AggregateFilter>(arguments[0]);
                if (filter.ClonedFromExtractionFilter_ID?.ToString() != arguments[1][(arguments[1].IndexOf(':') + 1)..])
                    problem = $"Filter {filter.ID} records source {filter.ClonedFromExtractionFilter_ID?.ToString() ?? "none"}.";
                break;
            case "UsePatientIndexTable":
                var user = Get<AggregateConfiguration>(arguments[0]);
                if (!user.PatientIndexJoinablesUsed.Any(u => $"JoinableCohortAggregateConfiguration:{u.JoinableCohortAggregateConfiguration_ID}" == arguments[1]))
                    problem = $"Set {user.ID} does not join the patient index table.";
                break;
            case "AddForcedJoin":
                var joined = Get<AggregateConfiguration>(arguments[0]);
                if (!joined.ForcedJoins.Any(t => $"TableInfo:{t.ID}" == arguments[1]))
                    problem = $"Set {joined.ID} has no forced join to {arguments[1]}.";
                break;
            case "SetTopX":
                var limited = Get<AggregateConfiguration>(arguments[0]);
                var topX = limited.GetTopXIfAny();
                var orderBy = arguments[2] == null ? null : (int?)int.Parse(arguments[2][(arguments[2].IndexOf(':') + 1)..]);
                if (topX == null || topX.TopX.ToString() != arguments[1] || topX.OrderByDimensionIfAny_ID != orderBy ||
                    !string.Equals(topX.OrderByDirection.ToString(), arguments[3], StringComparison.OrdinalIgnoreCase))
                    problem = $"Set {limited.ID} does not keep {arguments[1]} rows ordered as the line says.";
                break;
        }

        if (problem != null)
            throw Stop(line, CohortScriptRefusal.LineHadNoEffect, $"{line.Parsed.Command} ran but did not take effect: {problem}");
    }

    // the resolved value is null only for the bare word null; quoted text, even "null", is compared as text
    private static bool SameValue(object actual, string expected)
    {
        if (expected == null)
            return actual == null;
        return actual switch
        {
            null => false,
            string s => s == expected,
            bool b => bool.TryParse(expected, out var e) && b == e,
            Enum en => string.Equals(en.ToString(), expected, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture), expected, StringComparison.Ordinal)
        };
    }

    private static string Show(object value) => value == null ? "no value" : $"\"{value}\"";

    private void Bind(string handle, int id) => _ids[handle] = id;

    private int IdOf(ScriptFileLine line, string handle) =>
        _ids.TryGetValue(handle, out var id)
            ? id
            : throw Stop(line, CohortScriptRefusal.HandleUsedBeforeCreated, $"Handle {handle} has no object yet.");

    private T Get<T>(string typedId) where T : IMapsDirectlyToDatabaseTable =>
        _repository.GetObjectByID<T>(int.Parse(typedId[(typedId.IndexOf(':') + 1)..]));

    private IMapsDirectlyToDatabaseTable GetByTypedId(string typedId)
    {
        CohortScriptFormat.TrySplitTyped(typedId, out var typeName, out var idText);
        return _repository.GetObjectByID(ObjectTypes[typeName], int.Parse(idText));
    }

    private static CohortScriptRebuildException Stop(ScriptFileLine line, CohortScriptRefusal reason, string message,
        Exception inner = null, CohortScriptRefusal? commandRefusal = null) =>
        new(line?.FileLine ?? 0, reason,
            $"Line {line?.FileLine} of the build script: {message} The rebuild stopped here.", inner, commandRefusal);
}
