// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Checks;
using Rdmp.Core.ReusableLibraryCode.Settings;
using RdmpCohortScript;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// How often a rebuild refreshes RDMP's list of objects, and what it shows and opens. RDMP loads the whole list
/// again on each refresh, so a refresh after every line makes a rebuild slow. The lines run without refreshing,
/// and the build refreshes once at the end, or after a failure only when the clean-up may have left something.
/// </summary>
internal class CohortScriptRefreshTests : UnitTests
{
    private FaultyRepository _repository;
    private FaultyLocator _locator;
    private Catalogue _catalogue;
    private string _column;
    private ExtractionInformation _identifier;
    private Project _project;

    [SetUp]
    public void MakeCatalogue()
    {
        _repository = new FaultyRepository();
        _locator = new FaultyLocator(new RepositoryProvider(_repository));
        var throwaway = WhenIHaveA(_repository, out _, out var first);
        _catalogue = throwaway.Catalogue;
        _catalogue.Name = $"Refresh catalogue {Guid.NewGuid():N}";
        _catalogue.SaveToDatabase();
        first.IsExtractionIdentifier = true;
        first.SaveToDatabase();
        _column = first.GetRuntimeName();
        _identifier = first;
        throwaway.DeleteInDatabase();
        _project = WhenIHaveA<Project>(_repository);
    }

    private const string CreateLine = "CreateNewCohortIdentificationConfiguration \"Refresh test\" => $cic1 $c1 $c2 $c3";

    private string SetLine(string handle) =>
        $"AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"{_column}\" false => ${handle} ${handle}.{_column}";

    private string LinkLine => $"LinkCohortToProject CohortIdentificationConfiguration:$cic1 Project:{_project.ID} \"{_project.Name}\"";

    private string TwoSetScript => Script(CreateLine, SetLine("a1"), SetLine("a2"), "Set CohortAggregateContainer:$c2 Operation EXCEPT",
        "AddNewFilterContainer AggregateConfiguration:$a1 => $fc1", "Delete CohortAggregateContainer:$c3");

    // the second link of the same cohort to the same project is refused, after the set was made
    private string ScriptThatFailsAtItsLastLine => Script(CreateLine, SetLine("a1"), LinkLine, LinkLine);

    // ---- a build that works

