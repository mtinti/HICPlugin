// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandLine.Interactive.Picking;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.Repositories;
using Tests.Common;

namespace Rdmp.LinkedServerCohortSql.Tests;

public sealed class CommandTests : UnitTests
{
    private CohortFixture _f;
    private RecordingActivator _activator;
    private string _directory;

    [SetUp]
    protected override void SetUp()
    {
        Repository = new MemoryDataExportRepository();
        _f = new CohortFixture(Repository); _f.Set("A");
        _activator = new RecordingActivator(new RepositoryProvider(Repository)) { Interactive = true };
        _directory = Path.Combine(Path.GetTempPath(), "linked-sql-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public void ShowAndFileHaveIdenticalBodiesAndCommentedTitle()
    {
        new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic).Execute();
        var (title, body) = _activator.Messages.Single();
        Assert.That(title, Is.EqualTo("-- Linked cohort (Linked Servers) SQL"));
        Assert.That(body, Does.StartWith("-- Linked-server cohort SQL"));
        _activator.Messages.Clear();
        var file = new FileInfo(Path.Combine(_directory, "out.sql"));
        new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic, file).Execute();
        Assert.That(File.ReadAllText(file.FullName), Is.EqualTo(body));
        Assert.That(_activator.Messages, Is.Empty);
    }

