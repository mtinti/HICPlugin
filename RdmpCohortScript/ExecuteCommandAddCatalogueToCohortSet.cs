// Adds a set to a cohort container: one catalogue, through a chosen identifier column, with or without the
// catalogue's mandatory filters. RDMP's own add-catalogue command asks which identifier column to use when a
// catalogue has several, and always copies the mandatory filters when run by name; this command takes both as
// arguments, so a build script can say them.
//
//   rdmp cmd AddCatalogueToCohortSet CohortAggregateContainer:12 Catalogue:717 "SMR01 - Hospital Admissions" CHI false

using System;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.CommandExecution.Combining;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;

namespace RdmpCohortScript;

public class ExecuteCommandAddCatalogueToCohortSet : CohortScriptCommand
{
    private readonly ExecuteCommandAddCatalogueToCohortIdentificationSetContainer _add;

    /// <summary>The set this command made, once it has run.</summary>
    public AggregateConfiguration CreatedSet => _add?.AggregateCreatedIfAny;

    public ExecuteCommandAddCatalogueToCohortSet(IBasicActivateItems activator,
        [DemandsInitialization("The container the set goes into")]
        CohortAggregateContainer container,
        [DemandsInitialization("The catalogue the set reads, by id")]
        Catalogue catalogue,
        [DemandsInitialization("The catalogue's name, to check the id. Give null to skip the check.")]
        string catalogueName,
        [DemandsInitialization("The identifier column to use, by its name in the catalogue. Give null when the catalogue has only one.")]
        string identifierColumn,
        [DemandsInitialization("true to copy the catalogue's current mandatory filters into the set, false to leave them out")]
        bool importMandatoryFilters) : base(activator)
    {
        if (container == null || catalogue == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the container and the catalogue for the new set.");
            return;
        }

        var cic = container.GetCohortIdentificationConfiguration();
        if (RefuseIfReadOnly(container, $"container {container.ID} of cohort {cic?.ID}"))
            return;

        var nameProblem = ExistingObjectName.Mismatch("catalogue", catalogue, catalogue.Name, catalogueName);
        if (nameProblem != null)
        {
            Refuse(CohortScriptRefusal.NameMismatch, nameProblem);
            return;
        }

        if (RefuseCatalogueTheCohortCannotUse(catalogue, cic))
            return;

        var identifier = IdentifierColumn.Find(catalogue, identifierColumn, out var refusal, out var columnProblem);
        if (identifier == null)
        {
            Refuse(refusal, columnProblem);
            return;
        }

        // RDMP 9.3.0 refuses an identifier column given to the constructor that takes a Catalogue: it compares
        // the column's catalogue item id with the catalogue's id. The constructor that takes a combineable carries
        // the column choice without that check. Its other checks are done above.
        var combineable = new CatalogueCombineable(catalogue)
        {
            ResolveMultipleExtractionIdentifiers = (_, _) => identifier
        };
        _add = new ExecuteCommandAddCatalogueToCohortIdentificationSetContainer(activator, combineable, container)
        {
            SkipMandatoryFilterCreation = !importMandatoryFilters
        };

        if (_add.IsImpossible)
            Refuse(CohortScriptRefusal.RdmpRefused,
                $"Cannot add catalogue {catalogue.ID} \"{catalogue.Name}\" to the container: {_add.ReasonCommandImpossible}");
    }

    public override void Execute()
    {
        base.Execute();
        _add.Execute();
    }
}

/// <summary>Finds a catalogue's identifier column by name, or the only one when no name is given.</summary>
internal static class IdentifierColumn
{
    public static ExtractionInformation Find(Catalogue catalogue, string name, out CohortScriptRefusal refusal,
        out string problem)
    {
        if (catalogue.IsApiCall())
        {
            refusal = CohortScriptRefusal.ApiCatalogue;
            problem = $"Catalogue {catalogue.ID} \"{catalogue.Name}\" is served by an API and has no identifier column. A build script cannot use it.";
            return null;
        }

        var identifiers = IdentifiersOf(catalogue);
        var names = string.Join(", ", identifiers.Select(e => e.GetRuntimeName()));
        refusal = default;
        problem = null;

        if (identifiers.Length == 0)
        {
            refusal = CohortScriptRefusal.NoIdentifierColumn;
            problem = $"Catalogue {catalogue.ID} \"{catalogue.Name}\" has no identifier column. Mark the patient identifier column as an extraction identifier in the catalogue.";
            return null;
        }

        if (name == null)
        {
            if (identifiers.Length == 1)
                return identifiers[0];

            refusal = CohortScriptRefusal.AmbiguousIdentifierColumn;
            problem = $"Catalogue {catalogue.ID} \"{catalogue.Name}\" has {identifiers.Length} identifier columns ({names}). Give the one to use.";
            return null;
        }

        var match = identifiers.SingleOrDefault(e =>
            string.Equals(e.GetRuntimeName(), name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            refusal = CohortScriptRefusal.UnknownIdentifierColumn;
            problem = $"Catalogue {catalogue.ID} \"{catalogue.Name}\" has no identifier column called \"{name}\". Its identifier columns are: {names}.";
        }

        return match;
    }

    public static ExtractionInformation[] IdentifiersOf(Catalogue catalogue) =>
        catalogue.GetAllExtractionInformation(ExtractionCategory.Any).Where(e => e.IsExtractionIdentifier).ToArray();
}
