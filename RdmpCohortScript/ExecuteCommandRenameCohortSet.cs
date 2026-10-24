// Renames a cohort set. RDMP names every set cic_<cohort id>_<name>, and the prefix matters: RDMP selects all
// of a set's columns only when its name starts with cic_. So the command keeps the prefix of the set's own
// cohort and changes only the rest. A plain Set of Name would need the cohort's id, which a script does not know.
//
//   rdmp cmd RenameCohortSet AggregateConfiguration:40 "Prescribed near an admission"

using System;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;

namespace RdmpCohortScript;

public class ExecuteCommandRenameCohortSet : CohortScriptCommand
{
    private readonly AggregateConfiguration _set;
    private readonly CohortIdentificationConfiguration _cic;
    private readonly string _name;

    public ExecuteCommandRenameCohortSet(IBasicActivateItems activator,
        [DemandsInitialization("The set to rename")]
        AggregateConfiguration set,
        [DemandsInitialization("The new name, without RDMP's cic_<id>_ prefix")]
        string name) : base(activator)
    {
        _set = set;
        _name = name;
        _cic = set?.GetCohortIdentificationConfigurationIfAny();

        if (set == null || _cic == null)
        {
            Refuse(set == null ? CohortScriptRefusal.MissingArgument : CohortScriptRefusal.NotInACohort,
                "Give a set that belongs to a cohort.");
            return;
        }

        if (RefuseIfReadOnly(set, $"set {set.ID}"))
            return;

        if (string.IsNullOrWhiteSpace(name))
        {
            Refuse(CohortScriptRefusal.EmptyName, $"Give a new name for set {set.ID}.");
            return;
        }

        if (name.StartsWith("cic_", StringComparison.OrdinalIgnoreCase))
        {
            Refuse(CohortScriptRefusal.NameHasPrefix,
                "Give the name without RDMP's cic_<id>_ prefix. The command adds the prefix of the set's cohort.");
            return;
        }

        // RDMP adds "(Copy 1)" when any aggregate in the catalogue already has the name, also one that is in no
        // cohort tree; refusing keeps the name the script gives
        var fullName = $"cic_{_cic.ID}_{name}";
        var taken = activator.RepositoryLocator.CatalogueRepository.GetAllObjects<AggregateConfiguration>()
            .FirstOrDefault(a => a.ID != set.ID && a.Name == fullName);
        if (taken != null)
            Refuse(CohortScriptRefusal.NameTaken,
                $"Aggregate {taken.ID} is already called \"{fullName}\", and RDMP needs the name to be unique. Give set {set.ID} another name, or rename or delete aggregate {taken.ID}.");
    }

    public override void Execute()
    {
        base.Execute();

        _set.Name = _name;
        _cic.EnsureNamingConvention(_set);
        _set.SaveToDatabase();
    }
}
