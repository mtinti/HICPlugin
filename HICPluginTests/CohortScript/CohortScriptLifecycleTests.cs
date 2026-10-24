// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using RdmpCohortScript;

namespace HICPluginTests;

/// <summary>
/// The part of the lifecycle tests that runs everywhere, GitHub included: one cohort through a rebuild and the
/// fixed point, and one deliberate fault that the checks must catch.
/// </summary>
public class CohortScriptLifecycleSmokeTests : CohortScriptLifecycleBase
{
    [Test]
    public void ACohortUnderExceptKeepsItsPatientsAndStructureThroughARebuildAndTheFixedPoint()
    {
        StartWithACleanDatabase();
        var (cic, _) = SetOperations.Build(this, "Smoke, A EXCEPT B", SetOperations.AExceptB, reverseInsertion: true);

        var result = RoundTrip(cic.ID, Ps(1, 2, 5, 7), allGenerations: false);

        Assert.That(result.Problems, Is.Empty, string.Join(Environment.NewLine, result.Problems));
    }

    [Test]
    public void SwappingTheChildrenOfExceptIsCaughtByTheRowCheck()
    {
        StartWithACleanDatabase();
        var (cic, _) = SetOperations.Build(this, "Smoke fault, A EXCEPT B", SetOperations.AExceptB);
        var reference = CohortSnapshot.Read(FreshLocator(), cic.ID);

        SwapTheFirstTwoChildren(cic.ID);

        var problems = Verify("the changed cohort", cic.ID, cic.Name, Ps(1, 2, 5, 7), reference);
        Assert.Multiple(() =>
        {
            Assert.That(problems, Has.Some.StartsWith("rows:"), "Swapping the children of EXCEPT changes the patients, so the row check should fail.");
            Assert.That(problems, Has.Some.StartsWith("snapshot:"), "The saved order changed, so the snapshot check should fail.");
        });
    }
}

/// <summary>
/// Cohorts built from fake patient data with answers worked out by hand, taken through the whole round trip: B0,
/// export, rebuild B1, export again, rebuild B2, and two more rebuilds of the first export. Each generation is
/// reopened through fresh repositories and checked for its rows and its saved structure. Also: edits that are
/// saved and reverted, the export folder, one failed rebuild, and deliberate faults that the checks must catch.
/// It takes minutes, so it runs only when asked for: <c>dotnet test --filter TestCategory=Lifecycle</c>.
/// </summary>
[Explicit("Takes minutes. Run it with --filter TestCategory=Lifecycle.")]
[Category("Lifecycle")]
public class CohortScriptLifecycleTests : CohortScriptLifecycleBase
{
    // ---- F1, set operations

    private static IEnumerable<TestCaseData> SetOperationCases()
    {
        static TestCaseData Case(string name, SetOperations.Expression e, int[] expected, bool reverse = false) =>
            new TestCaseData(e, expected, reverse).SetName($"SetOperationsKeepTheirPatientsThroughTwoGenerations({name})");

        var o = SetOperations.Leaves;
        yield return Case("A UNION B", new(SetOperation.UNION, o.A, o.B), Range(1, 10));
        yield return Case("A INTERSECT B", new(SetOperation.INTERSECT, o.A, o.B), new[] { 3, 4, 6, 8 });
        yield return Case("A EXCEPT B", SetOperations.AExceptB, new[] { 1, 2, 5, 7 }, reverse: true);
        yield return Case("B EXCEPT A", new(SetOperation.EXCEPT, o.B, o.A), new[] { 9, 10 });
        yield return Case("(A INTERSECT B) EXCEPT C",
            new(SetOperation.EXCEPT, new SetOperations.Expression(SetOperation.INTERSECT, o.A, o.B), o.C), new[] { 3, 6 });
        yield return Case("(A EXCEPT B) EXCEPT C",
            new(SetOperation.EXCEPT, new SetOperations.Expression(SetOperation.EXCEPT, o.A, o.B), o.C), new[] { 1, 5, 7 }, reverse: true);
        yield return Case("A EXCEPT (B EXCEPT C)",
            new(SetOperation.EXCEPT, o.A, new SetOperations.Expression(SetOperation.EXCEPT, o.B, o.C)), new[] { 1, 2, 4, 5, 7, 8 });
        yield return Case("A EXCEPT A", new(SetOperation.EXCEPT, o.A, o.A), Array.Empty<int>());
        yield return Case("A UNION A", new(SetOperation.UNION, o.A, o.A), Range(1, 8));
        yield return Case("A UNION NONE", new(SetOperation.UNION, o.A, o.None), Range(1, 8));
        yield return Case("ALL EXCEPT A", new(SetOperation.EXCEPT, o.All, o.A), Range(9, 16), reverse: true);
        yield return Case("A INTERSECT ALL", new(SetOperation.INTERSECT, o.A, o.All), Range(1, 8));
    }

