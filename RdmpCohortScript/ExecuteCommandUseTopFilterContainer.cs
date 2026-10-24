// Gives a script a handle to a set's top filter container, and makes the container (AND) when the set has none.
// When a set copies its catalogue's mandatory filters, RDMP puts them in a new top filter container, and RDMP's
// AddNewFilterContainer then fails because the set has one. This command lets the script add its own filters
// beside the mandatory ones.
//
//   rdmp cmd UseTopFilterContainer AggregateConfiguration:40

using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;

namespace RdmpCohortScript;

public class ExecuteCommandUseTopFilterContainer : CohortScriptCommand
{
    private readonly AggregateConfiguration _set;

    /// <summary>The set's top filter container, once the command has run.</summary>
    public AggregateFilterContainer Container { get; private set; }

    public ExecuteCommandUseTopFilterContainer(IBasicActivateItems activator,
        [DemandsInitialization("The set whose top filter container the script uses. The command makes one (AND) when the set has none.")]
        AggregateConfiguration set) : base(activator)
    {
        _set = set;

        if (set == null)
            Refuse(CohortScriptRefusal.MissingArgument, "Give the set whose top filter container the script uses.");
        else if (set.OverrideFiltersByUsingParentAggregateConfigurationInstead_ID != null)
            Refuse(CohortScriptRefusal.RdmpRefused,
                $"Set {set.ID} \"{set.Name}\" uses the filters of another aggregate, so it has no filter container of its own.");
        else
            RefuseIfReadOnly(set, $"set {set.ID}");
    }

    public override void Execute()
    {
        base.Execute();

        if (_set.RootFilterContainer is AggregateFilterContainer existing)
        {
            Container = existing;
            return;
        }

        Container = new AggregateFilterContainer(BasicActivator.RepositoryLocator.CatalogueRepository,
            FilterContainerOperation.AND);
        _set.RootFilterContainer_ID = Container.ID;
        _set.SaveToDatabase();
    }
}