    [Test]
    public void ARebuildRefreshesRdmpOnceWithTheNewCohort()
    {
        var activator = new RecordingActivator(_locator);
        var name = $"Once {Guid.NewGuid():N}";

        RebuildWith(activator, TwoSetScript, name);

        var cic = _repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single();
        var sets = cic.RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively().Select(s => s.ID).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(activator.Published, Is.EqualTo(new[] { Describe(cic) }),
                "The rebuild should refresh RDMP once, with the new cohort.");
            Assert.That(activator.CoreChildProvider.AllCohortIdentificationConfigurations.Select(c => c.ID), Has.Member(cic.ID),
                "After the refresh, RDMP's list of objects should hold the new cohort.");
            Assert.That(sets, Has.Length.EqualTo(2), "The cohort should have its two sets.");
            Assert.That(activator.CoreChildProvider.AllAggregateConfigurations.Select(a => a.ID), Is.SupersetOf(sets),
                "After the refresh, RDMP's list of objects should hold both sets.");
        });
    }

    [Test]
    public void ARebuildWithADeleteLineRefreshesOnce()
    {
        var activator = new RecordingActivator(_locator);
        var containers = new List<CohortAggregateContainer>();
        _repository.Inserting += (_, e) =>
        {
            if (e.Object is CohortAggregateContainer c)
                containers.Add(c);
        };

        RebuildWith(activator, TwoSetScript, $"Delete {Guid.NewGuid():N}");

        var deleted = containers.Where(c => !_repository.StillExists(c)).Select(c => c.ID).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(activator.Published, Has.Count.EqualTo(1), "The rebuild should refresh RDMP once, even with a Delete line.");
            Assert.That(deleted, Has.Length.EqualTo(1), "The Delete line should remove the second automatic container.");
            Assert.That(activator.CoreChildProvider.AllCohortAggregateContainers.Select(c => c.ID), Has.No.AnyOf(deleted),
                "After the refresh, RDMP's list of objects should not hold the deleted container.");
        });
    }

    [Test]
    public void ASecondBuildWithTheSameActivatorAlsoRefreshesOnce()
    {
        var activator = new RecordingActivator(_locator);
        var first = $"First {Guid.NewGuid():N}";
        var second = $"Second {Guid.NewGuid():N}";

        RebuildWith(activator, TwoSetScript, first);
        RebuildWith(activator, TwoSetScript, second);

        Assert.That(activator.Published, Is.EqualTo(new[] { Describe(Cohort(first)), Describe(Cohort(second)) }),
            "Each build should refresh RDMP once, with its own cohort.");
    }

    [Test]
    public void ABuildShowsTheNewCohortInTheTreeAndOpensNothing()
    {
        var activator = new RecordingActivator(_locator);
        var name = $"Shown {Guid.NewGuid():N}";

        RebuildWith(activator, TwoSetScript, name);

        var cic = Describe(Cohort(name));
        Assert.That(activator.Events, Is.EqualTo(new[] { $"publish {cic}", $"show Built cohort {Cohort(name).ID}", $"emphasise {cic}" }),
            "The build should refresh RDMP, then say it built the cohort, then select the cohort in the tree, and open nothing.");
    }

    [Test]
    public void TheWrapperDropsEmphasisAndActivationDuringTheBuild()
    {
        var activator = new RecordingActivator(_locator);
        var wrapper = new NoPublishActivator(activator);
        var cic = new CohortIdentificationConfiguration(_repository, $"Not shown {Guid.NewGuid():N}");

        wrapper.RequestItemEmphasis(this, new EmphasiseRequest(cic));
        wrapper.Activate(cic);
        wrapper.Publish(cic);
        wrapper.PublishNearest(cic);

        Assert.That(activator.Events, Is.Empty, "The wrapper should not pass a refresh, an emphasis or an activation to RDMP.");
    }

    [Test]
    public void TheWrapperDeletesWithoutRefreshing()
    {
        var activator = new RecordingActivator(_locator) { InteractiveDeletes = false };
        var container = new CohortAggregateContainer(_repository, SetOperation.UNION);

        Assert.That(new NoPublishActivator(activator).DeleteWithConfirmation(container), Is.True, "The delete should report that it deleted.");
        Assert.Multiple(() =>
        {
            Assert.That(_repository.StillExists(container), Is.False, "The container should be deleted.");
            Assert.That(activator.Events, Is.Empty, "The delete should not refresh RDMP.");
        });
    }

    // ---- the end of a build that fails to refresh or to show the cohort

    [Test]
    public void AFailedFinalRefreshKeepsTheCohortAndNamesIt()
    {
        var activator = new RecordingActivator(_locator) { FailOnPublish = true };
        var name = $"Not refreshed {Guid.NewGuid():N}";

        RebuildWith(activator, TwoSetScript, name);

        var cic = Cohort(name);
        Assert.Multiple(() =>
        {
            Assert.That(cic.RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively(), Has.Count.EqualTo(2),
                "The cohort should be saved with both sets.");
            Assert.That(activator.Warnings.Single(), Does.Contain($"Built cohort {cic.ID}").And.Contain("The cohort is saved"),
                "The warning should name the cohort and say that it is saved.");
            Assert.That(activator.Events.Where(e => e.StartsWith("emphasise", StringComparison.Ordinal)), Is.Empty,
                "The build should not try to show a cohort RDMP's list of objects does not hold.");
        });
    }

    [Test]
    public void AFailureToShowTheCohortReportsItAsSaved()
    {
        var activator = new RecordingActivator(_locator) { FailOnEmphasis = true };
        var name = $"Not selected {Guid.NewGuid():N}";

        RebuildWith(activator, TwoSetScript, name);

        var cic = Cohort(name);
        Assert.Multiple(() =>
        {
            Assert.That(activator.Published, Is.EqualTo(new[] { Describe(cic) }), "The cohort should be refreshed before RDMP tries to show it.");
            Assert.That(activator.Warnings.Single(), Does.Contain($"Cohort {cic.ID}").And.Contain("is built and saved").And.Contain("could not select it"),
                "The warning should say only that the saved cohort could not be selected in the tree.");
        });
    }

    // ---- a build whose line fails

    [Test]
    public void AFailureAfterADeleteLineRefreshesNothingAndLeavesNothingBehind()
    {
        var activator = new RecordingActivator(_locator);
        var before = Counts();

        var e = Assert.Throws<CohortScriptRebuildException>(() =>
            RebuildWith(activator, Script(CreateLine, SetLine("a1"), "Delete CohortAggregateContainer:$c3", LinkLine, LinkLine), $"Fails {Guid.NewGuid():N}"));

        Assert.Multiple(() =>
        {
            Assert.That(e!.Cleanup.Incomplete, Is.False, "The clean-up should remove everything the build made.");
            Assert.That(Counts(), Is.EqualTo(before), "Nothing the build made should be left.");
            Assert.That(activator.Published, Is.Empty, "A complete clean-up should not refresh RDMP: its list never held the build's objects.");
            Assert.That(activator.CoreChildProvider.AllCohortIdentificationConfigurations.Select(c => c.ID), Has.No.Member(e.Cleanup.Cohort.ID),
                "RDMP's list of objects should not hold the removed cohort.");
        });
    }

    [Test]
    public void AnIncompleteCleanupRefreshesOnceAndNamesTheLeftovers([Values(false, true)] bool selectiveRefresh)
    {
        var activator = new RecordingActivator(_locator);
        _repository.RefuseDelete = o => o is AggregateConfiguration;

        var e = WithSelectiveRefresh(selectiveRefresh, () => Assert.Throws<CohortScriptRebuildException>(() =>
            RebuildWith(activator, ScriptThatFailsAtItsLastLine, $"Leftover {Guid.NewGuid():N}")));

        var set = _repository.GetAllObjects<AggregateConfiguration>().Single(a => a.Name.StartsWith($"cic_{e.Cleanup.Cohort.ID}_", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(e.Message, Does.StartWith("Line 6 of the build script"), "The failing line should stay the main error.");
            Assert.That(e.Cleanup.Leftovers, Has.Exactly(1).Contains($"AggregateConfiguration {set.ID}"), "The leftovers should name the set.");
            Assert.That(e.Message, Does.Contain("Delete them by hand"), "The message should say what to do about the leftovers.");
            Assert.That(activator.Published, Is.EqualTo(new[] { Describe(e.Cleanup.Cohort) }), "The build should refresh RDMP once, with the removed cohort.");
            Assert.That(activator.CoreChildProvider.AllAggregateConfigurations.Select(a => a.ID), Has.Member(set.ID),
                "After the refresh, RDMP's list of objects should hold the leftover set.");
            Assert.That(activator.CoreChildProvider.AllCohortIdentificationConfigurations.Select(c => c.ID), Has.No.Member(e.Cleanup.Cohort.ID),
                "After the refresh, RDMP's list of objects should not hold the removed cohort.");
            Assert.That(activator.Events.Where(x => x.StartsWith("emphasise", StringComparison.Ordinal)), Is.Empty,
                "A failed build should not show anything in the tree.");
        });
    }

    [Test]
    public void ADeleteThatFailsOnceAndThenSucceedsLeavesNoLeftovers()
    {
        var activator = new RecordingActivator(_locator);
        var refused = false;
        _repository.RefuseDelete = o =>
        {
            if (refused || o is not AggregateConfiguration)
                return false;
            refused = true;
            return true;
        };
        var before = Counts();

        var e = Assert.Throws<CohortScriptRebuildException>(() => RebuildWith(activator, ScriptThatFailsAtItsLastLine, $"Retry {Guid.NewGuid():N}"));

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.True, "The first delete of the set should have been refused.");
            Assert.That(e!.Cleanup.Leftovers, Is.Empty, "A set that a later pass removed should not be reported as left.");
            Assert.That(Counts(), Is.EqualTo(before), "Nothing the build made should be left.");
            Assert.That(activator.Published, Is.Empty, "A clean-up that removed everything should not refresh RDMP.");
        });
    }

    [TestCase(true, TestName = "AForcedJoinThatIsGoneAfterAFailedRemovalIsNotALeftover")]
    [TestCase(false, TestName = "AForcedJoinThatStaysAfterAFailedRemovalIsALeftover")]
    public void AFailedForcedJoinRemovalIsCheckedAgain(bool goneAnyway)
    {
        var activator = new RecordingActivator(_locator);
        var table = ForcedJoinTable.JoinedTo(_repository, _identifier);
        _repository.FailToBreakForcedJoins = true;
        _repository.BreakForcedJoinsBeforeFailing = goneAnyway;
        var join = $"AddForcedJoin AggregateConfiguration:$a1 TableInfo:{table.ID} \"{table.Name}\"";

        var e = Assert.Throws<CohortScriptRebuildException>(() =>
            RebuildWith(activator, Script(CreateLine, SetLine("a1"), join, LinkLine, LinkLine), $"Forced join {Guid.NewGuid():N}"));

        Assert.That(_repository.GetAllObjects<AggregateConfiguration>().Where(a => a.Name.StartsWith($"cic_{e!.Cleanup.Cohort.ID}_", StringComparison.Ordinal)),
            Is.Empty, "The set should be removed in both cases.");
        if (goneAnyway)
            Assert.Multiple(() =>
            {
                Assert.That(e.Cleanup.Leftovers, Is.Empty, "A forced join that is gone at the end should not be reported as left.");
                Assert.That(activator.Published, Is.Empty, "A clean-up that removed everything should not refresh RDMP.");
            });
        else
            Assert.Multiple(() =>
            {
                Assert.That(e.Cleanup.Leftovers, Has.Exactly(1).Contains($"to table {table.ID}"), "A forced join still there should be reported as left.");
                Assert.That(activator.Published, Has.Count.EqualTo(1), "A clean-up that left something should refresh RDMP once.");
            });
    }

    // with RDMP's "always join everything" setting on, RDMP lists the catalogue's main table as a forced join of the
    // set, though none is saved; the database then refuses to remove it, because no row is deleted
    [Test]
    public void AJoinTheSettingAddsIsNotRemovedOrReportedAsLeft()
    {
        var activator = new RecordingActivator(_locator);
        _repository.RefuseToRemoveAMissingForcedJoin = true;
        var before = Counts();

        var e = WithAlwaysJoinEverything(() => Assert.Throws<CohortScriptRebuildException>(() =>
            RebuildWith(activator, ScriptThatFailsAtItsLastLine, $"Join everything {Guid.NewGuid():N}")));

        Assert.Multiple(() =>
        {
            Assert.That(e!.Cleanup.Leftovers, Is.Empty, "A join that was never saved should not be reported as left.");
            Assert.That(Counts(), Is.EqualTo(before), "Nothing the build made should be left.");
            Assert.That(activator.Published, Is.Empty, "A clean-up that removed everything should not refresh RDMP.");
        });
    }

    [Test]
    public void ACleanupThatCannotCheckAnObjectSaysSo()
    {
        var uncheckable = new UncheckableContainer(_repository);

        var result = new CohortScriptCleanup(_locator).Remove(null, new IMapsDirectlyToDatabaseTable[] { uncheckable });

        Assert.Multiple(() =>
        {
            Assert.That(result.Unverified, Has.Exactly(1).Contains($"UncheckableContainer {uncheckable.ID}"),
                "The object whose check failed should be named as not checked.");
            Assert.That(result.Incomplete, Is.True, "A clean-up that could not check everything is not complete.");
            Assert.That(result.Sentence, Does.Contain("could not check"), "The message should not claim the clean-up was complete.");
        });
    }

    [Test]
    public void AFailedReadDuringTheCleanupKeepsTheLineError()
    {
        var activator = new RecordingActivator(_locator);

        // the link line fails after saving, and the clean-up's first read of the platform then fails once
        _repository.FailAfterInsert = o =>
        {
            if (o is not ProjectCohortIdentificationConfigurationAssociation)
                return null;
            _locator.FailNextCatalogueRead = true;
            return new InvalidOperationException("A failure after the link was saved.");
        };

        var e = Assert.Throws<CohortScriptRebuildException>(() => RebuildWith(activator, Script(CreateLine, SetLine("a1"), LinkLine), $"Unread {Guid.NewGuid():N}"));

        Assert.Multiple(() =>
        {
            Assert.That(e!.Message, Does.StartWith("Line 5 of the build script").And.Contain("A failure after the link was saved"),
                "The failing line should stay the main error.");
            Assert.That(e.Cleanup.Unverified, Has.Exactly(1).Contains($"the parts of cohort {e.Cleanup.Cohort.ID}"),
                "The clean-up should say it could not check the cohort's parts.");
            Assert.That(e.Message, Does.Contain("could not check"), "The message should say what the clean-up could not check.");
            Assert.That(activator.Published, Is.EqualTo(new[] { Describe(e.Cleanup.Cohort) }), "The build should refresh RDMP once.");
        });
    }

    [Test]
    public void AFailedRefreshAfterAnIncompleteCleanupKeepsTheOriginalError()
    {
        var activator = new RecordingActivator(_locator) { FailOnPublish = true };
        _repository.RefuseDelete = o => o is AggregateConfiguration;

        var e = Assert.Throws<CohortScriptRebuildException>(() => RebuildWith(activator, ScriptThatFailsAtItsLastLine, $"Twice {Guid.NewGuid():N}"));

        Assert.Multiple(() =>
        {
            Assert.That(e!.Message, Does.StartWith("Line 6 of the build script"), "The failing line should stay the main error.");
            Assert.That(e.Message, Does.Contain("Still there").And.Contain("RDMP could not refresh its list of objects either"),
                "The message should name the leftovers and then the failed refresh.");
            Assert.That(e.Cleanup.Leftovers, Is.Not.Empty, "The leftovers should still be reported.");
            Assert.That(e.Reason, Is.EqualTo(CohortScriptRefusal.LineRefused), "The reason should be the failing line's.");
        });
    }

    // ---- the wrapper forwards the rest

    [Test]
    public void TheWrapperForwardsQuestionsAndMessages()
    {
        var asking = new PromptRefusingActivator(_locator) { AnswerYesNo = true };
        var recording = new RecordingActivator(_locator);

        var answered = new NoPublishActivator(asking).YesNo(new DialogArgs { TaskDescription = "Carry on?" }, out var chosen);
        new NoPublishActivator(recording).Show("Title", "A message");

        Assert.Multiple(() =>
        {
            Assert.That(answered && chosen, Is.True, "The wrapper should return the wrapped activator's answer.");
            Assert.That(asking.Prompts, Is.EqualTo(new[] { "YesNo: Carry on?" }), "The question should reach the wrapped activator.");
            Assert.That(recording.Events, Is.EqualTo(new[] { "show A message" }), "The message should reach the wrapped activator.");
        });
    }

    [Test]
    public void TheWrapperForwardsProperties()
    {
        var real = new RecordingActivator(_locator) { InteractiveDeletes = true, HardRefresh = false };
        var wrapper = new NoPublishActivator(real);

        foreach (var property in typeof(IBasicActivateItems).GetProperties())
            Assert.That(property.GetValue(wrapper), Is.SameAs(property.GetValue(real)).Or.EqualTo(property.GetValue(real)),
                $"The wrapper should return the wrapped activator's {property.Name}.");

        wrapper.InteractiveDeletes = false;
        wrapper.HardRefresh = true;
        Assert.Multiple(() =>
        {
            Assert.That(real.InteractiveDeletes, Is.False, "Setting InteractiveDeletes on the wrapper should change the wrapped activator.");
            Assert.That(real.HardRefresh, Is.True, "Setting HardRefresh on the wrapper should change the wrapped activator.");
        });
    }

    // ---- helpers

    private CohortIdentificationConfiguration Cohort(string name) =>
        _repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single();

    private static string Describe(IMapsDirectlyToDatabaseTable o) => $"{o.GetType().Name} {o.ID}";

    private Dictionary<string, int> Counts() => new()
    {
        ["cohorts"] = _repository.GetAllObjects<CohortIdentificationConfiguration>().Length,
        ["containers"] = _repository.GetAllObjects<CohortAggregateContainer>().Length,
        ["sets"] = _repository.GetAllObjects<AggregateConfiguration>().Length,
        ["columns"] = _repository.GetAllObjects<AggregateDimension>().Length,
        ["filter containers"] = _repository.GetAllObjects<AggregateFilterContainer>().Length,
        ["project links"] = _repository.GetAllObjects<ProjectCohortIdentificationConfigurationAssociation>().Length
    };

    // the setting is saved for the user, so it is put back whatever happens
    private static T WithSelectiveRefresh<T>(bool on, Func<T> action)
    {
        var was = UserSettings.SelectiveRefresh;
        UserSettings.SelectiveRefresh = on;
        try
        {
            return action();
        }
        finally
        {
            UserSettings.SelectiveRefresh = was;
        }
    }

    // the setting is saved for the user, so it is put back whatever happens
    internal static T WithAlwaysJoinEverything<T>(Func<T> action)
    {
        var was = UserSettings.AlwaysJoinEverything;
        UserSettings.AlwaysJoinEverything = true;
        try
        {
            return action();
        }
        finally
        {
            UserSettings.AlwaysJoinEverything = was;
        }
    }

    private static string Script(params string[] lines) =>
        "cohort-script-format: \"1\"\nCommands:\n" + string.Concat(lines.Select(l => $"  - {YamlText.DoubleQuoted(l)}\n"));

    internal static void RebuildWith(IBasicActivateItems activator, string text, string name)
    {
        var file = new FileInfo(Path.Join(Path.GetTempPath(), $"rdmp-cohortscript-{Guid.NewGuid()}.yaml"));
        File.WriteAllText(file.FullName, text);
        try
        {
            new ExecuteCommandBuildCohortFromScript(activator, file, name).Execute();
        }
        finally
        {
            file.Delete();
        }
    }

    /// <summary>A container whose check for existence always fails, as when the platform cannot be read.</summary>
    private sealed class UncheckableContainer : CohortAggregateContainer
    {
        public UncheckableContainer(ICatalogueRepository repository) : base(repository, SetOperation.UNION)
        {
        }

        public override bool Exists() => throw new InvalidOperationException("The platform could not be read.");
    }
}

