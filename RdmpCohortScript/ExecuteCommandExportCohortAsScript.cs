// Exports a cohort identification configuration as three text files that hold no patient data:
//   <out>/<cohort>/requirement.md   <out>/<cohort>/build.script.yaml   <out>/<cohort>/catalogue-manifest.yaml
//
// build.script.yaml is a format 1 build script (COHORT-SCRIPT-FORMAT.md) that ExecuteCommandBuildCohortFromScript
// runs to build an identical cohort. catalogue-manifest.yaml lists the catalogues, tables and projects the script
// refers to. requirement.md is a placeholder for a requirement written by hand, and is never replaced.

using System;
using System.IO;
using System.Text;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;

namespace RdmpCohortScript;

public class ExecuteCommandExportCohortAsScript : BasicCommandExecution
{
    private readonly CohortIdentificationConfiguration _cic;
    private readonly DirectoryInfo _outDir;

    public ExecuteCommandExportCohortAsScript(IBasicActivateItems activator,
        [DemandsInitialization("The cohort to export")]
        CohortIdentificationConfiguration cic,
        [DemandsInitialization(
            "Folder to write the export into. If not given, the GUI asks for one and the command line uses the current directory.")]
        DirectoryInfo toDir = null) : base(activator)
    {
        _cic = cic;
        _outDir = toDir;

        if (_cic == null)
            SetImpossible("Select a cohort to export.");
    }

    public override void Execute()
    {
        base.Execute();

        var outDir = _outDir;
        if (outDir == null)
        {
            if (BasicActivator.IsInteractive)
            {
                outDir = BasicActivator.SelectDirectory("Choose the folder to export the build script into");
                if (outDir == null)
                    return;
            }
            else
            {
                outDir = new DirectoryInfo(Environment.CurrentDirectory);
            }
        }

        // both files are made before anything is written, so a cohort the format cannot carry leaves no files
        var script = new BuildScriptWriter(_cic, BasicActivator.RepositoryLocator).Document();
        var manifest = new CatalogueManifestWriter(BasicActivator.RepositoryLocator).ForCohort(_cic);

        var dir = new DirectoryInfo(Path.Join(outDir.FullName, Sanitise(_cic.Name)));
        var scriptPath = Path.Join(dir.FullName, "build.script.yaml");
        var manifestPath = Path.Join(dir.FullName, "catalogue-manifest.yaml");

        // the command line replaces an earlier export without asking, so scripts can re-export
        if (BasicActivator.IsInteractive && (File.Exists(scriptPath) || File.Exists(manifestPath)) &&
            !BasicActivator.YesNo(
                $"The folder {dir.FullName} already holds an export of this cohort. Replace its build script and catalogue manifest? The requirement file is kept.",
                "Replace export"))
            return;

        dir.Create();

        // the requirement is written by hand from the request; the cohort's description is often unrelated to it
        var requirementPath = Path.Join(dir.FullName, "requirement.md");
        if (!File.Exists(requirementPath))
            File.WriteAllText(requirementPath, $"<!-- Write the requirement for '{_cic.Name}' here. -->\n");

        File.WriteAllText(scriptPath, script);
        File.WriteAllText(manifestPath, manifest);

        BasicActivator.Show($"Exported cohort {_cic.ID} \"{_cic.Name}\" to {dir.FullName}");
    }

    private static string Sanitise(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
