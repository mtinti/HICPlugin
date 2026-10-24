// Writes the reference of the commands a build script may use, from the same command table the check and the
// rebuild use, and from the descriptions on each command's own arguments. The README, the user notes and an
// agent's prompt take their command list from this output, so they cannot drift from the code.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;

namespace RdmpCohortScript;

/// <summary>Writes the command reference for build scripts, format 1, as Markdown.</summary>
internal static class CohortScriptReference
{
    /// <summary>
    /// One example line per command, in an order that makes them one valid script: each handle is made before a line
    /// uses it. The ids of existing objects are made up.
    /// </summary>
    public static readonly IReadOnlyList<string> Examples = new[]
    {
        "CreateNewCohortIdentificationConfiguration \"Diabetes since 2010\" => $cic1 $c1 $c2 $c3",
        "LinkCohortToProject CohortIdentificationConfiguration:$cic1 Project:5 \"Diabetes study\"",
        "SetText CohortIdentificationConfiguration:$cic1 Description \"People with diabetes, diagnosed since 2010\"",
        "AddCohortParameter CohortIdentificationConfiguration:$cic1 \"@since\" \"DECLARE @since AS date\" \"'2010-01-01'\" \"Start of the study\"",
        "AddPatientIndexTable CohortIdentificationConfiguration:$cic1 Catalogue:21 \"Hospital admissions\" \"chi\" \"admission_date\" false => $a1 $j1 $a1.chi $a1.admission_date",
        "AddNewFilterContainer AggregateConfiguration:$a1 => $fc3",
        "AddCohortFilter AggregateFilterContainer:$fc3 \"Emergency admissions\" \"admission_type = @admission_type\" => $f3",
        "AddCohortParameter AggregateFilter:$f3 \"@admission_type\" \"DECLARE @admission_type AS varchar(20)\" \"'Emergency'\" \"The kind of admission counted\"",
        "Set CohortAggregateContainer:$c2 Operation INTERSECT",
        "AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:22 \"Prescribing\" \"chi\" true => $a2 $a2.chi",
        "RenameCohortSet AggregateConfiguration:$a2 \"Prescribed after an admission\"",
        "UsePatientIndexTable AggregateConfiguration:$a2 JoinableCohortAggregateConfiguration:$j1 Inner",
        "AddForcedJoin AggregateConfiguration:$a2 TableInfo:31 \"[Data]..[DrugLookup]\"",
        "UseTopFilterContainer AggregateConfiguration:$a2 => $fc1",
        "AddCohortFilter AggregateFilterContainer:$fc1 \"After an admission\" \"prescribed_date > ix{$j1}.admission_date AND prescribed_date >= @since\" => $f1",
        "AddChildFilterContainer AggregateFilterContainer:$fc1 => $fc2",
        "Set AggregateFilterContainer:$fc2 Operation OR",
        "AddCohortFilter AggregateFilterContainer:$fc2 \"Metformin\" \"drug_name = 'Metformin'\" => $f2",
        "SetFilterSource AggregateFilter:$f2 ExtractionFilter:41 \"Metformin\"",
        "SetTopX AggregateConfiguration:$a2 100 AggregateDimension:$a2.chi Descending",
        "AddCohortSubContainer CohortAggregateContainer:$c1 => $c4",
        "Delete CohortAggregateContainer:$c3"
    };

