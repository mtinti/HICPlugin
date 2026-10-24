// The rules of format 1 that the reader, the check and the rebuild share (COHORT-SCRIPT-FORMAT.md): the commands
// a build script may use, how many arguments each takes, the roles of the handles it binds, where it refers to an
// existing object by id and name, and the properties Set may change.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RdmpCohortScript;

/// <summary>The role of a handle: the kind of object it stands for.</summary>
internal enum HandleRole
{
    Cohort,
    Container,
    Set,
    Joinable,
    FilterContainer,
    Filter,
    Dimension
}

/// <summary>Where a line refers to an existing object: the argument with Type:id, and the one with its name.</summary>
internal sealed record ExistingReference(int IdArgument, int NameArgument, string TypeName);

/// <summary>What an argument position holds. The check enforces it whatever the quoting.</summary>
internal enum ArgumentKind
{
    /// <summary>An object this script made, written Type:$handle, bare.</summary>
    Handle,

    /// <summary>An existing object, written Type:id, bare.</summary>
    Reference,

    /// <summary>Text: quoted, or the bare word null where the command allows it.</summary>
    Text,

    /// <summary>SQL: quoted text in which {$j} markers are allowed and replaced.</summary>
    Sql,

    /// <summary>A bare word: true or false, a number, an enum name, a property name, or null.</summary>
    Word,

    /// <summary>The value of a SetText line: SQL or text, depending on the property.</summary>
    SetValue
}

/// <summary>One argument position: its kind, for handles the object types it accepts, and whether null is allowed.</summary>
internal sealed record ArgumentSpec(ArgumentKind Kind, string[] Types, bool OptionalHandle = false);

/// <summary>One command a build script may use.</summary>
internal sealed record ScriptCommandDefinition(
    string Name,
    ArgumentSpec[] ArgumentSpecs,
    HandleRole[] Outputs,
    bool MoreDimensions = false,
    ExistingReference Reference = null)
{
    public int Arguments => ArgumentSpecs.Length;
}

/// <summary>The commands, handles and properties of format 1.</summary>
internal static class CohortScriptFormat
{
    public const string Version = "1";

    private static ArgumentSpec H(params string[] types) => new(ArgumentKind.Handle, types);
    private static ArgumentSpec OptionalH(params string[] types) => new(ArgumentKind.Handle, types, true);
    private static readonly ArgumentSpec Ref = new(ArgumentKind.Reference, Array.Empty<string>());
    private static readonly ArgumentSpec Text = new(ArgumentKind.Text, Array.Empty<string>());
    private static readonly ArgumentSpec Sql = new(ArgumentKind.Sql, Array.Empty<string>());
    private static readonly ArgumentSpec Word = new(ArgumentKind.Word, Array.Empty<string>());

    public static readonly IReadOnlyDictionary<string, ScriptCommandDefinition> Commands =
        new Dictionary<string, ScriptCommandDefinition>(StringComparer.Ordinal)
        {
            ["CreateNewCohortIdentificationConfiguration"] = new("CreateNewCohortIdentificationConfiguration",
                new[] { Text },
                new[] { HandleRole.Cohort, HandleRole.Container, HandleRole.Container, HandleRole.Container }),
            ["LinkCohortToProject"] = new("LinkCohortToProject",
                new[] { H("CohortIdentificationConfiguration"), Ref, Text }, Array.Empty<HandleRole>(),
                Reference: new ExistingReference(1, 2, "Project")),
            ["AddCohortParameter"] = new("AddCohortParameter",
                new[] { H("CohortIdentificationConfiguration", "AggregateConfiguration", "AggregateFilter"), Text, Sql, Sql, Text },
                Array.Empty<HandleRole>()),
            ["AddCohortSubContainer"] = new("AddCohortSubContainer",
                new[] { H("CohortAggregateContainer") }, new[] { HandleRole.Container }),
            ["AddCatalogueToCohortSet"] = new("AddCatalogueToCohortSet",
                new[] { H("CohortAggregateContainer"), Ref, Text, Text, Word }, new[] { HandleRole.Set, HandleRole.Dimension },
                Reference: new ExistingReference(1, 2, "Catalogue")),
            ["AddPatientIndexTable"] = new("AddPatientIndexTable",
                new[] { H("CohortIdentificationConfiguration"), Ref, Text, Text, Text, Word },
                new[] { HandleRole.Set, HandleRole.Joinable, HandleRole.Dimension }, MoreDimensions: true,
                Reference: new ExistingReference(1, 2, "Catalogue")),
            ["UsePatientIndexTable"] = new("UsePatientIndexTable",
                new[] { H("AggregateConfiguration"), H("JoinableCohortAggregateConfiguration"), Word }, Array.Empty<HandleRole>()),
            ["AddForcedJoin"] = new("AddForcedJoin",
                new[] { H("AggregateConfiguration"), Ref, Text }, Array.Empty<HandleRole>(),
                Reference: new ExistingReference(1, 2, "TableInfo")),
            ["AddNewFilterContainer"] = new("AddNewFilterContainer",
                new[] { H("AggregateConfiguration") }, new[] { HandleRole.FilterContainer }),
            ["UseTopFilterContainer"] = new("UseTopFilterContainer",
                new[] { H("AggregateConfiguration") }, new[] { HandleRole.FilterContainer }),
            ["AddChildFilterContainer"] = new("AddChildFilterContainer",
                new[] { H("AggregateFilterContainer") }, new[] { HandleRole.FilterContainer }),
            ["AddCohortFilter"] = new("AddCohortFilter",
                new[] { H("AggregateFilterContainer"), Text, Sql }, new[] { HandleRole.Filter }),
            ["SetFilterSource"] = new("SetFilterSource",
                new[] { H("AggregateFilter"), Ref, Text }, Array.Empty<HandleRole>(),
                Reference: new ExistingReference(1, 2, "ExtractionFilter")),
            ["RenameCohortSet"] = new("RenameCohortSet",
                new[] { H("AggregateConfiguration"), Text }, Array.Empty<HandleRole>()),
            ["SetTopX"] = new("SetTopX",
                new[] { H("AggregateConfiguration"), Word, OptionalH("AggregateDimension"), Word }, Array.Empty<HandleRole>()),
            ["Set"] = new("Set",
                new[]
                {
                    H("CohortAggregateContainer", "AggregateConfiguration", "AggregateDimension", "AggregateFilterContainer",
                        "AggregateFilter"),
                    Word, Word
                }, Array.Empty<HandleRole>()),
            ["SetText"] = new("SetText",
                new[]
                {
                    H("CohortIdentificationConfiguration", "CohortAggregateContainer", "AggregateConfiguration",
                        "AggregateDimension", "AggregateFilter"),
                    Word, new ArgumentSpec(ArgumentKind.SetValue, Array.Empty<string>())
                }, Array.Empty<HandleRole>()),
            ["Delete"] = new("Delete", new[] { H("CohortAggregateContainer") }, Array.Empty<HandleRole>())
        };

