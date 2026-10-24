// Keeps only the first rows of a set (RDMP's Top X). Without an order-by column the rows kept are not fixed,
// so the same cohort may return different patients from one run to the next.
//
//   rdmp cmd SetTopX AggregateConfiguration:40 100 AggregateDimension:55 Descending

using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;

namespace RdmpCohortScript;

public class ExecuteCommandSetTopX : CohortScriptCommand
{
    private readonly AggregateConfiguration _set;
    private readonly int _topX;
    private readonly AggregateDimension _orderBy;
    private readonly AggregateTopXOrderByDirection _direction;

    public ExecuteCommandSetTopX(IBasicActivateItems activator,
        [DemandsInitialization("The set")]
        AggregateConfiguration set,
        [DemandsInitialization("How many rows to keep")]
        int topX,
        [DemandsInitialization("The column of the set that decides which rows come first. Give null to order by the count column.")]
        AggregateDimension orderBy,
        [DemandsInitialization("Ascending or Descending")]
        AggregateTopXOrderByDirection direction) : base(activator)
    {
        _set = set;
        _topX = topX;
        _orderBy = orderBy;
        _direction = direction;

        if (set == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the set to limit.");
            return;
        }

        if (RefuseIfReadOnly(set, $"set {set.ID}"))
            return;

        if (topX <= 0)
        {
            Refuse(CohortScriptRefusal.BadRowCount, $"The number of rows to keep must be at least 1, not {topX}.");
            return;
        }

        if (orderBy != null && orderBy.AggregateConfiguration_ID != set.ID)
            Refuse(CohortScriptRefusal.OrderByOtherSet, $"Column {orderBy.ID} belongs to another set. Order set {set.ID} by one of its own columns.");
    }

    public override void Execute()
    {
        base.Execute();

        var topX = _set.GetTopXIfAny() ??
                   new AggregateTopX(BasicActivator.RepositoryLocator.CatalogueRepository, _set, _topX);
        topX.TopX = _topX;
        topX.OrderByDimensionIfAny_ID = _orderBy?.ID;
        topX.OrderByDirection = _direction;
        topX.SaveToDatabase();
    }
}
