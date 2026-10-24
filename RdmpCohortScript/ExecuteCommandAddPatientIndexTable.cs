// Adds a patient index table to a cohort: a set on one catalogue that other sets join to, for example to use
// each patient's admission dates. It selects the identifier column and the extra columns given. RDMP's own
// command for this takes an argument a script cannot give, so the plugin has its own.
//
//   rdmp cmd AddPatientIndexTable CohortIdentificationConfiguration:7 Catalogue:21 "Admissions" chi "admission_date" false

using System;
using System.Collections.Generic;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.CommandExecution.Combining;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;

namespace RdmpCohortScript;

public class ExecuteCommandAddPatientIndexTable : CohortScriptCommand
{
    private readonly CohortIdentificationConfiguration _cic;
    private readonly Catalogue _catalogue;
    private readonly ExtractionInformation _identifier;
    private readonly ExtractionInformation[] _extraColumns;
    private readonly bool _importMandatoryFilters;

    /// <summary>The patient index table's set, once the command has run.</summary>
    public AggregateConfiguration CreatedSet { get; private set; }

    /// <summary>The joinable that makes the set a patient index table, once the command has run.</summary>
    public JoinableCohortAggregateConfiguration CreatedJoinable { get; private set; }

    public ExecuteCommandAddPatientIndexTable(IBasicActivateItems activator,
        [DemandsInitialization("The cohort the patient index table belongs to")]
        CohortIdentificationConfiguration cic,
        [DemandsInitialization("The catalogue the patient index table reads, by id")]
        Catalogue catalogue,
        [DemandsInitialization("The catalogue's name, to check the id. Give null to skip the check.")]
        string catalogueName,
        [DemandsInitialization("The identifier column to use, by its name in the catalogue. Give null when the catalogue has only one.")]
        string identifierColumn,
        [DemandsInitialization("The other columns to select, by name, separated by commas (so a name cannot contain a comma). Give null for none.")]
        string extraColumns,
        [DemandsInitialization("true to copy the catalogue's current mandatory filters into the set, false to leave them out")]
        bool importMandatoryFilters) : base(activator)
    {
        _cic = cic;
        _catalogue = catalogue;
        _importMandatoryFilters = importMandatoryFilters;

        if (cic == null || catalogue == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the cohort and the catalogue for the new patient index table.");
            return;
        }

        if (RefuseIfReadOnly(cic, $"cohort {cic.ID} \"{cic.Name}\""))
            return;

        var nameProblem = ExistingObjectName.Mismatch("catalogue", catalogue, catalogue.Name, catalogueName);
        if (nameProblem != null)
        {
            Refuse(CohortScriptRefusal.NameMismatch, nameProblem);
            return;
        }

        if (RefuseCatalogueTheCohortCannotUse(catalogue, cic))
            return;

        _identifier = IdentifierColumn.Find(catalogue, identifierColumn, out var refusal, out var columnProblem);
        if (_identifier == null)
        {
            Refuse(refusal, columnProblem);
            return;
        }

        var columns = catalogue.GetAllExtractionInformation(ExtractionCategory.Any);
        var extras = new List<ExtractionInformation>();
        foreach (var name in (extraColumns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var column = columns.SingleOrDefault(e =>
                string.Equals(e.GetRuntimeName(), name, StringComparison.OrdinalIgnoreCase));
            if (column == null)
            {
                Refuse(CohortScriptRefusal.UnknownColumn,
                    $"Catalogue {catalogue.ID} \"{catalogue.Name}\" has no column called \"{name}\". Its columns are: {string.Join(", ", columns.Select(e => e.GetRuntimeName()))}.");
                return;
            }

            if (column.ID == _identifier.ID || extras.Any(e => e.ID == column.ID))
            {
                Refuse(CohortScriptRefusal.DuplicateColumn,
                    $"Column \"{name}\" is listed twice for the patient index table on catalogue {catalogue.ID} \"{catalogue.Name}\". The identifier column is always included.");
                return;
            }

            // RDMP allows one identifier column in a patient index table, and fails only after the table is saved
            if (column.IsExtractionIdentifier)
            {
                Refuse(CohortScriptRefusal.IdentifierAsExtraColumn,
                    $"Column \"{name}\" of catalogue {catalogue.ID} \"{catalogue.Name}\" is an identifier column, and a patient index table can have only one. Leave it out, or give it as the identifier column.");
                return;
            }

            extras.Add(column);
        }

        _extraColumns = extras.ToArray();
    }

    public override void Execute()
    {
        base.Execute();

        var combineable = new CatalogueCombineable(_catalogue)
        {
            ResolveMultipleExtractionIdentifiers = (_, _) => _identifier
        };
        var set = combineable.GenerateAggregateConfigurationFor(BasicActivator, _cic, _importMandatoryFilters);

        var repository = BasicActivator.RepositoryLocator.CatalogueRepository;
        foreach (var column in _extraColumns)
            _ = new AggregateDimension(repository, column, set.Aggregate);

        var before = _cic.GetAllJoinables().Select(j => j.ID).ToHashSet();
        new ExecuteCommandConvertAggregateConfigurationToPatientIndexTable(BasicActivator, set, _cic).Execute();

        CreatedSet = set.Aggregate;
        CreatedJoinable = _cic.GetAllJoinables().Single(j => !before.Contains(j.ID));
    }
}
