// Checks a build script and builds nothing. It runs every check the rebuild runs first: the file's form, each
// line's command and arguments, handles and their order, parameters, and every existing catalogue, table, project
// and published filter on this RDMP. It lists every problem with its file line. For a script that a person or an
// agent wrote, rather than one exported from a cohort, the second argument adds a warning for each catalogue whose
// mandatory filters a line leaves out. The third argument accepts renamed objects, as the rebuild's does, so the
// check passes exactly when the rebuild with the same option would start building.
//
//   rdmp cmd CheckCohortScript .\out\MyCohort\build.script.yaml
//   rdmp cmd CheckCohortScript .\drafts\new-cohort.build.script.yaml true
//   rdmp cmd CheckCohortScript .\out\MyCohort\build.script.yaml false true

using System;
using System.Collections.Generic;
using System.IO;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;

namespace RdmpCohortScript;

public class ExecuteCommandCheckCohortScript : BasicCommandExecution
{
    private readonly FileInfo _scriptFile;
    private readonly bool _authored;
    private readonly bool _acceptRenamed;

    /// <summary>The warnings of the last check, once the command has run.</summary>
    public IReadOnlyList<CohortScriptProblem> Warnings { get; private set; } = Array.Empty<CohortScriptProblem>();

    public ExecuteCommandCheckCohortScript(IBasicActivateItems activator,
        [DemandsInitialization("The build.script.yaml to check. In the GUI, leave it out to choose the file.")]
        FileInfo scriptFile,
        [DemandsInitialization("true when a person or an agent wrote the script rather than exporting it: the check then warns about mandatory filters a line leaves out. Leave out, or false, for an exported script.")]
        bool authored = false,
        [DemandsInitialization("true to accept a catalogue, table, project or published filter renamed since the export, with a warning for each, as BuildCohortFromScript does with the same option. Leave out, or false, to report a renamed object as a problem.")]
        bool acceptRenamedObjects = false) : base(activator)
    {
        _scriptFile = scriptFile;
        _authored = authored;
        _acceptRenamed = acceptRenamedObjects;
        if (scriptFile != null && !scriptFile.Exists)
            SetImpossible($"There is no build script at {scriptFile.FullName}.");
    }

    public override void Execute()
    {
        base.Execute();

        var file = _scriptFile ?? BasicActivator.SelectFile("Choose the build.script.yaml to check", "Cohort build script", "*.yaml");
        if (file == null)
            return;

        var problems = new List<CohortScriptProblem>();
        var lines = CohortScriptReader.Read(File.ReadAllText(file.FullName), problems);
        var checker = new CohortScriptChecker(BasicActivator.RepositoryLocator, problems, _acceptRenamed, _authored);
        checker.Check(lines, null);
        Warnings = checker.Warnings;

        // problems end the command with an error, so a script that runs the check can stop on them
        if (problems.Count > 0)
            throw new CohortScriptProblemsException(problems,
                $"The build script {file.FullName} has {problems.Count} problem(s). A rebuild would refuse it.", checker.Warnings);

        var warnings = checker.Warnings.Count == 0
            ? ""
            : $" It has {checker.Warnings.Count} warning(s):{Environment.NewLine}{string.Join(Environment.NewLine, checker.Warnings)}";
        BasicActivator.Show($"The build script {file.Name} passed every check.{warnings}");
    }
}
