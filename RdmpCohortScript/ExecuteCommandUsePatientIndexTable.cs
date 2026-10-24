// Makes a set join a patient index table of the same cohort, so the set's filters can use the table's columns
// as ix<joinable id>.<column>. No RDMP command makes this link.
//
//   rdmp cmd UsePatientIndexTable AggregateConfiguration:40 JoinableCohortAggregateConfiguration:3 Inner

using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort.Joinables;

namespace RdmpCohortScript;

public class ExecuteCommandUsePatientIndexTable : CohortScriptCommand
{
    private readonly AggregateConfiguration _set;
    private readonly JoinableCohortAggregateConfiguration _joinable;
    private readonly ExtractionJoinType _joinType;

    public ExecuteCommandUsePatientIndexTable(IBasicActivateItems activator,
        [DemandsInitialization("The set that joins the patient index table")]
        AggregateConfiguration set,
        [DemandsInitialization("The patient index table, by its joinable")]
        JoinableCohortAggregateConfiguration joinable,
        [DemandsInitialization("How the set joins the table: Inner, Left or Right")]
        ExtractionJoinType joinType) : base(activator)
    {
        _set = set;
        _joinable = joinable;
        _joinType = joinType;

        if (set == null || joinable == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the set and the patient index table it joins.");
            return;
        }

        if (RefuseIfReadOnly(set, $"set {set.ID}"))
            return;

        if (joinable.AggregateConfiguration_ID == set.ID)
        {
            Refuse(CohortScriptRefusal.JoinsItself, $"Set {set.ID} is the patient index table itself, so it cannot join it.");
            return;
        }

        var setCohort = set.GetCohortIdentificationConfigurationIfAny();
        if (setCohort == null)
        {
            Refuse(CohortScriptRefusal.NotInACohort,
                $"Set {set.ID} belongs to no cohort. Only a set of a cohort can join a patient index table.");
            return;
        }

        if (setCohort.ID != joinable.CohortIdentificationConfiguration_ID)
        {
            Refuse(CohortScriptRefusal.DifferentCohorts,
                $"Set {set.ID} and patient index table {joinable.ID} belong to different cohorts. A set can join only a patient index table of its own cohort.");
            return;
        }

        if (set.PatientIndexJoinablesUsed.Any())
            Refuse(CohortScriptRefusal.AlreadyJoinsATable, $"Set {set.ID} already joins a patient index table. RDMP allows one per set.");
    }

    public override void Execute()
    {
        base.Execute();

        var use = _joinable.AddUser(_set);
        use.JoinType = _joinType;
        use.SaveToDatabase();
    }
}
