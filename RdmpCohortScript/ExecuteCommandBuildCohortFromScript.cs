// Builds a new cohort from a format 1 build script (COHORT-SCRIPT-FORMAT.md). It reads and checks the whole script
// first and builds nothing when anything is wrong, listing every problem with its line. Then it runs the lines one
// by one through RDMP's command runner, binds each handle to what its line made, and checks each line's effect. If a
// line fails it stops there and names the line.
//
//   rdmp cmd BuildCohortFromScript .\out\MyCohort\build.script.yaml "MyCohort (rebuilt)"

using System.Collections.Generic;
using System.IO;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;

namespace RdmpCohortScript;

public class ExecuteCommandBuildCohortFromScript : BasicCommandExecution
{
    private readonly FileInfo _scriptFile;
    private readonly string _newName;

    public ExecuteCommandBuildCohortFromScript(IBasicActivateItems activator,
        [DemandsInitialization("The build.script.yaml to build the cohort from. In the GUI, leave it out to choose the file.")]
        FileInfo scriptFile,
        [DemandsInitialization("Name for the new cohort, which must be unique. In the GUI, leave it out to type it.")]
        string newCohortName = null) : base(activator)
    {
        _scriptFile = scriptFile;
        _newName = newCohortName;
        if (scriptFile != null && !scriptFile.Exists)
            SetImpossible($"There is no build script at {scriptFile.FullName}.");
    }

    public override void Execute()
    {
        base.Execute();

        var file = _scriptFile ?? BasicActivator.SelectFile("Choose the build.script.yaml to build the cohort from", "Cohort build script", "*.yaml");
        if (file == null)
            return;

        var newName = _newName;
        if (string.IsNullOrWhiteSpace(newName) &&
            (!BasicActivator.TypeText("Build cohort from script", "Name for the new cohort", 200, null, out newName, false)
             || string.IsNullOrWhiteSpace(newName)))
            return;

        var problems = new List<CohortScriptProblem>();
        var lines = CohortScriptReader.Read(File.ReadAllText(file.FullName), problems);
        new CohortScriptChecker(BasicActivator.RepositoryLocator, problems).Check(lines, newName);
        if (problems.Count > 0)
            throw new CohortScriptProblemsException(file.FullName, problems);

        var runner = new CohortScriptRunner(BasicActivator, newName);
        runner.Run(lines.Parsed());

        BasicActivator.Show($"Built cohort {runner.Cohort.ID} \"{runner.Cohort.Name}\" from {file.Name}.");
        Publish(runner.Cohort);
    }
}
