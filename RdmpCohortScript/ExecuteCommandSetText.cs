// Sets a text property of a cohort object to exactly the text the line gives: a name, a description, a ticket, a
// folder, HAVING or count SQL, a column's SQL or alias. RDMP's Set reads its value through the command line again,
// so it reads Catalogue:12 as a catalogue and the word null as no value. Plain values (an operation, an order, a
// true or false) still use RDMP's Set.
//
//   rdmp cmd SetText AggregateConfiguration:40 HavingSQL "count(*) >= 2"

using System;
using System.Collections.Generic;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.MapsDirectlyToDatabaseTable;

namespace RdmpCohortScript;

public class ExecuteCommandSetText : CohortScriptCommand
{
    private readonly IMapsDirectlyToDatabaseTable _target;
    private readonly System.Reflection.PropertyInfo _property;
    private readonly string _text;

    public ExecuteCommandSetText(IBasicActivateItems activator,
        [DemandsInitialization("The cohort object to change: a cohort, container, set, column of a set, or filter")]
        IMapsDirectlyToDatabaseTable target,
        [DemandsInitialization("The text property to set, for example Description or HavingSQL")]
        string property,
        [DemandsInitialization("The text, exactly as it should be stored. Give null for none.")]
        string text) : base(activator)
    {
        _target = target;
        _text = text;

        if (target == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the object to change.");
            return;
        }

        var typeName = target.GetType().Name;
        if (!CohortScriptFormat.TextProperties.TryGetValue(typeName, out var allowed) ||
            !allowed.Contains(property, StringComparer.Ordinal))
        {
            Refuse(CohortScriptRefusal.PropertyNotAllowed,
                $"SetText cannot set {property} on {typeName}. It sets: {Allowed()}.");
            return;
        }

        if (target is IMightBeReadOnly readOnly && RefuseIfReadOnly(readOnly, $"{typeName} {target.ID}"))
            return;

        _property = target.GetType().GetProperty(property);
    }

    public override void Execute()
    {
        base.Execute();
        _property.SetValue(_target, _text);
        ((ISaveable)_target).SaveToDatabase();
    }

    private static string Allowed() => string.Join("; ",
        CohortScriptFormat.TextProperties.Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}"));
}