/// <summary>
/// The console activator, with each refresh, emphasis, activation and message recorded in order, and optional
/// failures of the refresh and the emphasis.
/// </summary>
internal sealed class RecordingActivator : ConsoleInputManager
{
    public List<string> Published { get; } = new();
    public List<string> Events { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool FailOnPublish { get; set; }
    public bool FailOnEmphasis { get; set; }

    public RecordingActivator(IRDMPPlatformRepositoryServiceLocator locator)
        : base(locator, ThrowImmediatelyCheckNotifier.Quiet)
    {
        DisallowInput = true;
    }

    public override void Publish(IMapsDirectlyToDatabaseTable databaseEntity)
    {
        var what = $"{databaseEntity.GetType().Name} {databaseEntity.ID}";
        Published.Add(what);
        Events.Add($"publish {what}");
        if (FailOnPublish)
            throw new InvalidOperationException("RDMP's list of objects could not be loaded.");
        base.Publish(databaseEntity);
    }

    public override void RequestItemEmphasis(object sender, EmphasiseRequest emphasiseRequest)
    {
        var o = emphasiseRequest.ObjectToEmphasise;
        Events.Add(o is IMapsDirectlyToDatabaseTable m ? $"emphasise {m.GetType().Name} {m.ID}" : $"emphasise {o}");
        if (FailOnEmphasis)
            throw new InvalidOperationException("The tree could not be shown.");
        base.RequestItemEmphasis(sender, emphasiseRequest);
    }

    protected override void ActivateImpl(object o) => Events.Add($"activate {o}");

    // only the first words of a message, so a test can check the order of events without the whole text
    public override void Show(string title, string message) =>
        Events.Add($"show {string.Join(' ', message.Split(' ').Take(3))}");

    public override void ShowWarning(string message) => Warnings.Add(message);
}

/// <summary>
/// A memory repository that can fail on chosen objects: after saving one, or when asked to delete one. Each
/// failure is chosen by the test.
/// </summary>
internal sealed class FaultyRepository : MemoryDataExportRepository
{
    /// <summary>Called after each object is saved; when it returns an exception, the save throws it, once.</summary>
    public Func<IMapsDirectlyToDatabaseTable, Exception> FailAfterInsert { get; set; }

