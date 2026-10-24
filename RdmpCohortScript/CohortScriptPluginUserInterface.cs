// Surfaces the cohort script commands in the RDMP desktop GUI: right-click a cohort identification
// configuration to export it as a build script or its catalogue manifest, to build a new cohort from a script, or
// to check a script. The entries sit in one submenu. The same command classes are found by the CLI
// (`rdmp cmd ExportCohortAsScript`, `rdmp cmd BuildCohortFromScript` and the others).

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
    internal const string ManifestLabel = "Export catalogue manifest...";
    internal const string CheckLabel = "Check a build script...";

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

        yield return new ExecuteCommandExportCatalogueManifest(BasicActivator, cic)
        {
            OverrideCommandName = ManifestLabel,
            SuggestedCategory = MenuCategory
        };

        // with no file and no name, the command asks for both
        yield return new ExecuteCommandBuildCohortFromScript(BasicActivator, null)
        {
            OverrideCommandName = BuildLabel,
            SuggestedCategory = MenuCategory
        };

        yield return new ExecuteCommandCheckCohortScript(BasicActivator, null)
        {
            OverrideCommandName = CheckLabel,
            SuggestedCategory = MenuCategory
        };
    }
}