    public static string Markdown(IBasicActivateItems activator)
    {
        var types = CohortScriptRunner.CommandTypes(activator);
        var sb = new StringBuilder();

        sb.AppendLine($"# Build script commands, format {CohortScriptFormat.Version}");
        sb.AppendLine();
        sb.AppendLine("A build script is a YAML file whose Commands list holds one RDMP command per line. Every line is a");
        sb.AppendLine("double-quoted string. A line is the command, its arguments, and, after =>, the handles it binds:");
        sb.AppendLine();
        sb.AppendLine("    AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:22 \"Prescribing\" \"chi\" true => $a2 $a2.chi");
        sb.AppendLine();
        sb.AppendLine("Run a script with BuildCohortFromScript, and check it first with CheckCohortScript.");
        sb.AppendLine();

        sb.AppendLine("## Handles and existing objects");
        sb.AppendLine();
        sb.AppendLine("- A handle names an object the script makes. It is $ with a prefix and a name that starts with a digit");
        sb.AppendLine("  or x: $cic (the cohort), $c (a container), $a (a set), $j (a patient index table), $fc (a filter");
        sb.AppendLine("  container) and $f (a filter). A column of a set is the set's handle, a dot and the column name, as");
        sb.AppendLine("  in $a2.chi.");
        sb.AppendLine("- A line binds each handle once, and only a later line may use it. A line refers to a handle with its");
        sb.AppendLine("  type, bare, as in AggregateConfiguration:$a2.");
        sb.AppendLine("- A script changes only what it makes. It refers to an existing catalogue, table, project or published");
        sb.AppendLine("  filter by type and id, followed by the object's name in quotes. The check refuses a name that differs");
        sb.AppendLine("  from the object's current name.");
        sb.AppendLine("- Text and SQL are in double quotes and kept exactly. The bare word null means no value. Plain values");
        sb.AppendLine("  (true, false, numbers, words such as INTERSECT) are bare. true and false are in lower case.");
        sb.AppendLine("- In SQL, ix{$j1} is the alias of patient index table $j1, as in ix{$j1}.admission_date.");
        sb.AppendLine("- Every parameter a filter uses (@name) is declared with AddCohortParameter where RDMP gives it to the");
        sb.AppendLine("  filter: on the cohort, on the filter's set or any filter of that set, or on a patient index table the");
        sb.AppendLine("  set joins (its set or any of its filters). A mandatory filter that a set line copies brings its");
        sb.AppendLine("  parameters, which count like those of the set's other filters. A parameter of another set does not");
        sb.AppendLine("  count, because a set run alone would fail on it.");
        sb.AppendLine();

        sb.AppendLine("## Order of lines");
        sb.AppendLine();
        sb.AppendLine("1. The first line creates the cohort. RDMP makes three containers with it: the root (EXCEPT), Inclusion");
        sb.AppendLine("   Criteria and Exclusion Criteria (both UNION). The line binds the cohort and the three containers, in");
        sb.AppendLine("   that order. A script reuses them, and deletes one it does not need with Delete.");
        sb.AppendLine("2. LinkCohortToProject comes before any set on a catalogue that is specific to that project.");
        sb.AppendLine("3. A patient index table comes before every set that joins it.");
        sb.AppendLine("4. RDMP adds each new child at the top of its container. After every child of a container exists, one");
        sb.AppendLine("   Set ... Order line per child gives the order. The order matters for EXCEPT.");
        sb.AppendLine("5. IsDisabled lines come last, after the structure is built.");
        sb.AppendLine();

        sb.AppendLine("## Mandatory filters");
        sb.AppendLine();
        sb.AppendLine("A catalogue's mandatory filters are the filters that every use of it should apply. The catalogue");
        sb.AppendLine("manifest marks them. The last argument of AddCatalogueToCohortSet and AddPatientIndexTable says whether");
        sb.AppendLine("to copy them into the new set. A new cohort normally copies them: write true. An exported script");
        sb.AppendLine("writes false, because it already holds the filters as the cohort saved them, each with a SetFilterSource");
        sb.AppendLine("line naming the mandatory filter it came from. When you start from an exported script, write true and");
        sb.AppendLine("leave out those filters. CheckCohortScript with its second argument true warns about every false on a");
        sb.AppendLine("catalogue that has mandatory filters.");
        sb.AppendLine();
        sb.AppendLine("When RDMP copies mandatory filters, it puts them in a new top filter container (AND) of the set, and");
        sb.AppendLine("AddNewFilterContainer then fails, because a set has one top filter container. After a line that");
        sb.AppendLine("writes true, use UseTopFilterContainer: it binds the container the set has, or makes one when the");
        sb.AppendLine("catalogue has no mandatory filters. The set's own filters then go beside the mandatory ones.");
        sb.AppendLine();

        sb.AppendLine("## Commands");
        foreach (var (name, definition) in CohortScriptFormat.Commands.OrderBy(c => Array.FindIndex(Examples.ToArray(),
                         e => e.StartsWith(c.Key + " ", StringComparison.Ordinal))))
        {
            sb.AppendLine();
            sb.AppendLine($"### {name}");
            sb.AppendLine();
            if (types.TryGetValue(name, out var type) && Summary(type) is { } summary)
            {
                sb.AppendLine(summary);
                sb.AppendLine();
            }

            var parameters = type == null
                ? Array.Empty<ParameterInfo>()
                : CohortScriptRunner.Constructors(type, definition.Arguments).FirstOrDefault()?.GetParameters().Skip(1).ToArray() ??
                  Array.Empty<ParameterInfo>();

            sb.AppendLine("Arguments:");
            sb.AppendLine();
            for (var i = 0; i < definition.Arguments; i++)
            {
                var spec = definition.ArgumentSpecs[i];
                var parameter = i < parameters.Length ? parameters[i] : null;
                var description = RdmpArguments.TryGetValue(name, out var own)
                    ? own[i]
                    : parameter?.GetCustomAttribute<DemandsInitializationAttribute>()?.Description;
                sb.AppendLine($"{i + 1}. {parameter?.Name ?? $"argument {i + 1}"} ({Kind(spec)}){(description == null ? "" : $": {description}")}");
            }

            if (definition.Reference is { } reference)
            {
                sb.AppendLine();
                sb.AppendLine($"In a build script, argument {reference.NameArgument + 1} always gives the {Kinds[reference.TypeName]}'s name, so the check can catch a wrong id.");
            }

            var outputs = Outputs(definition);
            if (outputs != null)
            {
                sb.AppendLine();
                sb.AppendLine($"Binds: {outputs}.");
            }

            if (name is "Set" or "SetText")
            {
                var properties = name == "Set" ? CohortScriptFormat.WordProperties : CohortScriptFormat.TextProperties;
                sb.AppendLine();
                sb.AppendLine("Properties it may change:");
                sb.AppendLine();
                foreach (var (typeName, allowed) in properties)
                    sb.AppendLine($"- {typeName}: {string.Join(", ", allowed)}");
            }

            sb.AppendLine();
            sb.AppendLine("Example:");
            sb.AppendLine();
            foreach (var example in Examples.Where(e => e.StartsWith(name + " ", StringComparison.Ordinal)))
                sb.AppendLine($"    {example}");
        }

        return sb.ToString();
    }

