// Adds a child filter container (AND) under a filter container, so filters can be grouped, for example
// "A AND (B OR C)". RDMP's AddNewFilterContainer takes only a set on the command line, so it can make a set's
// root filter container but not a child.
//
//   rdmp cmd AddChildFilterContainer AggregateFilterContainer:12

using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;

namespace RdmpCohortScript;

public class ExecuteCommandAddChildFilterContainer : CohortScriptCommand
{
    private readonly AggregateFilterContainer _parent;

    /// <summary>The new child container, once the command has run.</summary>
    public AggregateFilterContainer CreatedContainer { get; private set; }

    public ExecuteCommandAddChildFilterContainer(IBasicActivateItems activator,
        [DemandsInitialization("The filter container the new child goes under")]
        AggregateFilterContainer parent) : base(activator)
    {
        _parent = parent;

        if (parent == null)
            Refuse(CohortScriptRefusal.MissingArgument, "Give the filter container the new child goes under.");
        else
            RefuseIfReadOnly(parent, $"filter container {parent.ID}");
    }

    public override void Execute()
    {
        base.Execute();

        CreatedContainer = new AggregateFilterContainer(BasicActivator.RepositoryLocator.CatalogueRepository,
            FilterContainerOperation.AND);
        _parent.AddChild(CreatedContainer);
    }
}
