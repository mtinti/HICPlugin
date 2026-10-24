// Keeps only the first rows of a set (RDMP's Top X). RDMP 9.3.0 builds a cohort set's query without an ORDER BY,
// so the rows kept are not fixed, and the same cohort may return different patients from one run to the next.
// The column to order by is used by the set's graph.
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
        [DemandsInitialization("The column that orders the rows in the set's graph. Give null to order the graph by the count column. RDMP 9.3.0 writes no ORDER BY in a cohort query, so this does not decide which patients a cohort set keeps.")]
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