    /// <summary>When it returns true for an object, deleting that object throws.</summary>
    public Func<IMapsDirectlyToDatabaseTable, bool> RefuseDelete { get; set; }

    /// <summary>When true, removing a forced join throws.</summary>
    public bool FailToBreakForcedJoins { get; set; }

    /// <summary>When true, a removal that throws has removed the forced join first.</summary>
    public bool BreakForcedJoinsBeforeFailing { get; set; }

    /// <summary>When true, removing a forced join that is not saved throws, as the database does when no row is deleted.</summary>
    public bool RefuseToRemoveAMissingForcedJoin { get; set; }

    public override void BreakLinkBetween(AggregateConfiguration configuration, ITableInfo tableInfo)
    {
        if (RefuseToRemoveAMissingForcedJoin && !(ForcedJoins.TryGetValue(configuration, out var saved) && saved.Contains(tableInfo)))
            throw new InvalidOperationException("No forced join was removed, because none is saved.");
        if (!FailToBreakForcedJoins || BreakForcedJoinsBeforeFailing)
            base.BreakLinkBetween(configuration, tableInfo);
        if (FailToBreakForcedJoins)
            throw new InvalidOperationException("The test fails the removal of this forced join.");
    }

    public override void InsertAndHydrate<T>(T toCreate, Dictionary<string, object> constructorParameters)
    {
        base.InsertAndHydrate(toCreate, constructorParameters);
        if (FailAfterInsert?.Invoke(toCreate) is { } e)
        {
            FailAfterInsert = null;
            throw e;
        }
    }

