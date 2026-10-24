// Records which published filter a cohort filter was copied from. It does not change the query: the filter
// keeps its own SQL. With the manifest's mandatory flag, it shows which saved filters are copies of mandatory
// filters.
//
//   rdmp cmd SetFilterSource AggregateFilter:9 ExtractionFilter:6454 "Adults only"

using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;

namespace RdmpCohortScript;

public class ExecuteCommandSetFilterSource : CohortScriptCommand
{
    private readonly AggregateFilter _filter;
    private readonly ExtractionFilter _source;

    public ExecuteCommandSetFilterSource(IBasicActivateItems activator,
        [DemandsInitialization("The cohort filter")]
        AggregateFilter filter,
        [DemandsInitialization("The published filter it was copied from, by id")]
        ExtractionFilter source,
        [DemandsInitialization("The published filter's name, to check the id. Give null to skip the check.")]
        string sourceName) : base(activator)
    {
        _filter = filter;
        _source = source;

        if (filter == null || source == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the cohort filter and the published filter it was copied from.");
            return;
        }

        if (RefuseIfReadOnly(filter, $"filter {filter.ID} \"{filter.Name}\""))
            return;

        var nameProblem = ExistingObjectName.Mismatch("published filter", source, source.Name, sourceName);
        if (nameProblem != null)
            Refuse(CohortScriptRefusal.NameMismatch, nameProblem);
    }

    public override void Execute()
    {
        base.Execute();

        _filter.ClonedFromExtractionFilter_ID = _source.ID;
        _filter.SaveToDatabase();
    }
}