    [TestCase(false)] [TestCase(true)]
    public void PickerRequiresExplicitOverwrite(bool accept)
    {
        var file = new FileInfo(Path.Combine(_directory, "out.sql")); File.WriteAllText(file.FullName, "existing");
        _activator.SelectedFile = file; _activator.Overwrite = accept;
        new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic, askForFile: true, staged: false).Execute();
        Assert.That(_activator.FileRequests.Single(), Is.EqualTo(("Save linked-server SQL", "SQL files", "*.sql")));
        Assert.That(_activator.Confirmations.Single().WindowTitle, Is.EqualTo("Overwrite file?"));
        Assert.That(File.ReadAllText(file.FullName), accept ? Does.StartWith("-- Linked-server") : Is.EqualTo("existing"));
        Assert.That(_activator.Messages, Is.Empty);
    }

    [Test]
    public void PickerCancelHasNoOutput()
    {
        new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic, askForFile: true, staged: false).Execute();
        Assert.That(_activator.Messages, Is.Empty);
        Assert.That(Directory.GetFiles(_directory), Is.Empty);
    }

    [TestCase(true, 20000, false)] [TestCase(true, 20001, true)] [TestCase(false, 20001, false)]
    public void Threshold(bool interactive, int length, bool expected) =>
        Assert.That(ExecuteCommandViewCohortLinkedServerSql.NeedsFile(interactive, length), Is.EqualTo(expected));

    [TestCase(true, 20000)] [TestCase(true, 20001)] [TestCase(false, 20001)]
    public void RealLongOutputUsesFileOnlyWhenInteractive(bool interactive, int length)
    {
        _activator.Interactive = interactive;
        var filter = _f.Filter(_f.Root.GetAllAggregateConfigurationsRecursively().Single(), "1=1 -- x");
        var initial = _f.Build().ToScript(!interactive).Length;
        filter.WhereSQL += new string('x', length - initial); filter.SaveToDatabase();
        new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic).Execute();
        Assert.That(_activator.FileRequests.Count, Is.EqualTo(interactive && length > 20000 ? 1 : 0));
        if (interactive && length > 20000) Assert.That(_activator.Messages.Single().Message, Does.Contain("too long"));
        else Assert.That(_activator.Messages.Single().Message.Length, Is.EqualTo(length));
    }

    [Test]
    public void CommandInvokerSupportsOptionalFile()
    {
        _activator.Interactive = false;
        var invoker = new CommandInvoker(_activator);
        var path = Path.Combine(_directory, "cli.sql");
        invoker.ExecuteCommand(typeof(ExecuteCommandViewCohortLinkedServerSql),
            new CommandLineObjectPicker([$"CohortIdentificationConfiguration:{_f.Cic.ID}", path], _activator));
        Assert.That(File.ReadAllText(path), Does.Not.Contain("-- Tip:"), "the tip is pointless inside the requested file");
        Assert.That(_activator.Messages, Is.Empty);
        invoker.ExecuteCommand(typeof(ExecuteCommandViewCohortLinkedServerSql),
            new CommandLineObjectPicker([$"CohortIdentificationConfiguration:{_f.Cic.ID}"], _activator));
        Assert.That(_activator.Messages.Single().Message, Does.StartWith("-- Tip:"));
        Assert.That(_activator.Messages.Single().Message, Does.EndWith(File.ReadAllText(path)));
    }

    [Test]
    public void MenuAndImpossibleStatesMatchContract()
    {
        var ui = new LinkedServerCohortSqlUserInterface(_activator);
        Assert.That(ui.GetAdditionalRightClickMenuItems(_f.Cic).Select(c => c.GetCommandName()), Is.Unique);
        Assert.That(ui.GetAdditionalRightClickMenuItems(_f.Cic).Select(c => c.GetCommandName()), Is.EqualTo(MenuOrder));
        Assert.That(ui.GetAdditionalRightClickMenuItems(_f.Cic).Select(c => c.SuggestedCategory), Is.All.EqualTo("Linked Servers SQL"), "one submenu");
        Assert.That(ui.GetAdditionalRightClickMenuItems(new Catalogue(Repository, "C")), Is.Empty);
        var empty = new CohortIdentificationConfiguration(Repository, "Empty");
        var noRoot = new ExecuteCommandViewCohortLinkedServerSql(_activator, empty);
        Assert.That(noRoot.IsImpossible, Is.True);
        Assert.That(noRoot.ReasonCommandImpossible, Is.EqualTo("Cohort has no root container"));
        var noAssoc = new ExecuteCommandViewCohortLinkedServerSql(_activator, (ProjectCohortIdentificationConfigurationAssociation)null);
        Assert.That(noAssoc.IsImpossible, Is.True);
        Assert.That(noAssoc.ReasonCommandImpossible, Is.EqualTo("Association has no cohort identification configuration"));
    }

    [Test]
    public void AssociationResolvesConfigurationBeforeExecuteAndHandlesDeletedTarget()
    {
        var repository = (MemoryDataExportRepository)Repository;
        var project = new Project(repository, "Project");
        var association = new ProjectCohortIdentificationConfigurationAssociation(repository, project, _f.Cic);
        var commands = new LinkedServerCohortSqlUserInterface(_activator).GetAdditionalRightClickMenuItems(association).ToArray();
        Assert.That(commands.Select(c => c.GetCommandName()), Is.EqualTo(MenuOrder));
        Assert.That(commands.Select(c => c.SuggestedCategory), Is.All.EqualTo("Linked Servers SQL"));
        Assert.That(commands.All(c => !c.IsImpossible), Is.True);
        commands[0].Execute();
        Assert.That(_activator.Messages.Single().Message, Is.EqualTo(_f.BuildStaged().ToScript()));
        _activator.Messages.Clear();
        commands[2].Execute();
        Assert.That(_activator.Messages.Single().Message, Is.EqualTo(_f.Build().ToScript()));
        _f.Cic.DeleteInDatabase(); association.ClearAllInjections();
        Assert.That(new ExecuteCommandViewCohortLinkedServerSql(_activator, association).IsImpossible, Is.True);
    }

    [TestCase(false)] [TestCase(true)]
    public void LongOutputReportsSaveOrDeclinedOverwrite(bool accept)
    {
        _f.Filter(_f.Root.GetAllAggregateConfigurationsRecursively().Single(), "1=1 -- " + new string('x', 20000));
        var file = new FileInfo(Path.Combine(_directory, "large.sql")); File.WriteAllText(file.FullName, "existing");
        _activator.SelectedFile = file; _activator.Overwrite = accept;
        new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic).Execute();
        Assert.That(_activator.Messages.Single().Message,
            accept ? Does.Contain("Saved to " + file.FullName) : Does.Contain("save to file..."));
        Assert.That(File.ReadAllText(file.FullName), accept ? Is.EqualTo(_f.Build().ToScript()) : Is.EqualTo("existing"));
    }

    private static readonly string[] MenuOrder =
    [
        "Query Builder SQL (Linked Servers, staged)", "Query Builder SQL (Linked Servers, staged) save to file...",
        "Query Builder SQL (Linked Servers)", "Query Builder SQL (Linked Servers) save to file..."
    ];

    // Plan 0.7.7 (r) and 0.7.5: the positional CLI forms.
    [Test]
    public void CommandInvokerSelectsShapePositionally()
    {
        _activator.Interactive = false;
        var invoker = new CommandInvoker(_activator);
        var id = $"CohortIdentificationConfiguration:{_f.Cic.ID}";
        var staged = Path.Combine(_directory, "staged.sql");
        invoker.ExecuteCommand(typeof(ExecuteCommandViewCohortLinkedServerSql), new CommandLineObjectPicker([id, staged, "true"], _activator));
        Assert.That(File.ReadAllText(staged), Is.EqualTo(_f.BuildStaged().ToScript()));
        Assert.That(File.ReadAllText(staged), Does.Contain("Shape: staged").And.Contain("INTO #rdmp_cohort"));
        var composite = Path.Combine(_directory, "composite.sql");
        invoker.ExecuteCommand(typeof(ExecuteCommandViewCohortLinkedServerSql), new CommandLineObjectPicker([id, composite], _activator));
        Assert.That(File.ReadAllText(composite), Is.EqualTo(_f.Build().ToScript()));
        Assert.That(File.ReadAllText(composite), Does.Contain("Shape: composite").And.Contain("READ, DO NOT RUN ON PRODUCTION"));
        invoker.ExecuteCommand(typeof(ExecuteCommandViewCohortLinkedServerSql), new CommandLineObjectPicker([id, "null", "true"], _activator));
        Assert.That(_activator.Messages.Single().Message, Is.EqualTo(_f.BuildStaged().ToScript(includeConsoleTip: true)));
    }

    [TestCase("true")] [TestCase("False")]
    public void BooleanFileNameIsImpossible(string name)
    {
        var command = new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic, new FileInfo(Path.Combine(_directory, name)));
        Assert.That(command.IsImpossible, Is.True);
        Assert.That(command.ReasonCommandImpossible, Does.Contain("did you mean '<id> null " + name.ToLowerInvariant() + "'"));
        Assert.That(new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic, new FileInfo(Path.Combine(_directory, "true.sql"))).IsImpossible, Is.False);
    }

    [Test]
    public void StagedTooLongNamesTheStagedSaveEntry()
    {
        _f.Filter(_f.Root.GetAllAggregateConfigurationsRecursively().Single(), "1=1 -- " + new string('x', 20000));
        new ExecuteCommandViewCohortLinkedServerSql(_activator, _f.Cic, null, staged: true).Execute();
        Assert.That(_activator.Messages.Single().Message, Does.Contain("Query Builder SQL (Linked Servers, staged) save to file..."));
    }
}
