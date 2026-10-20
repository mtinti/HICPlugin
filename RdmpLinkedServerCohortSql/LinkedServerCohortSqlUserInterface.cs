// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using Rdmp.Core;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;

namespace RdmpLinkedServerCohortSql;

public sealed class LinkedServerCohortSqlUserInterface(IBasicActivateItems activator) : PluginUserInterface(activator)
{
    /// <summary>All four entries sit in one submenu of this name (RDMP groups commands by SuggestedCategory).</summary>
    internal const string Submenu = "Linked Servers SQL";

    public override IEnumerable<IAtomicCommand> GetAdditionalRightClickMenuItems(object model)
    {
        foreach (var command in Commands(model))
        {
            command.SuggestedCategory = Submenu;
            yield return command;
        }
    }

    private IEnumerable<ExecuteCommandViewCohortLinkedServerSql> Commands(object model)
    {
        if (model is CohortIdentificationConfiguration cic)
        {
            // The staged script comes first, because it is the one to run.
            yield return new ExecuteCommandViewCohortLinkedServerSql(BasicActivator, cic, askForFile: false, staged: true);
            yield return new ExecuteCommandViewCohortLinkedServerSql(BasicActivator, cic, askForFile: true, staged: true);
            yield return new ExecuteCommandViewCohortLinkedServerSql(BasicActivator, cic, askForFile: false, staged: false);
            yield return new ExecuteCommandViewCohortLinkedServerSql(BasicActivator, cic, askForFile: true, staged: false);
        }
        else if (model is ProjectCohortIdentificationConfigurationAssociation association)
        {
            yield return new ExecuteCommandViewCohortLinkedServerSql(BasicActivator, association, askForFile: false, staged: true);
            yield return new ExecuteCommandViewCohortLinkedServerSql(BasicActivator, association, askForFile: true, staged: true);
            yield return new ExecuteCommandViewCohortLinkedServerSql(BasicActivator, association, askForFile: false, staged: false);
            yield return new ExecuteCommandViewCohortLinkedServerSql(BasicActivator, association, askForFile: true, staged: false);
        }
    }
}