    // header comments are not available at run time, so each command's one-line summary is kept here
    private static string Summary(Type type) => Summaries.GetValueOrDefault(BasicCommandExecution.GetCommandName(type.Name));

    private static readonly Dictionary<string, string> Summaries = new(StringComparer.Ordinal)
    {
        ["CreateNewCohortIdentificationConfiguration"] = "Creates the cohort. RDMP's own command.",
        ["LinkCohortToProject"] = "Links the cohort to an existing project.",
        ["AddCohortParameter"] = "Declares a parameter on the cohort, a set or a filter, with its value.",
        ["AddCohortSubContainer"] = "Adds a container inside a container. RDMP's own command.",
        ["AddCatalogueToCohortSet"] = "Adds a set to a container: one catalogue, through one identifier column.",
        ["AddPatientIndexTable"] = "Adds a patient index table to the cohort: a set that other sets join, with extra columns.",
        ["UsePatientIndexTable"] = "Makes a set join a patient index table.",
        ["AddForcedJoin"] = "Makes a set join one more table of its catalogue, even when no column needs it.",
        ["AddNewFilterContainer"] = "Gives a set its top filter container. RDMP's own command.",
        ["UseTopFilterContainer"] = "Binds a set's top filter container, and makes one (AND) when the set has none. Use it after a set line that copies mandatory filters.",
        ["AddChildFilterContainer"] = "Adds a filter container inside a filter container.",
        ["AddCohortFilter"] = "Adds a filter to a filter container, with exactly the name and SQL given.",
        ["SetFilterSource"] = "Records which published filter a filter was copied from.",
        ["RenameCohortSet"] = "Renames a set. RDMP keeps its cic_<id>_ prefix in front of the name.",
        ["SetTopX"] = "Keeps only the first rows of a set.",
        ["Set"] = "Sets a plain value: an operation, an order, or true or false. RDMP's own command.",
        ["SetText"] = "Sets a text property to exactly the text given.",
        ["Delete"] = "Deletes a container the script made. In a build script, only RDMP's automatic containers. RDMP's own command."
    };

    // RDMP's own commands describe their arguments for every use, not for a build script (Delete mentions deleting
    // many objects, Set suggests Description, which goes through SetText), or not at all
    private static readonly Dictionary<string, string[]> RdmpArguments = new(StringComparer.Ordinal)
    {
        ["CreateNewCohortIdentificationConfiguration"] = new[] { "The new cohort's name. BuildCohortFromScript replaces it with the name it is given." },
        ["AddCohortSubContainer"] = new[] { "The container the new container goes into" },
        ["AddNewFilterContainer"] = new[] { "The set that gets the filter container. A set has one top filter container." },
        ["Set"] = new[]
        {
            "The object to change",
            "The property to change. The list below gives the properties a build script may change.",
            "The new value: an operation (UNION, INTERSECT, EXCEPT, AND, OR), a number, or true or false"
        },
        ["Delete"] = new[] { "The container to delete: one of the three the first line made, which the cohort does not use" }
    };

    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["Catalogue"] = "catalogue",
        ["TableInfo"] = "table",
        ["Project"] = "project",
        ["ExtractionFilter"] = "published filter"
    };

    private static string Kind(ArgumentSpec spec) => spec.Kind switch
    {
        ArgumentKind.Handle => $"handle: {string.Join(" or ", spec.Types.Select(t => $"{t}:$..."))}{(spec.OptionalHandle ? ", or null" : "")}",
        ArgumentKind.Reference => "existing object: Type:id",
        ArgumentKind.Text => "text in quotes, or null",
        ArgumentKind.Sql => "SQL in quotes, or null",
        ArgumentKind.Word => "plain word",
        _ => "text or SQL in quotes, or null"
    };

    private static string Outputs(ScriptCommandDefinition definition)
    {
        if (definition.Outputs.Length == 0)
            return null;

        var names = definition.Outputs.Select(r => r switch
        {
            HandleRole.Cohort => "the cohort ($cic)",
            HandleRole.Container => "a container ($c)",
            HandleRole.Set => "the set ($a)",
            HandleRole.Joinable => "the patient index table ($j)",
            HandleRole.FilterContainer => "the filter container ($fc)",
            HandleRole.Filter => "the filter ($f)",
            _ => "the identifier column ($a.column)"
        }).ToList();
        if (definition.Name == "CreateNewCohortIdentificationConfiguration")
            names = new List<string> { "the cohort ($cic)", "the root container ($c)", "Inclusion Criteria ($c)", "Exclusion Criteria ($c)" };
        if (definition.MoreDimensions)
            names.Add("then one column ($a.column) for each extra column");
        return string.Join(", ", names);
    }
}
