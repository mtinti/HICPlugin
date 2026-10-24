// The three files of one cohort's export, made in memory first so that a cohort the format cannot carry leaves no
// files, and then written to a folder. The single export and the export of many cohorts both use it, so their files
// are the same.

using System.IO;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Repositories;

namespace RdmpCohortScript;

/// <summary>One cohort's build script and catalogue manifest, ready to write with its requirement placeholder.</summary>
internal sealed class CohortExportFiles
{
    public const string ScriptFile = "build.script.yaml";
    public const string ManifestFile = "catalogue-manifest.yaml";
    public const string RequirementFile = "requirement.md";

    public CohortIdentificationConfiguration Cohort { get; }
    public string Script { get; }
    public string Manifest { get; }

    /// <summary>Makes both files. Throws <see cref="CohortScriptExportException"/> when the cohort cannot be exported.</summary>
    public CohortExportFiles(CohortIdentificationConfiguration cic, IRDMPPlatformRepositoryServiceLocator locator)
    {
        Cohort = cic;
        Script = new BuildScriptWriter(cic, locator).Document();
        Manifest = new CatalogueManifestWriter(locator).ForCohort(cic);
    }

    /// <summary>True when the folder already holds a build script or a manifest that writing would replace.</summary>
    public static bool WouldReplace(DirectoryInfo dir) =>
        File.Exists(Path.Join(dir.FullName, ScriptFile)) || File.Exists(Path.Join(dir.FullName, ManifestFile));

    /// <summary>
    /// Writes the build script and the manifest into the folder, replacing earlier ones, and a requirement placeholder
    /// when the folder has no requirement yet. A requirement is written by hand from the request, because the
    /// cohort's description is often unrelated to it, so it is never replaced.
    /// </summary>
    public void WriteTo(DirectoryInfo dir)
    {
        dir.Create();

        var requirementPath = Path.Join(dir.FullName, RequirementFile);
        if (!File.Exists(requirementPath))
            File.WriteAllText(requirementPath, $"<!-- Write the requirement for '{Cohort.Name}' here. -->\n");

        File.WriteAllText(Path.Join(dir.FullName, ScriptFile), Script);
        File.WriteAllText(Path.Join(dir.FullName, ManifestFile), Manifest);
    }
}