    /// <summary>The properties whose SetText value is SQL, so {$j} markers are allowed and replaced in it.</summary>
    public static readonly ISet<string> SqlProperties = new HashSet<string>(StringComparer.Ordinal)
        { "HavingSQL", "CountSQL", "SelectSQL" };


    /// <summary>The object types a handle can stand for, and the role each one has.</summary>
    public static readonly IReadOnlyDictionary<string, HandleRole> HandleTypes =
        new Dictionary<string, HandleRole>(StringComparer.Ordinal)
        {
            ["CohortIdentificationConfiguration"] = HandleRole.Cohort,
            ["CohortAggregateContainer"] = HandleRole.Container,
            ["AggregateConfiguration"] = HandleRole.Set,
            ["JoinableCohortAggregateConfiguration"] = HandleRole.Joinable,
            ["AggregateFilterContainer"] = HandleRole.FilterContainer,
            ["AggregateFilter"] = HandleRole.Filter,
            ["AggregateDimension"] = HandleRole.Dimension
        };

    /// <summary>The plain-value properties RDMP's Set may change in a build script, by object type (section 7.1).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> WordProperties =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["CohortAggregateContainer"] = new[] { "Operation", "Order", "IsDisabled" },
            ["AggregateConfiguration"] = new[] { "Order", "IsDisabled" },
            ["AggregateDimension"] = new[] { "GroupBy" },
            ["AggregateFilterContainer"] = new[] { "Operation", "IsDisabled" },
            ["AggregateFilter"] = new[] { "IsDisabled" }
        };

    /// <summary>The text properties SetText may change, by object type (section 7.1).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> TextProperties =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["CohortIdentificationConfiguration"] = new[] { "Description", "Ticket", "Folder" },
            ["CohortAggregateContainer"] = new[] { "Name" },
            ["AggregateConfiguration"] = new[] { "HavingSQL", "CountSQL", "Description" },
            ["AggregateDimension"] = new[] { "SelectSQL", "Alias" },
            ["AggregateFilter"] = new[] { "Description" }
        };

    private static readonly Regex HandlePattern =
        new(@"^\$(?<prefix>cic|fc|a|c|f|j)(?<name>[0-9x][A-Za-z0-9]*)(?:\.(?<column>[^\s.""{}]+))?$", RegexOptions.CultureInvariant);

    /// <summary>The marker of a patient index table's id inside SQL: {$j17}.</summary>
    public static readonly Regex SqlMarker = new(@"\{(?<handle>\$j[0-9x][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant);

    /// <summary>Reads a handle's role from its text, or returns null when the text is not a handle.</summary>
    public static HandleRole? RoleOf(string handle)
    {
        var m = HandlePattern.Match(handle ?? "");
        if (!m.Success)
            return null;

        if (m.Groups["column"].Success)
            return m.Groups["prefix"].Value == "a" ? HandleRole.Dimension : null;

        return m.Groups["prefix"].Value switch
        {
            "cic" => HandleRole.Cohort,
            "c" => HandleRole.Container,
            "a" => HandleRole.Set,
            "j" => HandleRole.Joinable,
            "fc" => HandleRole.FilterContainer,
            "f" => HandleRole.Filter,
            _ => null
        };
    }

    /// <summary>The set handle and column of a dimension handle such as $a12.chi.</summary>
    public static (string SetHandle, string Column) SplitDimension(string handle)
    {
        var dot = handle.IndexOf('.');
        return (handle[..dot], handle[(dot + 1)..]);
    }

    /// <summary>Splits an argument such as AggregateFilter:$f9 into its type and the rest.</summary>
    public static bool TrySplitTyped(string argument, out string typeName, out string rest)
    {
        var colon = argument.IndexOf(':');
        typeName = colon > 0 ? argument[..colon] : null;
        rest = colon > 0 ? argument[(colon + 1)..] : null;
        return colon > 0;
    }
}
