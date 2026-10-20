// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.Repositories.Construction;

namespace RdmpLinkedServerCohortSql;

/// <summary>
/// Shows or saves the SQL of a cohort whose sets live on servers that the home server reaches as linked servers.
/// <para>With <c>staged = true</c> it writes the staged script, which copies each set into a temp table; this is the script to
/// run. By default it writes the script for inspection only, which is one statement.</para>
/// <para>Command line: <c>rdmp cmd ViewCohortLinkedServerSql CohortIdentificationConfiguration:&lt;id&gt; out.sql true</c> saves the
/// staged script to a file, <c>... &lt;id&gt; null true</c> prints it, and <c>... &lt;id&gt; out.sql</c> saves the script for
/// inspection.</para>
/// </summary>
public sealed class ExecuteCommandViewCohortLinkedServerSql : BasicCommandExecution
{
    internal const string Composite = "Query Builder SQL (Linked Servers, for inspection only)";
    internal const string Staged = "Query Builder SQL (Linked Servers, staged, to run)";
    internal const string SaveSuffix = " save to file...";

    private readonly CohortIdentificationConfiguration _cic;
    private readonly FileInfo _toFile;
    private readonly bool _askForFile;
    private readonly bool _staged;

    [UseWithObjectConstructor]
    public ExecuteCommandViewCohortLinkedServerSql(IBasicActivateItems activator,
        CohortIdentificationConfiguration cic, FileInfo toFile = null, bool staged = false) : base(activator)
    {
        _cic = cic;
        _toFile = toFile;
        _staged = staged;
        OverrideCommandName = staged ? Staged : Composite;
        if (cic == null) SetImpossible("This command needs a cohort identification configuration, and none was given.");
        else if (cic.RootCohortAggregateContainer_ID == null)
            SetImpossible("The cohort has no sets yet (it has no top-level UNION, INTERSECT or EXCEPT container). Add a set first.");
        // The arguments are positional, so "<id> true" puts true in the file argument and would save the script for inspection
        // to a file named true.
        if (toFile != null && (toFile.Name.Equals("true", StringComparison.OrdinalIgnoreCase) || toFile.Name.Equals("false", StringComparison.OrdinalIgnoreCase)))
            SetImpossible($"The file argument is '{toFile.Name}', so the script for inspection would be saved to a file named '{toFile.Name}'. " +
                          "To print the staged script, use '<id> null true'. To save it, use '<id> out.sql true'.");
    }

    public ExecuteCommandViewCohortLinkedServerSql(IBasicActivateItems activator,
        CohortIdentificationConfiguration cic, bool askForFile, bool staged) : this(activator, cic, null, staged)
    {
        _askForFile = askForFile;
        if (askForFile) OverrideCommandName = (staged ? Staged : Composite) + SaveSuffix;
    }

    public ExecuteCommandViewCohortLinkedServerSql(IBasicActivateItems activator,
        ProjectCohortIdentificationConfigurationAssociation association, bool askForFile = false, bool staged = false)
        : this(activator, association?.CohortIdentificationConfiguration, askForFile, staged)
    {
        if (association?.CohortIdentificationConfiguration == null)
            SetImpossible("This project points to a cohort that no longer exists. Someone may have deleted it.");
    }

    // RDMP 9.3.0 shows at most 20,000 characters in a message box (WideMessageBox.MAX_LENGTH_BODY). The value is copied here
    // so the plugin does not depend on the Windows-only user interface assembly.
    internal static bool NeedsFile(bool isInteractive, int length) => isInteractive && length > 20000;

    public override void Execute()
    {
        base.Execute();
        var result = new LinkedServerCohortSqlBuilder().Build(_cic, _staged);
        // The tip tells a console user how to save the script to a file, so it is left out when the script goes to a file.
        var text = result.ToScript(includeConsoleTip: !BasicActivator.IsInteractive && _toFile == null);
        if (_toFile != null)
        {
            File.WriteAllText(_toFile.FullName, text);
            return;
        }
        if (_askForFile) { Save(text); return; }
        if (NeedsFile(BasicActivator.IsInteractive, text.Length))
        {
            var saved = Save(text);
            if (saved != null)
                BasicActivator.Show("Linked Servers SQL saved",
                    $"The SQL is {text.Length} characters, too long for this window. Saved to {saved.FullName}.");
            else
                BasicActivator.Show("Linked Servers SQL",
                    $"The SQL is {text.Length} characters, too long for this window, and no file was saved. " +
                    $"Use \"{(_staged ? Staged : Composite) + SaveSuffix}\" to save it. The terminal interface can only pick a file that " +
                    "already exists, so create an empty .sql file first and pick it, or give a file path on the rdmp command line.");
            return;
        }
        // The title starts with "--" because RDMP copies it above the script, where it must be an SQL comment.
        BasicActivator.Show($"-- {LinkedServerCohortSqlResult.CommentText(_cic.Name)} (Linked Servers, {(_staged ? "staged, to run" : "for inspection only")}) SQL", text);
    }

    private FileInfo Save(string text)
    {
        var file = BasicActivator.SelectFile("Save linked-server SQL", "SQL files", "*.sql");
        if (file == null) return null;
        if (file.Exists)
        {
            BasicActivator.YesNo(new DialogArgs
            {
                WindowTitle = "Overwrite file?",
                TaskDescription = $"{file.FullName} already exists. Overwrite it?"
            }, out var chosen);
            if (!chosen) return null;
        }
        File.WriteAllText(file.FullName, text);
        return file;
    }
}
