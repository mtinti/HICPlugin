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
using Rdmp.Core.CommandLine.Interactive.Picking;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Checks;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// The RDMP commands that a build script uses as they are, run the way the rebuild runs them: by name, with text
/// arguments, through RDMP's command invoker. Each test pins one behaviour the script format relies on, so an RDMP
/// upgrade that changes it fails here first. The activator is interactive, as in the GUI, and refuses every prompt.
/// </summary>
internal class RdmpCommandBehaviourTests : UnitTests
{
    private Catalogue _catalogue;
    private ExtractionInformation _identifier;
    private ExtractionInformation _date;

    [SetUp]
    public void MakeCatalogue()
    {
        _catalogue = MakeCohortCatalogue("Behaviour", out _identifier, out _date);
    }

    [Test]
    public void CreateNewCohortMakesARootAndTwoChildContainersWithoutAPrompt()
    {
        var activator = NewActivator();
        var result = Run(activator, "CreateNewCohortIdentificationConfiguration", "Behaviour cohort");

        var cic = Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", "Behaviour cohort").Single();
        var root = cic.RootCohortAggregateContainer;
        var children = root.GetSubContainers().OrderBy(c => c.Order).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, "The create command should complete.");
            Assert.That(activator.Prompts, Is.Empty, "The create command should not ask anything when given a name.");
            Assert.That(root.Operation, Is.EqualTo(SetOperation.EXCEPT), "The root should be EXCEPT.");
            Assert.That(root.Name, Is.EqualTo("Root Container"), "The root should be named Root Container.");
            Assert.That(children.Select(c => c.Name), Is.EqualTo(new[] { "Inclusion Criteria", "Exclusion Criteria" }),
                "The root should have Inclusion Criteria then Exclusion Criteria.");
            Assert.That(children.Select(c => c.Order), Is.EqualTo(new[] { 0, 1 }), "The two children should have Order 0 and 1.");
            Assert.That(children.Select(c => c.Operation), Is.All.EqualTo(SetOperation.UNION), "Both children should be UNION.");
        });
    }

    [Test]
    public void SetChangesTheOperationNameOrderAndDisabledFlagOfAnAutomaticContainerWithoutAPrompt()
    {
        var activator = NewActivator();
        Run(activator, "CreateNewCohortIdentificationConfiguration", "Container cohort");
        var inclusion = Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", "Container cohort")
            .Single().RootCohortAggregateContainer.GetSubContainers().Single(c => c.Name == "Inclusion Criteria");
        var target = $"CohortAggregateContainer:{inclusion.ID}";

        var results = new[]
        {
            Run(activator, "Set", target, "Operation", "INTERSECT"),
            Run(activator, "Set", target, "Name", "My inclusion"),
            Run(activator, "Set", target, "Order", "5"),
            Run(activator, "Set", target, "IsDisabled", "true")
        };

        inclusion.RevertToDatabaseState();
        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Completed), Is.All.True, "Every Set line should complete.");
            Assert.That(activator.Prompts, Is.Empty, "Set should not ask anything, not even to rename the container.");
            Assert.That(inclusion.Operation, Is.EqualTo(SetOperation.INTERSECT));
            Assert.That(inclusion.Name, Is.EqualTo("My inclusion"));
            Assert.That(inclusion.Order, Is.EqualTo(5));
            Assert.That(inclusion.IsDisabled, Is.True);
        });
    }

    [Test]
    public void SetChangesTheOrderHavingCountDescriptionAndDisabledFlagOfASet()
    {
        var (cic, container) = MakeCohort();
        var set = AddSet(cic, container);
        var activator = NewActivator();
        var target = $"AggregateConfiguration:{set.ID}";

        var results = new[]
        {
            Run(activator, "Set", target, "Order", "3"),
            Run(activator, "Set", target, "HavingSQL", "count(*) >= 2"),
            Run(activator, "Set", target, "CountSQL", "count(*) AS n"),
            Run(activator, "Set", target, "Description", "A described set"),
            Run(activator, "Set", target, "IsDisabled", "true")
        };

        var reloaded = Repository.GetObjectByID<AggregateConfiguration>(set.ID);
        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Completed), Is.All.True, "Every Set line should complete.");
            Assert.That(activator.Prompts, Is.Empty, "Set should not ask anything.");
            Assert.That(reloaded.Order, Is.EqualTo(3), "The order of a set is kept by its container, and Set should reach it.");
            Assert.That(reloaded.HavingSQL, Is.EqualTo("count(*) >= 2"));
            Assert.That(reloaded.CountSQL, Is.EqualTo("count(*) AS n"));
            Assert.That(reloaded.Description, Is.EqualTo("A described set"));
            Assert.That(reloaded.IsDisabled, Is.True);
        });
    }

    [Test]
    public void SetChangesTheSqlAliasAndGroupByOfADimension()
    {
        var (cic, container) = MakeCohort();
        var set = AddSet(cic, container);
        var dimension = new AggregateDimension(Repository, _date, set);
        var activator = NewActivator();
        var target = $"AggregateDimension:{dimension.ID}";

        var results = new[]
        {
            Run(activator, "Set", target, "SelectSQL", "MAX([db]..[tbl].[event_date])"),
            Run(activator, "Set", target, "Alias", "last_event"),
            Run(activator, "Set", target, "GroupBy", "false")
        };

        var reloaded = Repository.GetObjectByID<AggregateDimension>(dimension.ID);
        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Completed), Is.All.True, "Every Set line should complete.");
            Assert.That(reloaded.SelectSQL, Is.EqualTo("MAX([db]..[tbl].[event_date])"));
            Assert.That(reloaded.Alias, Is.EqualTo("last_event"));
            Assert.That(reloaded.GroupBy, Is.False);
        });
    }

    [Test]
    public void SetChangesTheDescriptionTicketAndFolderOfACohort()
    {
        var (cic, _) = MakeCohort();
        var activator = NewActivator();
        var target = $"CohortIdentificationConfiguration:{cic.ID}";

        var results = new[]
        {
            Run(activator, "Set", target, "Description", "Two\nlines"),
            Run(activator, "Set", target, "Ticket", "TEST-1"),
            Run(activator, "Set", target, "Folder", "\\tests\\features")
        };

        var reloaded = Repository.GetObjectByID<CohortIdentificationConfiguration>(cic.ID);
        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Completed), Is.All.True, "Every Set line should complete.");
            Assert.That(reloaded.Description, Is.EqualTo("Two\nlines"), "A new line inside a value should survive.");
            Assert.That(reloaded.Ticket, Is.EqualTo("TEST-1"));
            Assert.That(reloaded.Folder, Is.EqualTo("\\tests\\features"));
        });
    }

    [Test]
    public void AddNewFilterContainerMakesARootContainerOnASetWithoutAPrompt()
    {
        var (cic, container) = MakeCohort();
        var set = AddSet(cic, container);
        var activator = NewActivator();

        var result = Run(activator, "AddNewFilterContainer", $"AggregateConfiguration:{set.ID}");

        var root = (AggregateFilterContainer)Repository.GetObjectByID<AggregateConfiguration>(set.ID).RootFilterContainer;
        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, "Adding a root filter container to a set should complete.");
            Assert.That(root, Is.Not.Null, "The set should have a root filter container.");
            Assert.That(root?.Operation, Is.EqualTo(FilterContainerOperation.AND), "A new filter container should be AND.");
            Assert.That(activator.Prompts, Is.Empty, "The line should not ask anything.");
        });
    }

    [Test]
    public void AddNewFilterContainerCannotMakeAChildContainerFromTheCommandLine()
    {
        var (cic, container) = MakeCohort();
        var set = AddSet(cic, container);
        var activator = NewActivator();
        Run(activator, "AddNewFilterContainer", $"AggregateConfiguration:{set.ID}");
        var root = Repository.GetObjectByID<AggregateConfiguration>(set.ID).RootFilterContainer;

        // the command-line form takes only a set; the form that takes a container is for the GUI
        Assert.That(() => Run(activator, "AddNewFilterContainer", $"AggregateFilterContainer:{root.ID}"),
            Throws.Exception.With.Message.Contains("IRootFilterContainerHost"),
            "A child filter container needs a plugin command, because RDMP's command line cannot make one.");
    }

    [Test]
    public void AddNewFilterContainerIsRefusedWithoutAnErrorWhenTheSetAlreadyHasOne()
    {
        var (cic, container) = MakeCohort();
        var set = AddSet(cic, container);
        var activator = NewActivator();
        Run(activator, "AddNewFilterContainer", $"AggregateConfiguration:{set.ID}");

        var second = Run(activator, "AddNewFilterContainer", $"AggregateConfiguration:{set.ID}");

        Assert.Multiple(() =>
        {
            Assert.That(second.Completed, Is.False, "The second root container should not be made.");
            Assert.That(second.ImpossibleReason, Is.Not.Null,
                "RDMP reports a refused command through an event, not an exception, so the rebuild must listen for it.");
        });
    }

    [Test]
    public void SetChangesTheOperationAndDisabledFlagOfAFilterContainerAndOfAFilter()
    {
        var (cic, container) = MakeCohort();
        var set = AddSet(cic, container);
        var fc = new AggregateFilterContainer(Repository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        var filter = new AggregateFilter(Repository, "A filter", fc) { WhereSQL = "1 = 1" };
        filter.SaveToDatabase();
        var activator = NewActivator();

        var results = new[]
        {
            Run(activator, "Set", $"AggregateFilterContainer:{fc.ID}", "Operation", "OR"),
            Run(activator, "Set", $"AggregateFilterContainer:{fc.ID}", "IsDisabled", "true"),
            Run(activator, "Set", $"AggregateFilter:{filter.ID}", "IsDisabled", "true"),
            Run(activator, "Set", $"AggregateFilter:{filter.ID}", "Description", "Described")
        };

        var rfc = Repository.GetObjectByID<AggregateFilterContainer>(fc.ID);
        var rf = Repository.GetObjectByID<AggregateFilter>(filter.ID);
        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Completed), Is.All.True, "Every Set line should complete.");
            Assert.That(rfc.Operation, Is.EqualTo(FilterContainerOperation.OR));
            Assert.That(rfc.IsDisabled, Is.True);
            Assert.That(rf.IsDisabled, Is.True);
            Assert.That(rf.Description, Is.EqualTo("Described"));
        });
    }

    [Test]
    public void CreateNewFilterMakesAFilterWithTheGivenNameAndSqlAndNoParameters()
    {
        var (cic, container) = MakeCohort();
        var set = AddSet(cic, container);
        var fc = new AggregateFilterContainer(Repository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        var activator = NewActivator();

        var result = Run(activator, "CreateNewFilter", $"AggregateFilterContainer:{fc.ID}", "Uses a parameter",
            "code = @code");

        var filter = fc.GetFilters().SingleOrDefault();
        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, "CreateNewFilter should complete.");
            Assert.That(activator.Prompts, Is.Empty, "CreateNewFilter should not ask anything when given a name and SQL.");
            Assert.That(filter?.Name, Is.EqualTo("Uses a parameter"));
            Assert.That(filter?.WhereSQL, Is.EqualTo("code = @code"));
            Assert.That(filter?.GetAllParameters(), Is.Empty,
                "CreateNewFilter makes no parameter for @code, so the script must add each one.");
        });
    }

    [Test]
    public void DeleteRemovesAContainerWithoutAPromptWhenInteractiveDeletesIsOff()
    {
        var (_, container) = MakeCohort();
        var child = new CohortAggregateContainer(Repository, SetOperation.UNION);
        child.SaveToDatabase();
        container.AddChild(child);
        var activator = NewActivator();
        activator.InteractiveDeletes = false;

        var result = Run(activator, "Delete", $"CohortAggregateContainer:{child.ID}");

        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, "Delete should complete.");
            Assert.That(activator.Prompts, Is.Empty, "Delete should not ask when interactive deletes are off.");
            Assert.That(child.Exists(), Is.False, "The container should be gone.");
        });
    }

    [Test]
    public void DeleteAsksForConfirmationInTheGuiWhenInteractiveDeletesIsOn()
    {
        var (_, container) = MakeCohort();
        var child = new CohortAggregateContainer(Repository, SetOperation.UNION);
        child.SaveToDatabase();
        container.AddChild(child);
        var activator = NewActivator();
        activator.InteractiveDeletes = true;
        activator.AnswerYesNo = false;

        var result = Run(activator, "Delete", $"CohortAggregateContainer:{child.ID}");

        Assert.Multiple(() =>
        {
            Assert.That(activator.Prompts, Has.Count.EqualTo(1), "Delete should ask once.");
            Assert.That(child.Exists(), Is.True, "The container should stay when the user says no.");
            Assert.That(result.Completed, Is.True,
                "RDMP still reports the delete as completed when the user says no, so the rebuild must check the result.");
        });
    }

    private (CohortIdentificationConfiguration cic, CohortAggregateContainer root) MakeCohort()
    {
        var cic = new CohortIdentificationConfiguration(Repository, $"Behaviour {Guid.NewGuid():N}");
        cic.CreateRootContainerIfNotExists();
        return (cic, cic.RootCohortAggregateContainer);
    }

    private AggregateConfiguration AddSet(CohortIdentificationConfiguration cic, CohortAggregateContainer container)
    {
        var set = new AggregateConfiguration(Repository, _catalogue, "Behaviour set");
        _ = new AggregateDimension(Repository, _identifier, set);
        cic.EnsureNamingConvention(set);
        container.AddChild(set, 0);
        return set;
    }

    private PromptRefusingActivator NewActivator() => new(RepositoryLocator);

    private static (bool Completed, string ImpossibleReason) Run(IBasicActivateItems activator, params string[] line)
    {
        var invoker = new CommandInvoker(activator);
        var type = invoker.GetSupportedCommands()
            .First(t => BasicCommandExecution.GetCommandName(t.Name).Equals(line[0], StringComparison.OrdinalIgnoreCase));
        var completed = false;
        string reason = null;
        invoker.CommandCompleted += (_, _) => completed = true;
        invoker.CommandImpossible += (_, e) => reason = e.Command.ReasonCommandImpossible;
        invoker.ExecuteCommand(type, new CommandLineObjectPicker(line.Skip(1).ToArray(), activator));
        return (completed, reason);
    }

    private Catalogue MakeCohortCatalogue(string name, out ExtractionInformation idEi, out ExtractionInformation dateEi)
    {
        var throwaway = WhenIHaveA(Repository, out dateEi, out var otherEi);
        var cata = throwaway.Catalogue;
        cata.Name = name;
        cata.SaveToDatabase();
        otherEi.IsExtractionIdentifier = true;
        otherEi.SaveToDatabase();
        idEi = otherEi;
        throwaway.DeleteInDatabase();
        return cata;
    }

    /// <summary>
    /// An interactive activator, as in the GUI. It records every question and refuses it, except a yes/no question,
    /// which it answers with <see cref="AnswerYesNo"/>.
    /// </summary>
    private sealed class PromptRefusingActivator : ConsoleInputManager
    {
        public List<string> Prompts { get; } = new();
        public bool AnswerYesNo { get; set; }

        public PromptRefusingActivator(IRDMPPlatformRepositoryServiceLocator locator)
            : base(locator, ThrowImmediatelyCheckNotifier.Quiet)
        {
            DisallowInput = false;
        }

        private Exception Refuse(string what)
        {
            Prompts.Add(what);
            return new InvalidOperationException($"The command asked a question: {what}");
        }

        public override bool YesNo(DialogArgs args, out bool chosen)
        {
            Prompts.Add($"YesNo: {args.TaskDescription}");
            chosen = AnswerYesNo;
            return true;
        }

        public override bool TypeText(DialogArgs args, int maxLength, string initialText, out string text,
            bool requireSaneHeaderText) => throw Refuse($"TypeText: {args.TaskDescription}");

        public override bool SelectEnum(DialogArgs args, Type enumType, out Enum chosen) =>
            throw Refuse($"SelectEnum: {args.TaskDescription}");

        public override bool SelectType(DialogArgs args, Type[] available, out Type chosen) =>
            throw Refuse($"SelectType: {args.TaskDescription}");

        public override IMapsDirectlyToDatabaseTable SelectOne(DialogArgs args,
            IMapsDirectlyToDatabaseTable[] availableObjects) => throw Refuse($"SelectOne: {args.TaskDescription}");

        public override bool SelectObject<T>(DialogArgs args, T[] available, out T selected) =>
            throw Refuse($"SelectObject: {args.TaskDescription}");

        public override bool SelectObjects<T>(DialogArgs args, T[] available, out T[] selected) =>
            throw Refuse($"SelectObjects: {args.TaskDescription}");

        public override DirectoryInfo SelectDirectory(string prompt) => throw Refuse($"SelectDirectory: {prompt}");

        public override FileInfo SelectFile(string prompt) => throw Refuse($"SelectFile: {prompt}");

        public override FileInfo SelectFile(string prompt, string patternDescription, string pattern) =>
            throw Refuse($"SelectFile: {prompt}");

        public override FileInfo[] SelectFiles(string prompt, string patternDescription, string pattern) =>
            throw Refuse($"SelectFiles: {prompt}");

        public override void Show(string title, string message)
        {
        }
    }
}
