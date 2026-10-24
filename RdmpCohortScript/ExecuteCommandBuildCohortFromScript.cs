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
    private readonly bool _acceptRenamed;

    public ExecuteCommandBuildCohortFromScript(IBasicActivateItems activator,
        [DemandsInitialization("The build.script.yaml to build the cohort from. In the GUI, leave it out to choose the file.")]
        FileInfo scriptFile,
        [DemandsInitialization("Name for the new cohort, which must be unique. In the GUI, leave it out to type it.")]
        string newCohortName = null,
        [DemandsInitialization("true to build even when a catalogue, table, project or published filter has been renamed since the export (a warning names each one). Leave out, or false, to refuse.")]
        bool acceptRenamedObjects = false) : base(activator)
    {
        _scriptFile = scriptFile;
        _newName = newCohortName;
        _acceptRenamed = acceptRenamedObjects;
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
        var checker = new CohortScriptChecker(BasicActivator.RepositoryLocator, problems, _acceptRenamed);
        checker.Check(lines, newName);
        if (problems.Count > 0)
            throw new CohortScriptProblemsException(file.FullName, problems);

        // the script deletes RDMP's automatic containers it does not use; in the GUI RDMP would ask about each one.
        // The setting is restored whatever happens.
        var interactiveDeletes = BasicActivator.InteractiveDeletes;
        BasicActivator.InteractiveDeletes = false;
        CohortScriptRunner runner;
        try
        {
            runner = new CohortScriptRunner(BasicActivator, newName, _acceptRenamed);
            runner.Run(lines.Parsed());
        }
        finally
        {
            BasicActivator.InteractiveDeletes = interactiveDeletes;
        }

        var warnings = checker.Warnings.Count == 0
            ? ""
            : $"{System.Environment.NewLine}{string.Join(System.Environment.NewLine, checker.Warnings)}";
        BasicActivator.Show($"Built cohort {runner.Cohort.ID} \"{runner.Cohort.Name}\" from {file.Name}.{warnings}");
        Publish(runner.Cohort);
    }
}
