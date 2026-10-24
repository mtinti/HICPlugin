// Writes the reference of every command a build script may use: its arguments, the handles it binds, and one
// example, with the rules for handles, names and the order of lines. It is made from the same table the check and
// the rebuild use, so a person or an agent reads what the plugin actually accepts.
//
//   rdmp cmd DescribeCohortScriptCommands .\out\build-script-commands.md

using System.IO;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;

namespace RdmpCohortScript;

public class ExecuteCommandDescribeCohortScriptCommands : BasicCommandExecution
{
    private readonly FileInfo _toFile;

    /// <summary>The reference text, once the command has run.</summary>
    public string Reference { get; private set; }

    public ExecuteCommandDescribeCohortScriptCommands(IBasicActivateItems activator,
        [DemandsInitialization("The Markdown file to write the reference to. Leave it out to show the reference instead.")]
        FileInfo toFile = null) : base(activator)
    {
        _toFile = toFile;
    }

    public override void Execute()
    {
        base.Execute();

        Reference = CohortScriptReference.Markdown(BasicActivator);
        if (_toFile == null)
        {
            BasicActivator.Show(Reference);
            return;
        }

        _toFile.Directory?.Create();
        File.WriteAllText(_toFile.FullName, Reference);
        BasicActivator.Show($"Wrote the build script command reference to {_toFile.FullName}");
    }
}
