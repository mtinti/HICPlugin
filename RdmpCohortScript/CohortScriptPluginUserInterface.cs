// Surfaces the cohort script commands in the RDMP desktop GUI: right-click a cohort identification
// configuration to export it as a build script, or to build a new cohort from a script. Both entries sit in
// one submenu. The same command classes are found by the CLI (`rdmp cmd ExportCohortAsScript`,
// `rdmp cmd BuildCohortFromScript`).

using System.Collections.Generic;
using Rdmp.Core;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.Curation.Data.Cohort;

namespace RdmpCohortScript;

public class CohortScriptPluginUserInterface : PluginUserInterface
{
    internal const string MenuCategory = "Cohort script";
    internal const string ExportLabel = "Export as build script...";
    internal const string BuildLabel = "Build new cohort from script...";

    public CohortScriptPluginUserInterface(IBasicActivateItems itemActivator) : base(itemActivator)
    {
    }

    public override IEnumerable<IAtomicCommand> GetAdditionalRightClickMenuItems(object o)
    {
        if (o is not CohortIdentificationConfiguration cic)
            yield break;

        yield return new ExecuteCommandExportCohortAsScript(BasicActivator, cic)
        {
            OverrideCommandName = ExportLabel,
            SuggestedCategory = MenuCategory
        };

        // with no file and no name, the command asks for both
        yield return new ExecuteCommandBuildCohortFromScript(BasicActivator, null)
        {
            OverrideCommandName = BuildLabel,
            SuggestedCategory = MenuCategory
        };
    }
}
