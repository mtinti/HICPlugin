// Forces a table into a set's query even when no column of the set comes from it. An inner join to that table
// can drop rows, so a forced join can change the patients. No RDMP command makes this link.
//
//   rdmp cmd AddForcedJoin AggregateConfiguration:40 TableInfo:31 "[Research]..[DrugLookup]"

using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;

namespace RdmpCohortScript;

public class ExecuteCommandAddForcedJoin : CohortScriptCommand
{
    private readonly AggregateConfiguration _set;
    private readonly TableInfo _table;

    public ExecuteCommandAddForcedJoin(IBasicActivateItems activator,
        [DemandsInitialization("The set whose query gets the table")]
        AggregateConfiguration set,
        [DemandsInitialization("The table to join, by id")]
        TableInfo table,
        [DemandsInitialization("The table's name, to check the id. Give null to skip the check.")]
        string tableName) : base(activator)
    {
        _set = set;
        _table = table;

        if (set == null || table == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the set and the table to join.");
            return;
        }

        if (RefuseIfReadOnly(set, $"set {set.ID}"))
            return;

        var nameProblem = ExistingObjectName.Mismatch("table", table, table.Name, tableName);
        if (nameProblem != null)
        {
            Refuse(CohortScriptRefusal.NameMismatch, nameProblem);
            return;
        }

        if (SavedForcedJoins.Of(set).Any(t => t.ID == table.ID))
            Refuse(CohortScriptRefusal.AlreadyForcedJoin, $"Set {set.ID} already has a forced join to table {table.ID} \"{table.Name}\".");
    }

    public override void Execute()
    {
        base.Execute();
        BasicActivator.RepositoryLocator.CatalogueRepository.AggregateForcedJoinManager.CreateLinkBetween(_set, _table);
    }
}
