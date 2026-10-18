// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.Repositories.Construction;

namespace Rdmp.LinkedServerCohortSql;

/// <summary>
/// Shows or saves the cohort's SQL for cohorts whose sets live on servers reachable as linked servers.
/// <para>Staged (<c>staged = true</c>): one temp table per set, the shape to run. Composite (default): one statement, for reading.</para>
/// <para>CLI: <c>rdmp ViewCohortLinkedServerSql CohortIdentificationConfiguration:&lt;id&gt; out.sql true</c> writes the staged
/// script to a file; <c>... &lt;id&gt; null true</c> writes it to stdout; <c>... &lt;id&gt; [out.sql]</c> writes the composite.</para>
/// </summary>
public sealed class ExecuteCommandViewCohortLinkedServerSql : BasicCommandExecution
{
    internal const string Composite = "Query Builder SQL (Linked Servers)";
    internal const string Staged = "Query Builder SQL (Linked Servers, staged)";
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
        if (cic == null) SetImpossible("No cohort identification configuration");
        else if (cic.RootCohortAggregateContainer_ID == null) SetImpossible("Cohort has no root container");
        // "<id> true" binds true to the file argument and would write the composite to a file named true (plan 0.7.5).
        if (toFile != null && (toFile.Name.Equals("true", StringComparison.OrdinalIgnoreCase) || toFile.Name.Equals("false", StringComparison.OrdinalIgnoreCase)))
            SetImpossible($"The file argument is '{toFile.Name}'; did you mean '<id> null {toFile.Name.ToLowerInvariant()}' (staged to the console) or '<id> out.sql {toFile.Name.ToLowerInvariant()}'?");
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
            SetImpossible("Association has no cohort identification configuration");
    }

    // WideMessageBox.MAX_LENGTH_BODY at RDMP 9.3.0. Avoid a dependency on the Windows-only UI assembly.
    internal static bool NeedsFile(bool isInteractive, int length) => isInteractive && length > 20000;

    public override void Execute()
    {
        base.Execute();
        var result = new LinkedServerCohortSqlBuilder().Build(_cic, _staged);
        // The tip tells a console user to pass a file path; pointless inside the file they asked for.
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
                    $"The SQL is {text.Length} characters, too long for this window. " +
                    $"Use '{(_staged ? Staged : Composite) + SaveSuffix}'. " +
                    "In the terminal GUI, create an empty .sql first and pick it, or use the CLI file argument.");
            return;
        }
        BasicActivator.Show($"-- {LinkedServerCohortSqlResult.CommentText(_cic.Name)} (Linked Servers{(_staged ? ", staged" : "")}) SQL", text);
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
