// Adds a filter to a filter container, with exactly the name and SQL the line gives. RDMP's CreateNewFilter reads
// its arguments through the command line, so it reads a name such as ExtractionFilter:12 as "import that published
// filter", the word null as no value, and ignores a name or SQL that is only spaces. A build script needs the saved
// filter back exactly.
//
//   rdmp cmd AddCohortFilter AggregateFilterContainer:12 "Diabetes since 2010" "main_condition LIKE @code"

using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.FilterImporting.Construction;

namespace RdmpCohortScript;

public class ExecuteCommandAddCohortFilter : CohortScriptCommand
{
    private readonly AggregateFilterContainer _container;
    private readonly string _name;
    private readonly string _whereSql;

    /// <summary>The new filter, once the command has run.</summary>
    public AggregateFilter CreatedFilter { get; private set; }

    public ExecuteCommandAddCohortFilter(IBasicActivateItems activator,
        [DemandsInitialization("The filter container the filter goes into")]
        AggregateFilterContainer container,
        [DemandsInitialization("The filter's name, exactly as it should be stored")]
        string name,
        [DemandsInitialization("The filter's SQL, exactly as it should be stored. Give null for none.")]
        string whereSql) : base(activator)
    {
        _container = container;
        _name = name;
        _whereSql = whereSql;

        if (container == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the filter container the filter goes into.");
            return;
        }

        if (RefuseIfReadOnly(container, $"filter container {container.ID}"))
            return;

        // the platform database requires a filter name
        if (string.IsNullOrWhiteSpace(name))
            Refuse(CohortScriptRefusal.MissingArgument,
                $"Give a name for the new filter in filter container {container.ID}. RDMP requires every filter to have one.");
    }

    public override void Execute()
    {
        base.Execute();

        // made the way RDMP's own command makes a cohort filter: through the factory, then added to the container
        var filter = (AggregateFilter)new AggregateFilterFactory(BasicActivator.RepositoryLocator.CatalogueRepository)
            .CreateNewFilter(_name);
        _container.AddChild(filter);
        filter.Name = _name;
        filter.WhereSQL = _whereSql;
        filter.SaveToDatabase();
        CreatedFilter = filter;
    }
}