    [TestCaseSource(nameof(SetOperationCases))]
    public void SetOperationsKeepTheirPatientsThroughTwoGenerations(SetOperations.Expression expression, int[] expected,
        bool reverseInsertion)
    {
        StartWithACleanDatabase();
        var (cic, _) = SetOperations.Build(this, $"F1 {Guid.NewGuid():N}"[..12], expression, reverseInsertion);

        AssertRoundTrip(cic.ID, Ps(expected));
    }

    [Test]
    public void AnAuthoredScriptWithNestedSetOperationsKeepsItsPatientsThroughTwoGenerations()
    {
        StartWithACleanDatabase();
        var t = SetOperations.Tables(this);
        string Add(string container, ICatalogue cata, string handle) =>
            $"AddCatalogueToCohortSet CohortAggregateContainer:{container} Catalogue:{cata.ID} \"{cata.Name}\" \"chi\" false => {handle} {handle}.chi";

        var id = BuildAuthored("F1 authored",
            "CreateNewCohortIdentificationConfiguration \"F1 authored\" => $cic1 $c1 $c2 $c3",
            "Set CohortAggregateContainer:$c1 Operation EXCEPT",
            "Set CohortAggregateContainer:$c2 Operation INTERSECT",
            Add("$c2", t.A, "$a1"),
            Add("$c2", t.B, "$a2"),
            "Set AggregateConfiguration:$a1 Order 0",
            "Set AggregateConfiguration:$a2 Order 1",
            "Set CohortAggregateContainer:$c3 Operation UNION",
            Add("$c3", t.C, "$a3"),
            "Set CohortAggregateContainer:$c2 Order 0",
            "Set CohortAggregateContainer:$c3 Order 1");

        // (A INTERSECT B) EXCEPT C
        AssertRoundTrip(id, Ps(3, 6));
    }

    // ---- F2, count columns

    private static IEnumerable<TestCaseData> CountColumnCases()
    {
        yield return new TestCaseData(SetOperation.EXCEPT, new[] { "P01|2", "P03|3", "P06|1" })
            .SetName("CountColumnsMakeSetOperationsCompareWholeRows(EXCEPT)");
        yield return new TestCaseData(SetOperation.INTERSECT, new[] { "P02|1", "P04|2" })
            .SetName("CountColumnsMakeSetOperationsCompareWholeRows(INTERSECT)");
        yield return new TestCaseData(SetOperation.UNION,
                new[] { "P01|1", "P01|2", "P02|1", "P03|2", "P03|3", "P04|2", "P05|1", "P06|1" })
            .SetName("CountColumnsMakeSetOperationsCompareWholeRows(UNION)");
    }