    public override void DeleteFromDatabase(IMapsDirectlyToDatabaseTable oTableWrapperObject)
    {
        if (RefuseDelete?.Invoke(oTableWrapperObject) == true)
            throw new InvalidOperationException("The test refuses to delete this object.");
        base.DeleteFromDatabase(oTableWrapperObject);
    }
}

/// <summary>A repository locator whose next read of the catalogue repository can be made to fail, once.</summary>
internal sealed class FaultyLocator : IRDMPPlatformRepositoryServiceLocator
{
    private readonly IRDMPPlatformRepositoryServiceLocator _real;

    public bool FailNextCatalogueRead { get; set; }

    public FaultyLocator(IRDMPPlatformRepositoryServiceLocator real)
    {
        _real = real;
    }

    public ICatalogueRepository CatalogueRepository
    {
        get
        {
            if (!FailNextCatalogueRead)
                return _real.CatalogueRepository;
            FailNextCatalogueRead = false;
            throw new InvalidOperationException("The platform could not be read.");
        }
    }

    public IDataExportRepository DataExportRepository => _real.DataExportRepository;

    public IMapsDirectlyToDatabaseTable GetArbitraryDatabaseObject(string repositoryTypeName, string databaseObjectTypeName, int objectID) =>
        _real.GetArbitraryDatabaseObject(repositoryTypeName, databaseObjectTypeName, objectID);

    public bool ArbitraryDatabaseObjectExists(string repositoryTypeName, string databaseObjectTypeName, int objectID) =>
        _real.ArbitraryDatabaseObjectExists(repositoryTypeName, databaseObjectTypeName, objectID);

    public IMapsDirectlyToDatabaseTable GetObjectByID<T>(int value) where T : IMapsDirectlyToDatabaseTable => _real.GetObjectByID<T>(value);
    public IMapsDirectlyToDatabaseTable GetObjectByID(Type t, int value) => _real.GetObjectByID(t, value);
    public IEnumerable<IRepository> GetAllRepositories() => _real.GetAllRepositories();
}
