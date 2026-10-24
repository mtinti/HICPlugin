// Builds a new cohort from a format 1 build script (COHORT-SCRIPT-FORMAT.md). It reads and checks the whole script
// first and builds nothing when anything is wrong, listing every problem with its line. Then it runs the lines one
// by one through RDMP's command runner, binds each handle to what its line made, and checks each line's effect. If a
// line fails it stops there and names the line.
//
// The lines do not refresh RDMP's list of objects; the build refreshes it once, at the end. Then it selects the new
// cohort in the tree, without opening it. After a failure it refreshes only when the clean-up left something behind
// or could not check, so the trees show what is really there.
//
//   rdmp cmd BuildCohortFromScript .\out\MyCohort\build.script.yaml "MyCohort (rebuilt)"

using System;
using System.Collections.Generic;
using System.IO;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;

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
        catch (CohortScriptRebuildException e) when (e.Cleanup is { Incomplete: true, Cohort: { } removed })
        {
            // the trees never saw the build's objects, so a complete clean-up needs no refresh; this one may have left
            // some. A deleted cohort is still a safe object to refresh with: RDMP then loads its whole list.
            try
            {
                Publish(removed);
            }
            catch (Exception refresh)
            {
                throw e.WithSentence($"RDMP could not refresh its list of objects either: {refresh.Message}");
            }

            throw;
        }
        finally
        {
            BasicActivator.InteractiveDeletes = interactiveDeletes;
        }

        var cohort = runner.Cohort;
        var warnings = checker.Warnings.Count == 0
            ? ""
            : $"{Environment.NewLine}{string.Join(Environment.NewLine, checker.Warnings)}";
        try
        {
            Publish(cohort);
        }
        catch (Exception e)
        {
            BasicActivator.ShowWarning($"Built cohort {cohort.ID} \"{cohort.Name}\" from {file.Name}. The cohort is saved, but RDMP could not refresh its list of objects, so refresh RDMP to see it. The reason: {e.Message}{warnings}");
            return;
        }

        BasicActivator.Show($"Built cohort {cohort.ID} \"{cohort.Name}\" from {file.Name}.{warnings}");
        ShowInTheTree(cohort);
    }

    // selects the cohort in its tree; in the Windows client this also shows the Cohort Builder panel if it is closed
    private void ShowInTheTree(CohortIdentificationConfiguration cohort)
    {
        try
        {
            Emphasise(cohort);
        }
        catch (Exception e)
        {
            BasicActivator.ShowWarning($"Cohort {cohort.ID} \"{cohort.Name}\" is built and saved, but RDMP could not select it in the tree: {e.Message}");
        }
    }
}
