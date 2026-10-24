// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Checks;
using RdmpCohortScript;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// Where the export writes when it runs from the GUI menu, which gives it no folder: it asks for one, writes
/// nothing when the user cancels, and asks before it replaces an earlier export.
/// </summary>
internal class CohortScriptExportFolderTests : UnitTests
{
    [Test]
    public void ExportFromTheGuiAsksForAFolderAndWritesTheFilesThere()
    {
        var cic = MakeCohort();
        var folder = TempFolder();
        try
        {
            var activator = new AnsweringActivator(RepositoryLocator) { FolderToChoose = folder };
            new ExecuteCommandExportCohortAsScript(activator, cic).Execute();

            var exported = Path.Join(folder.FullName, cic.Name);
            Assert.Multiple(() =>
            {
                Assert.That(activator.FolderQuestions, Is.EqualTo(1), "The export should ask once for a folder.");
                Assert.That(File.Exists(Path.Join(exported, "build.script.yaml")), Is.True,
                    "The build script should be written into the chosen folder.");
                Assert.That(File.Exists(Path.Join(exported, "catalogue-manifest.yaml")), Is.True,
                    "The catalogue manifest should be written into the chosen folder.");
                Assert.That(File.Exists(Path.Join(exported, "requirement.md")), Is.True,
                    "The requirement placeholder should be written into the chosen folder.");
            });
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Test]
    public void ExportFromTheGuiWritesNothingWhenTheUserCancelsTheFolderChoice()
    {
        var cic = MakeCohort();
        var activator = new AnsweringActivator(RepositoryLocator) { FolderToChoose = null };

        new ExecuteCommandExportCohortAsScript(activator, cic).Execute();

        Assert.That(Directory.Exists(Path.Join(Environment.CurrentDirectory, cic.Name)), Is.False,
            "A cancelled export should not fall back to the current directory.");
    }

    [Test]
    public void ExportFromTheGuiKeepsAnEarlierExportUnlessTheUserAgreesToReplaceIt()
    {
        var cic = MakeCohort();
        var folder = TempFolder();
        try
        {
            var exported = Directory.CreateDirectory(Path.Join(folder.FullName, cic.Name)).FullName;
            var script = Path.Join(exported, "build.script.yaml");
            var requirement = Path.Join(exported, "requirement.md");
            File.WriteAllText(script, "earlier export");
            File.WriteAllText(requirement, "written by hand");

            var declines = new AnsweringActivator(RepositoryLocator) { FolderToChoose = folder, ReplaceAnswer = false };
            new ExecuteCommandExportCohortAsScript(declines, cic).Execute();
            Assert.Multiple(() =>
            {
                Assert.That(declines.ReplaceQuestions, Is.EqualTo(1), "The export should ask before replacing.");
                Assert.That(File.ReadAllText(script), Is.EqualTo("earlier export"),
                    "The earlier build script should stay when the user says no.");
            });

            var agrees = new AnsweringActivator(RepositoryLocator) { FolderToChoose = folder, ReplaceAnswer = true };
            new ExecuteCommandExportCohortAsScript(agrees, cic).Execute();
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(script), Does.Contain("Commands:"),
                    "The build script should be replaced when the user says yes.");
                Assert.That(File.ReadAllText(requirement), Is.EqualTo("written by hand"),
                    "The requirement written by hand should never be replaced.");
            });
        }
        finally
        {
            folder.Delete(true);
        }
    }

    private CohortIdentificationConfiguration MakeCohort()
    {
        var cic = new CohortIdentificationConfiguration(Repository, $"Export folder test {Guid.NewGuid():N}");
        cic.CreateRootContainerIfNotExists();
        return cic;
    }

    private static DirectoryInfo TempFolder() =>
        Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"rdmp-cohortscript-{Guid.NewGuid()}"));

    /// <summary>An interactive activator that answers the folder and replace questions from its properties.</summary>
    private sealed class AnsweringActivator : ConsoleInputManager
    {
        public DirectoryInfo FolderToChoose { get; init; }
        public bool ReplaceAnswer { get; init; }
        public int FolderQuestions { get; private set; }
        public int ReplaceQuestions { get; private set; }

        public AnsweringActivator(IRDMPPlatformRepositoryServiceLocator locator)
            : base(locator, ThrowImmediatelyCheckNotifier.Quiet)
        {
            DisallowInput = false;
        }

        public override DirectoryInfo SelectDirectory(string prompt)
        {
            FolderQuestions++;
            return FolderToChoose;
        }

        public override bool YesNo(DialogArgs args, out bool chosen)
        {
            // chosen is the answer; the return value says the user did not cancel
            ReplaceQuestions++;
            chosen = ReplaceAnswer;
            return true;
        }

        public override void Show(string title, string message)
        {
        }
    }
}