    // visits (P01 2, P02 1, P03 3, P04 2, P06 1) and calls (P01 1, P02 1, P03 2, P04 2, P05 1), each as (patient, count)
    [TestCaseSource(nameof(CountColumnCases))]
    public void CountColumnsMakeSetOperationsCompareWholeRows(SetOperation operation, string[] expected)
    {
        StartWithACleanDatabase();
        var cic = CountColumns(operation, countOnCalls: true);

        AssertRoundTrip(cic.ID, expected);
    }

    // RDMP 9.3.0 writes one column for the plain set and two for the count set. SQL Server refuses such a UNION
    // with error 205 (the queries of a UNION, INTERSECT or EXCEPT return different numbers of columns).
    [Test]
    public void AContainerMixingACountSetAndAPlainSetGivesAQueryThatDoesNotRun()
    {
        StartWithACleanDatabase();
        var cic = CountColumns(SetOperation.UNION, countOnCalls: false);

        Assert.That(() => Rows(Reopen(cic.ID)), Throws.Exception.With.Message.Contains("equal number of expressions"),
            "A set with a count column and a set without one return different numbers of columns, so the query should fail.");
    }

    private CohortIdentificationConfiguration CountColumns(SetOperation operation, bool countOnCalls)
    {
        var visits = Patients(Unique("visits"), new[] { 1, 1, 2, 3, 3, 3, 4, 4, 6 });
        var calls = Patients(Unique("calls"), new[] { 1, 2, 3, 3, 4, 4, 5 });
        var cic = new CohortIdentificationConfiguration(CatalogueRepository, $"F2 {operation} {Guid.NewGuid():N}"[..20]);
        cic.CreateRootContainerIfNotExists();
        var root = cic.RootCohortAggregateContainer;
        root.Operation = operation;
        root.SaveToDatabase();
        var sets = new[] { (visits, 0, true), (calls, 1, countOnCalls) }.Select(s =>
        {
            var (cata, order, count) = s;
            var set = AddSet(cic, root, cata, order);
            set.CountSQL = count ? "count(*)" : null;
            set.SaveToDatabase();
            return set;
        }).ToArray();
        SetOrders(sets);

        return cic;
    }

    // ---- F3, an index admission and a window of @window days

    [Test]
    public void APrescriptionWindowAfterAnAdmissionKeepsItsPatientsThroughTwoGenerations()
    {
        StartWithACleanDatabase();
        var f3 = AdmissionWindow.Build(this, "F3");

        AssertRoundTrip(f3.Cohort.ID, Ps(2, 3, 5));
    }

    [Test]
    public void AnAuthoredPrescriptionWindowKeepsItsPatientsThroughTwoGenerations()
    {
        StartWithACleanDatabase();
        var (admissions, prescribing) = AdmissionWindow.Tables(this);

        var id = BuildAuthored("F3 authored",
            "CreateNewCohortIdentificationConfiguration \"F3 authored\" => $cic1 $c1 $c2 $c3",
            $"AddPatientIndexTable CohortIdentificationConfiguration:$cic1 Catalogue:{admissions.ID} \"{admissions.Name}\" \"chi\" \"admission_date\" false => $a1 $j1 $a1.chi $a1.admission_date",
            $"AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:{prescribing.ID} \"{prescribing.Name}\" \"chi\" false => $a2 $a2.chi",
            "UsePatientIndexTable AggregateConfiguration:$a2 JoinableCohortAggregateConfiguration:$j1 Inner",
            "AddCohortParameter AggregateConfiguration:$a2 \"@window\" \"DECLARE @window AS int\" \"90\" null",
            "AddNewFilterContainer AggregateConfiguration:$a2 => $fc1",
            "AddCohortFilter AggregateFilterContainer:$fc1 \"Within the window\" \"prescribed_date BETWEEN ix{$j1}.admission_date AND DATEADD(day, @window, ix{$j1}.admission_date)\" => $f1",
            "AddCohortFilter AggregateFilterContainer:$fc1 \"Drug A\" \"drug = 'A'\" => $f2",
            "Delete CohortAggregateContainer:$c3");

        AssertRoundTrip(id, Ps(2, 3, 5));
    }

