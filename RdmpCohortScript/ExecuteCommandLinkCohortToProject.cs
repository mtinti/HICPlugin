// Links a cohort to a project. A cohort must be linked to a project before it can use that project's
// project-specific catalogues. RDMP's own command asks which project to use, so a script cannot run it.
// Unlike RDMP's command, this one refuses a frozen cohort, so a script never changes a frozen cohort.
//
//   rdmp cmd LinkCohortToProject CohortIdentificationConfiguration:7 Project:82 "Diabetes study"

using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;

namespace RdmpCohortScript;

public class ExecuteCommandLinkCohortToProject : CohortScriptCommand
{
    private readonly CohortIdentificationConfiguration _cic;
    private readonly Project _project;

    public ExecuteCommandLinkCohortToProject(IBasicActivateItems activator,
        [DemandsInitialization("The cohort to link")]
        CohortIdentificationConfiguration cic,
        [DemandsInitialization("The project, by id")]
        Project project,
        [DemandsInitialization("The project's name, to check the id. Give null to skip the check.")]
        string projectName) : base(activator)
    {
        _cic = cic;
        _project = project;

        if (cic == null || project == null)
        {
            Refuse(CohortScriptRefusal.MissingArgument, "Give the cohort and the project to link it to.");
            return;
        }

        if (RefuseIfReadOnly(cic, $"cohort {cic.ID} \"{cic.Name}\""))
            return;

        var nameProblem = ExistingObjectName.Mismatch("project", project, project.Name, projectName);
        if (nameProblem != null)
        {
            Refuse(CohortScriptRefusal.NameMismatch, nameProblem);
            return;
        }

        if (activator.RepositoryLocator.DataExportRepository
            .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", cic.ID)
            .Any(a => a.Project_ID == project.ID))
            Refuse(CohortScriptRefusal.AlreadyLinked,
                $"Cohort {cic.ID} \"{cic.Name}\" is already linked to project {project.ID} \"{project.Name}\".");
    }

    public override void Execute()
    {
        base.Execute();
        _ = new ProjectCohortIdentificationConfigurationAssociation(
            BasicActivator.RepositoryLocator.DataExportRepository, _project, _cic);
    }
}
