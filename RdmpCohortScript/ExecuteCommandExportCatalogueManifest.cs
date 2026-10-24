// Writes a catalogue manifest: the catalogues, tables and projects a build script may refer to, each with its id
// and name. For a cohort it writes the same manifest as the cohort's export. For chosen catalogues, or for every
// catalogue a cohort can use when none is chosen, it lets an author see what a new cohort could use, not only what
// earlier cohorts used.
//
//   rdmp cmd ExportCatalogueManifest CohortIdentificationConfiguration:42 null .\out\manifest.yaml
//   rdmp cmd ExportCatalogueManifest null Catalogue:717,718 .\out\manifest.yaml
//   rdmp cmd ExportCatalogueManifest null null .\out\all-catalogues.yaml

using System.IO;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;

namespace RdmpCohortScript;

public class ExecuteCommandExportCatalogueManifest : BasicCommandExecution
{
    private readonly CohortIdentificationConfiguration _cic;
    private readonly Catalogue[] _catalogues;
    private readonly FileInfo _toFile;

    public ExecuteCommandExportCatalogueManifest(IBasicActivateItems activator,
        [DemandsInitialization("The cohort whose catalogues, tables and projects to list. Give null to list catalogues instead.")]
        CohortIdentificationConfiguration cohort,
        [DemandsInitialization("The catalogues to list, when no cohort is given. Give null to list every catalogue.")]
        Catalogue[] catalogues = null,
        [DemandsInitialization("The file to write. In the GUI, leave it out to choose a folder for catalogue-manifest.yaml.")]
        FileInfo toFile = null) : base(activator)
    {
        _cic = cohort;
        _catalogues = catalogues;
        _toFile = toFile;

        if (cohort != null && catalogues is { Length: > 0 })
            SetImpossible("Give a cohort or catalogues, not both.");
    }

    public override void Execute()
    {
        base.Execute();

        // RDMP's file dialogs open existing files, so the GUI asks for a folder
        var file = _toFile;
        if (file == null)
        {
            var dir = BasicActivator.SelectDirectory("Choose the folder to write catalogue-manifest.yaml into");
            if (dir == null)
                return;
            file = new FileInfo(Path.Join(dir.FullName, "catalogue-manifest.yaml"));
        }

        if (BasicActivator.IsInteractive && file.Exists &&
            !BasicActivator.YesNo($"The file {file.FullName} already exists. Replace it?", "Replace manifest"))
            return;

        var writer = new CatalogueManifestWriter(BasicActivator.RepositoryLocator);
        string manifest;
        string what;
        if (_cic != null)
        {
            manifest = writer.ForCohort(_cic);
            what = $"cohort {_cic.ID} \"{_cic.Name}\"";
        }
        else
        {
            // a cohort cannot use an internal dataset, so the list of every catalogue leaves those out
            var catalogues = _catalogues is { Length: > 0 }
                ? _catalogues
                : BasicActivator.RepositoryLocator.CatalogueRepository.GetAllObjects<Catalogue>()
                    .Where(c => !c.IsInternalDataset).ToArray();
            manifest = writer.ForCatalogues(catalogues);
            what = _catalogues is { Length: > 0 }
                ? $"{catalogues.Length} catalogue(s)"
                : $"every catalogue a cohort can use ({catalogues.Length})";
        }

        file.Directory?.Create();
        File.WriteAllText(file.FullName, manifest);
        BasicActivator.Show($"Wrote the catalogue manifest for {what} to {file.FullName}");
    }
}