    [Test]
    public void ChangingTheWindowAndChangingItBackGivesTheExpectedPatientsEachTime()
    {
        StartWithACleanDatabase();
        var f3 = AdmissionWindow.Build(this, "F3 edit");
        // another cohort with the same name must not receive the change
        MakeBystanders(f3.Cohort.Name);
        var before = Inventory.Take(FreshLocator());

        SetWindow(f3.Window.ID, "30");
        var shorter = Verify("the 30-day window", f3.Cohort.ID, f3.Cohort.Name, Ps(2), null);
        SetWindow(f3.Window.ID, "90");
        var back = Verify("the window set back to 90 days", f3.Cohort.ID, f3.Cohort.Name, Ps(2, 3, 5), null);

        var problems = shorter.Concat(back).Concat(Inventory.Changed(before, Inventory.Take(FreshLocator()))).ToList();
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    private void SetWindow(int parameterId, string value)
    {
        var parameter = FreshLocator().CatalogueRepository.GetObjectByID<AnyTableSqlParameter>(parameterId);
        parameter.Value = value;
        parameter.SaveToDatabase();
    }

    // ---- F4, the saved features in one cohort

    [Test]
    public void ACohortWithEverySavedFeatureKeepsItsPatientsAndStructureThroughTwoGenerations()
    {
        StartWithACleanDatabase();
        var f4 = SavedFeatures.Build(this);

        AssertRoundTrip(f4.Cohort.ID, Ps(1, 6), snapshot => f4.ExpectedStructure(FreshLocator()));
    }

    [Test]
    public void SavingAnUnchangedCohortThreeTimesChangesNothing()
    {
        StartWithACleanDatabase();
        var f4 = SavedFeatures.Build(this);
        var reference = CohortSnapshot.Read(FreshLocator(), f4.Cohort.ID);
        var before = Inventory.Take(FreshLocator());

        for (var i = 0; i < 3; i++)
            SavedFeatures.SaveEverything(Reopen(f4.Cohort.ID));

        var after = Inventory.Take(FreshLocator());
        var problems = Verify("the saved cohort", f4.Cohort.ID, f4.Cohort.Name, Ps(1, 6), reference)
            .Concat(Inventory.Changed(before, after))
            .Concat(Inventory.Added(before, after).Select(k => $"{k} was added."))
            .ToList();
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EnablingADisabledFilterAndDisablingItAgainGivesTheExpectedPatientsEachTime()
    {
        StartWithACleanDatabase();
        var f4 = SavedFeatures.Build(this);
        MakeBystanders(f4.Cohort.Name);
        var before = Inventory.Take(FreshLocator());

        SetDisabled(f4.DisabledFilter.ID, false);
        var enabled = Verify("the enabled filter", f4.Cohort.ID, f4.Cohort.Name, Ps(1, 2, 6), null);
        SetDisabled(f4.DisabledFilter.ID, true);
        var disabled = Verify("the filter disabled again", f4.Cohort.ID, f4.Cohort.Name, Ps(1, 6), null);

        var problems = enabled.Concat(disabled).Concat(Inventory.Changed(before, Inventory.Take(FreshLocator()))).ToList();
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    private void SetDisabled(int filterId, bool disabled)
    {
        var filter = FreshLocator().CatalogueRepository.GetObjectByID<AggregateFilter>(filterId);
        filter.IsDisabled = disabled;
        filter.SaveToDatabase();
    }

    // ---- the export folder

    [Test]
    public void ExportingAgainKeepsTheRequirementAndAnotherCohortOfTheSameNameIsRefusedWithoutChangingAFile()
    {
        StartWithACleanDatabase();
        var (cic, _) = SetOperations.Build(this, "F1 folder", SetOperations.AExceptB);
        var folder = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"rdmp-lifecycle-{Guid.NewGuid():N}"));
        try
        {
            var script = Export(cic.ID, folder.FullName);
            var requirement = Path.Join(script.DirectoryName, CohortExportFiles.RequirementFile);
            File.WriteAllText(requirement, "Patients on drug A who are not in B.\nWritten by hand.\n");
            var written = File.ReadAllBytes(requirement);

            Export(cic.ID, folder.FullName);
            Export(cic.ID, folder.FullName);
            var files = Hashes(script.Directory);

            var other = new CohortIdentificationConfiguration(CatalogueRepository, cic.Name);
            other.CreateRootContainerIfNotExists();
            var refused = Assert.Throws<CohortScriptExportException>(() => Export(other.ID, folder.FullName));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(requirement), Is.EqualTo(written), "Exporting again should keep the requirement byte for byte.");
                Assert.That(refused!.Reason, Is.EqualTo(CohortScriptRefusal.FolderHoldsAnotherExport),
                    "The export of another cohort with the same name should be refused.");
                Assert.That(Hashes(script.Directory), Is.EqualTo(files), "The refused export should change no file in the folder.");
            });
        }
        finally
        {
            folder.Delete(true);
        }
    }

    private static Dictionary<string, string> Hashes(DirectoryInfo dir) => dir.GetFiles().ToDictionary(f => f.Name,
        f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.FullName))));

    // ---- one failed rebuild

    [Test]
    public void ARebuildThatFailsAtItsLastLineRemovesWhatItMadeAndLeavesEverythingElse()
    {
        StartWithACleanDatabase();
        var f4 = SavedFeatures.Build(this);
        MakeBystanders(f4.Cohort.Name);
        var folder = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"rdmp-lifecycle-{Guid.NewGuid():N}"));
        try
        {
            var script = Export(f4.Cohort.ID, folder.FullName);
            var text = File.ReadAllText(script.FullName);
            // linking the cohort to a project twice fails only when the line runs, after every other line has run
            var link = text.Split('\n').First(l => l.Contains("LinkCohortToProject", StringComparison.Ordinal));
            var failing = new FileInfo(Path.Join(folder.FullName, "failing.yaml"));
            File.WriteAllText(failing.FullName, text.TrimEnd('\n') + "\n" + link + "\n");

            var before = Inventory.Take(FreshLocator());
            Assert.Throws<CohortScriptRebuildException>(() => Rebuild(failing, "F4 failing"));
            var after = Inventory.Take(FreshLocator());

            var problems = Inventory.Changed(before, after)
                .Concat(Inventory.Added(before, after).Select(k => $"{k} was left behind."))
                .ToList();
            var good = Rebuild(script, "F4 after the failure");
            problems.AddRange(Verify("the rebuild after the failure", good, "F4 after the failure", Ps(1, 6),
                CohortSnapshot.Read(FreshLocator(), f4.Cohort.ID)));
            Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    // ---- deliberate faults: each must be caught by the check named in the assertion

    [Test]
    public void AFilterChangedToGiveAsManyPatientsButDifferentOnesIsCaughtByTheRowCheck()
    {
        StartWithACleanDatabase();
        var f4 = SavedFeatures.Build(this);
        var reference = CohortSnapshot.Read(FreshLocator(), f4.Cohort.ID);

        var filter = FreshLocator().CatalogueRepository.GetObjectByID<AggregateFilter>(f4.DeathFilter.ID);
        filter.WhereSQL = filter.WhereSQL.Replace(">=", "<");
        filter.SaveToDatabase();

        var problems = Verify("the changed cohort", f4.Cohort.ID, f4.Cohort.Name, Ps(1, 6), reference);
        Assert.Multiple(() =>
        {
            Assert.That(Rows(Reopen(f4.Cohort.ID)), Has.Length.EqualTo(2), "The changed filter should give the same number of patients.");
            Assert.That(problems, Has.Some.StartsWith("rows:"), "The patients differ, so the row check should fail.");
        });
    }

    [Test]
    public void AChangedParameterValueIsCaughtByTheRowAndSnapshotChecks()
    {
        StartWithACleanDatabase();
        var f3 = AdmissionWindow.Build(this, "F3 fault");
        var reference = CohortSnapshot.Read(FreshLocator(), f3.Cohort.ID);

        SetWindow(f3.Window.ID, "30");

        var problems = Verify("the changed cohort", f3.Cohort.ID, f3.Cohort.Name, Ps(2, 3, 5), reference);
        Assert.Multiple(() =>
        {
            Assert.That(problems, Has.Some.StartsWith("rows:"), "A 30-day window drops two patients, so the row check should fail.");
            Assert.That(problems, Has.Some.StartsWith("snapshot:"), "The saved value changed, so the snapshot check should fail.");
        });
    }

    [Test]
    public void AChangedCountColumnIsCaughtByTheRowAndSnapshotChecks()
    {
        StartWithACleanDatabase();
        var cic = CountColumns(SetOperation.EXCEPT, countOnCalls: true);
        var reference = CohortSnapshot.Read(FreshLocator(), cic.ID);

        var visits = Reopen(cic.ID).RootCohortAggregateContainer.GetAggregateConfigurations().OrderBy(a => a.Order).First();
        visits.CountSQL = "count(*) + 1";
        visits.SaveToDatabase();

        var problems = Verify("the changed cohort", cic.ID, cic.Name, new[] { "P01|2", "P03|3", "P06|1" }, reference);
        Assert.Multiple(() =>
        {
            Assert.That(problems, Has.Some.StartsWith("rows:"), "Every count is one higher, so the row check should fail.");
            Assert.That(problems, Has.Some.StartsWith("snapshot:"), "The count column changed, so the snapshot check should fail.");
        });
    }

    [Test]
    public void ARemovedProjectLinkIsCaughtByTheSnapshotCheckAlone()
    {
        StartWithACleanDatabase();
        var f4 = SavedFeatures.Build(this);
        var reference = CohortSnapshot.Read(FreshLocator(), f4.Cohort.ID);

        FreshLocator().DataExportRepository
            .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", f4.Cohort.ID)
            .First().DeleteInDatabase();

        var problems = Verify("the changed cohort", f4.Cohort.ID, f4.Cohort.Name, Ps(1, 6), reference);
        Assert.Multiple(() =>
        {
            Assert.That(problems, Has.None.StartsWith("rows:"), "A project link does not change the patients.");
            Assert.That(problems, Has.Some.StartsWith("snapshot:"), "The project link is gone, so the snapshot check should fail.");
        });
    }

    [Test]
    public void AnEditThatWasNotSavedIsCaughtWhenTheCohortIsReopened()
    {
        StartWithACleanDatabase();
        var f3 = AdmissionWindow.Build(this, "F3 unsaved");

        // the edit stays in memory: the check must read what was saved, not this object
        var parameter = FreshLocator().CatalogueRepository.GetObjectByID<AnyTableSqlParameter>(f3.Window.ID);
        parameter.Value = "30";

        var problems = Verify("the reopened cohort", f3.Cohort.ID, f3.Cohort.Name, Ps(2), null);
        Assert.That(problems, Has.Some.StartsWith("rows:"),
            "The 30-day window was never saved, so the reopened cohort should still return three patients.");
    }

    // ---- helpers

    private void AssertRoundTrip(int cohortId, string[] expectedRows, Func<string, List<string>> checkB0Structure = null)
    {
        var result = RoundTrip(cohortId, expectedRows, checkB0Structure);
        Assert.That(result.Problems, Is.Empty, string.Join(Environment.NewLine, result.Problems));
    }

    private static int[] Range(int from, int to) => Enumerable.Range(from, to - from + 1).ToArray();
}
