// Base of the plugin's script commands. A command that refuses to run says why twice: in a message for people,
// and in Refusal, a reason code that code and tests use, so nothing has to parse a message.

using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;

namespace RdmpCohortScript;

/// <summary>A plugin command that a build script can use, with a reason code when it refuses to run.</summary>
public abstract class CohortScriptCommand : BasicCommandExecution
{
    /// <summary>Why the command refuses to run, or null when it can run.</summary>
    public CohortScriptRefusal? Refusal { get; private set; }

    protected CohortScriptCommand(IBasicActivateItems activator) : base(activator)
    {
    }

    protected void Refuse(CohortScriptRefusal reason, string message)
    {
        Refusal ??= reason;
        SetImpossible(message);
    }

    /// <summary>
    /// Refuses when RDMP says the object must not change, for example because its cohort is frozen. Returns true
    /// when it refused.
    /// </summary>
    protected bool RefuseIfReadOnly(IMightBeReadOnly target, string description)
    {
        if (target == null || !target.ShouldBeReadOnly(GetType().Name, out var why))
            return false;

        Refuse(CohortScriptRefusal.ReadOnly, $"Cannot change {description}: {why}");
        return true;
    }

    /// <summary>
    /// Refuses a catalogue that a cohort cannot use: an internal dataset, or a project-specific catalogue when
    /// the cohort is not linked to that catalogue's project. Returns true when it refused.
    /// </summary>
    /// <remarks>
    /// RDMP's own add-catalogue command asks the catalogue for its project through the repository the catalogue
    /// was loaded from. A catalogue loaded from a database catalogue repository has no data export repository, so
    /// RDMP's check passes silently. This check asks the platform's data export repository instead.
    /// </remarks>
    protected bool RefuseCatalogueTheCohortCannotUse(Catalogue catalogue, CohortIdentificationConfiguration cic)
    {
        if (catalogue.IsInternalDataset)
        {
            Refuse(CohortScriptRefusal.InternalDataset,
                $"Catalogue {catalogue.ID} \"{catalogue.Name}\" is an internal dataset, which a cohort cannot use.");
            return true;
        }

        var dataExport = BasicActivator.RepositoryLocator.DataExportRepository;
        if (dataExport == null || cic == null)
            return false;

        var catalogueProjects = dataExport.GetAllObjectsWhere<ExtractableDataSet>("Catalogue_ID", catalogue.ID)
            .SelectMany(eds => eds.Projects).ToArray();
        if (catalogueProjects.Length == 0)
            return false;

        var cohortProjects = dataExport
            .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", cic.ID)
            .Select(a => a.Project_ID).ToHashSet();
        if (catalogueProjects.Any(p => cohortProjects.Contains(p.ID)))
            return false;

        var projects = string.Join(", ", catalogueProjects.Select(p => $"{p.ID} \"{p.Name}\""));
        Refuse(CohortScriptRefusal.ProjectNotLinked,
            $"Catalogue {catalogue.ID} \"{catalogue.Name}\" is specific to project {projects}, but cohort {cic.ID} " +
            $"\"{cic.Name}\" is not linked to it. Link the cohort to the project first (LinkCohortToProject).");
        return true;
    }
}

/// <summary>Why a plugin script command refused to run.</summary>
public enum CohortScriptRefusal
{
    /// <summary>A required object was not given.</summary>
    MissingArgument,

    /// <summary>The name given beside an id differs from the object's current name.</summary>
    NameMismatch,

    /// <summary>RDMP says the object must not change, for example because its cohort is frozen.</summary>
    ReadOnly,

    /// <summary>The catalogue is an internal dataset.</summary>
    InternalDataset,

    /// <summary>The catalogue is project-specific and the cohort is not linked to its project.</summary>
    ProjectNotLinked,

    /// <summary>RDMP's own command refused, for a reason the message gives.</summary>
    RdmpRefused,

    /// <summary>The catalogue is served by an API, so it has no identifier column a script can name.</summary>
    ApiCatalogue,

    /// <summary>The catalogue has no identifier column.</summary>
    NoIdentifierColumn,

    /// <summary>The catalogue has several identifier columns and none was named.</summary>
    AmbiguousIdentifierColumn,

    /// <summary>The catalogue has no identifier column of the given name.</summary>
    UnknownIdentifierColumn,

    /// <summary>The catalogue has no column of the given name.</summary>
    UnknownColumn,

    /// <summary>A column is listed twice, or is the identifier column.</summary>
    DuplicateColumn,

    /// <summary>An extra column of a patient index table is itself an identifier column.</summary>
    IdentifierAsExtraColumn,

    /// <summary>The set belongs to no cohort.</summary>
    NotInACohort,

    /// <summary>The set and the patient index table belong to different cohorts.</summary>
    DifferentCohorts,

    /// <summary>The set is the patient index table it would join.</summary>
    JoinsItself,

    /// <summary>The set already joins a patient index table.</summary>
    AlreadyJoinsATable,

    /// <summary>The set already has a forced join to the table.</summary>
    AlreadyForcedJoin,

    /// <summary>The cohort is already linked to the project.</summary>
    AlreadyLinked,

    /// <summary>A parameter can go only on a cohort, a set or a filter.</summary>
    NotAParameterOwner,

    /// <summary>The parameter name does not start with @.</summary>
    BadParameterName,

    /// <summary>The declaration does not declare the parameter name given.</summary>
    DeclarationMismatch,

    /// <summary>RDMP reserves the parameter name.</summary>
    ReservedParameterName,

    /// <summary>The owner already has a parameter of that name.</summary>
    DuplicateParameter,

    /// <summary>No new name was given.</summary>
    EmptyName,

    /// <summary>The new name already carries RDMP's cic_ prefix.</summary>
    NameHasPrefix,

    /// <summary>Another set of the cohort already has the name.</summary>
    NameTaken,

    /// <summary>The number of rows to keep is less than 1.</summary>
    BadRowCount,

    /// <summary>The order-by column belongs to another set.</summary>
    OrderByOtherSet,

    /// <summary>Export: the cohort has no root container.</summary>
    NoRootContainer,

    /// <summary>Export: a set that is not a patient index table has a count column.</summary>
    CountColumnOnSet,

    /// <summary>Export: a set selects columns other than its identifier column.</summary>
    ExtraColumnsOnSet,

    /// <summary>Export: a patient index table column name contains a comma.</summary>
    CommaInColumnName,

    /// <summary>Export: a column name cannot be part of a handle, because it contains a space, a quote or a dot.</summary>
    ColumnNameNotUsableInHandle,

    /// <summary>Export: a set has a pivot.</summary>
    PivotOnSet,

    /// <summary>Export: a set has a date axis.</summary>
    DateAxisOnSet,

    /// <summary>Export: a set's column is no longer in its catalogue.</summary>
    DimensionWithoutColumn,

    /// <summary>Export: SQL already contains the text {$, which the script uses for markers.</summary>
    MarkerTextInSql,

    /// <summary>Export: a set has more than one identifier column.</summary>
    TooManyIdentifierColumns,

    /// <summary>Export: a patient index table uses one catalogue column for two of its outputs.</summary>
    RepeatedSourceColumn,

    /// <summary>Export: a set's name still starts with cic_ after RDMP's prefix is removed.</summary>
    SetNameLooksPrefixed,

    /// <summary>A patient index table cannot itself join a patient index table.</summary>
    JoinFromAPatientIndexTable
}
