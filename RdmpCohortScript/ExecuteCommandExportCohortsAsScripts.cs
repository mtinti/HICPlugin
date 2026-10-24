// Exports many cohorts at once, each into its own folder, from a list file. Each line of the list gives a cohort id
// and the folder for its three files (requirement.md, build.script.yaml, catalogue-manifest.yaml):
//
//   # cohort id, then its folder; a relative folder is read from the list file's folder
//   42  diabetes-since-2010
//   57  exports/heart failure
//
// The folder is the one listed, whatever the cohort is called now, so a cohort renamed since an earlier export
// still goes beside its requirement. The command checks the whole list and makes every file in memory before it
// writes anything: an unknown cohort, a cohort listed twice, two cohorts listed for one folder, a folder that holds
// another cohort's export, or a cohort the format cannot carry stops the run with every problem listed and no file
// written.
//
//   rdmp cmd ExportCohortsAsScripts .\cohorts-to-export.txt

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;

namespace RdmpCohortScript;

public class ExecuteCommandExportCohortsAsScripts : BasicCommandExecution
{
    private readonly FileInfo _listFile;

    public ExecuteCommandExportCohortsAsScripts(IBasicActivateItems activator,
        [DemandsInitialization("The list file: one line per cohort, with the cohort id and then its folder. In the GUI, leave it out to choose the file.")]
        FileInfo listFile) : base(activator)
    {
        _listFile = listFile;
        if (listFile != null && !listFile.Exists)
            SetImpossible($"There is no list of cohorts at {listFile.FullName}.");
    }

    public override void Execute()
    {
        base.Execute();

        var listFile = _listFile ?? BasicActivator.SelectFile("Choose the list of cohorts and folders to export", "List of cohorts", "*.txt");
        if (listFile == null)
            return;

        var problems = new List<CohortScriptProblem>();
        var exports = Prepare(listFile, problems);
        if (problems.Count > 0)
            throw new CohortScriptProblemsException(problems,
                $"Cannot export the cohorts listed in {listFile.FullName}. Nothing was written.");

        var replacing = exports.Where(e => CohortExportFiles.WouldReplace(e.Folder)).ToArray();
        if (BasicActivator.IsInteractive && replacing.Length > 0 &&
            !BasicActivator.YesNo(
                $"{replacing.Length} of the {exports.Count} folders already hold an export of the same cohort. Replace their build scripts and catalogue manifests? Requirement files are kept.",
                "Replace exports"))
            return;

        foreach (var (files, folder) in exports)
            files.WriteTo(folder);

        BasicActivator.Show($"Exported {exports.Count} cohort(s):{Environment.NewLine}" + string.Join(Environment.NewLine,
            exports.Select(e => $"cohort {e.Files.Cohort.ID} \"{e.Files.Cohort.Name}\" to {e.Folder.FullName}")));
    }

    private List<(CohortExportFiles Files, DirectoryInfo Folder)> Prepare(FileInfo listFile, List<CohortScriptProblem> problems)
    {
        var baseDir = listFile.Directory!.FullName;
        var repository = BasicActivator.RepositoryLocator.CatalogueRepository;
        var exports = new List<(CohortExportFiles, DirectoryInfo)>();

        // folders are compared without case, because Windows and macOS treat Exports and exports as one folder, and
        // after following links, because two names can lead to one folder
        var folderOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cohortLines = new Dictionary<int, int>();

        var lines = File.ReadAllLines(listFile.FullName);
        for (var n = 1; n <= lines.Length; n++)
        {
            var text = lines[n - 1].Trim();
            if (text.Length == 0 || text.StartsWith('#'))
                continue;

            var split = text.IndexOfAny(new[] { ' ', '\t' });
            if (split < 0 || !int.TryParse(text[..split], out var id))
            {
                Problem(problems, n, $"The line should give a cohort id and then a folder, not \"{text}\".");
                continue;
            }

            var folder = new DirectoryInfo(Path.GetFullPath(text[split..].Trim(), baseDir));
            var key = RealPath(folder.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (cohortLines.TryGetValue(id, out var earlierLine))
            {
                Problem(problems, n, $"Cohort {id} is already listed on line {earlierLine}. List each cohort once.");
                continue;
            }

            cohortLines[id] = n;

            if (folderOwners.TryGetValue(key, out var otherCohort))
            {
                Problem(problems, n,
                    $"Cohort {id} and cohort {otherCohort} are both listed for the folder {folder.FullName}. Give each cohort its own folder.");
                continue;
            }

            folderOwners[key] = id;

            var cic = repository.GetAllObjectsInIDList<CohortIdentificationConfiguration>(new[] { id }).SingleOrDefault();
            if (cic == null)
            {
                Problem(problems, n, $"There is no cohort {id} on this platform. Check the id in the list.");
                continue;
            }

            try
            {
                var files = new CohortExportFiles(cic, BasicActivator.RepositoryLocator);
                if (files.OtherExportIn(folder) is { } other)
                {
                    problems.Add(new CohortScriptProblem(n, CohortScriptRefusal.FolderHoldsAnotherExport, other));
                    continue;
                }

                exports.Add((files, folder));
            }
            catch (CohortScriptExportException e)
            {
                problems.Add(new CohortScriptProblem(n, e.Reason, $"Cohort {id} \"{cic.Name}\" cannot be exported. {e.Message}"));
            }
        }

        if (problems.Count == 0 && exports.Count == 0)
            Problem(problems, 0, $"The list {listFile.FullName} names no cohort.");

        return exports;
    }

    // the path with every link and junction on it followed. A link counts also when its target does not exist yet,
    // because an earlier export in the list may make that target. A part of the path that is not there is kept as
    // written, because it cannot be a link.
    internal static string RealPath(string fullPath, int depth = 0)
    {
        if (depth > 40)
            throw new IOException($"The path {fullPath} has too many links to follow.");

        var root = Path.GetPathRoot(fullPath) ?? "";
        var current = root;
        foreach (var part in fullPath[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, part);
            if (new DirectoryInfo(current).LinkTarget is { } target)
                current = RealPath(Path.GetFullPath(target, Path.GetDirectoryName(current)!), depth + 1);
        }

        return current;
    }

    private static void Problem(List<CohortScriptProblem> problems, int line, string message) =>
        problems.Add(new CohortScriptProblem(line, CohortScriptRefusal.BadExportList, message));
}
