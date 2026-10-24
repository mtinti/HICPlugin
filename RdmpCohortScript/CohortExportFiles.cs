// The three files of one cohort's export, made in memory first so that a cohort the format cannot carry leaves no
// files, and then written to a folder. The single export and the export of many cohorts both use it, so their files
// are the same.

using System.Collections.Generic;
using System.IO;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Repositories;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

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
    /// Says why writing into the folder would replace another cohort's export, or gives null when the folder holds no
    /// build script or this cohort's. A folder is known by the cohort id in its script's header, not by its name,
    /// because two cohorts can have one name, or names that differ only by a character the file system cannot hold.
    /// </summary>
    public string OtherExportIn(DirectoryInfo dir)
    {
        var path = Path.Join(dir.FullName, ScriptFile);
        if (!File.Exists(path))
            return null;

        var (id, name) = ExportedFrom(File.ReadAllText(path));
        if (id == Cohort.ID)
            return null;

        return id == null
            ? $"The folder {dir.FullName} holds a build script that does not say which cohort it was exported from, so it may be another cohort's or written by hand. Export cohort {Cohort.ID} \"{Cohort.Name}\" into another folder, or move that script first."
            : $"The folder {dir.FullName} holds the export of cohort {id} \"{name}\", not of cohort {Cohort.ID} \"{Cohort.Name}\". Export this cohort into another folder, or move that export first.";
    }

    // the cohort id and name in a build script's exported-from block, or nulls when the script has none
    private static (int? Id, string Name) ExportedFrom(string text)
    {
        try
        {
            var document = new Deserializer().Deserialize<Dictionary<object, object>>(text);
            if (document?.GetValueOrDefault("exported-from") is Dictionary<object, object> from &&
                from.GetValueOrDefault("cohort-id") is string idText && int.TryParse(idText, out var id))
                return (id, from.GetValueOrDefault("cohort-name") as string);
        }
        catch (YamlException)
        {
        }

        return (null, null);
    }

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
