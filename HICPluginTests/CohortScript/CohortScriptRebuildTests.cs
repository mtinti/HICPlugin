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
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.ReusableLibraryCode.Checks;
using RdmpCohortScript;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// The rebuild of a format 1 script: the checks it runs before building anything, and how it stops at a line that
/// fails. Each check has a script that breaks only that rule.
/// </summary>
internal class CohortScriptRebuildTests : UnitTests
{
    private Catalogue _catalogue;
    private ExtractionInformation _identifier;
    private string _column;

    [SetUp]
    public void MakeCatalogue()
    {
        var throwaway = WhenIHaveA(Repository, out _, out var first);
        _catalogue = throwaway.Catalogue;
        _catalogue.Name = $"Rebuild catalogue {Guid.NewGuid():N}";
        _catalogue.SaveToDatabase();
        first.IsExtractionIdentifier = true;
        first.SaveToDatabase();
        _identifier = first;
        _column = first.GetRuntimeName();
        throwaway.DeleteInDatabase();
    }

    private string CreateLine => "CreateNewCohortIdentificationConfiguration \"Rebuild test\" => $cic1 $c1 $c2 $c3";

    private string SetLine(string catalogueName = null, string id = null) =>
        $"AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:{id ?? _catalogue.ID.ToString()} {catalogueName ?? $"\"{_catalogue.Name}\""} \"{_column}\" false => $a1 $a1.{_column}";

    // ---- a script that is right

    [Test]
    public void AGoodScriptBuildsTheCohortAndBindsEachHandleToWhatItsLineMade()
    {
        var name = $"Built {Guid.NewGuid():N}";
        Rebuild(Script(CreateLine, SetLine(), "SetText CohortAggregateContainer:$c2 Name \"Inclusion\"", "Delete CohortAggregateContainer:$c3"), name);

        var cic = Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single();
        var children = cic.RootCohortAggregateContainer.GetSubContainers();
        Assert.Multiple(() =>
        {
            Assert.That(children.Select(c => c.Name), Is.EqualTo(new[] { "Inclusion" }),
                "The first automatic container should be renamed and the second deleted.");
            Assert.That(children.Single().GetAggregateConfigurations().Single().Catalogue.ID, Is.EqualTo(_catalogue.ID),
                "The set should be in the container its line named.");
        });
    }

    // ---- the file

    [Test]
    public void AScriptFromTheJuneExportIsRefusedWithAnExplanation()
    {
        var problems = Check("Commands:\n  - CreateNewCohortIdentificationConfiguration \"Old\" => $c1\n");

        Assert.That(problems.Select(p => p.Reason), Has.Member(CohortScriptRefusal.NotFormat1));
        Assert.That(problems.Single(p => p.Reason == CohortScriptRefusal.NotFormat1).Message, Does.Contain("June export"));
    }

    [Test]
    public void AValueThatIsNotDoubleQuotedIsRefused()
    {
        var problems = Check($"cohort-script-format: \"1\"\nCommands:\n  - {CreateLine}\n");

        Assert.That(problems.Select(p => p.Reason), Has.Member(CohortScriptRefusal.NotStrictYaml));
    }

    // ---- one test per check

    [TestCase("Frobnicate CohortAggregateContainer:$c1", CohortScriptRefusal.UnknownCommand, TestName = "AnUnknownCommandIsRefused")]
    [TestCase("Set CohortAggregateContainer:$c1 Operation", CohortScriptRefusal.WrongArgumentCount, TestName = "AWrongNumberOfArgumentsIsRefused")]
    [TestCase("AddCohortSubContainer CohortAggregateContainer:$c1", CohortScriptRefusal.WrongHandleCount, TestName = "AMissingHandleIsRefused")]
    [TestCase("AddCohortSubContainer CohortAggregateContainer:$c1 => $a9", CohortScriptRefusal.BadHandle, TestName = "AHandleOfTheWrongRoleIsRefused")]
    [TestCase("AddCohortSubContainer CohortAggregateContainer:$c1 => $c2", CohortScriptRefusal.HandleCreatedTwice, TestName = "AHandleCreatedTwiceIsRefused")]
    [TestCase("Set AggregateConfiguration:$a9 Order \"0\"", CohortScriptRefusal.HandleUsedBeforeCreated, TestName = "AHandleUsedBeforeItIsCreatedIsRefused")]
    [TestCase("Set CohortAggregateContainer:12 Operation UNION", CohortScriptRefusal.IdInsteadOfHandle, TestName = "SetOnAnExistingObjectByIdIsRefused")]
    [TestCase("Delete CohortAggregateContainer:12", CohortScriptRefusal.IdInsteadOfHandle, TestName = "DeleteOfAnExistingObjectByIdIsRefused")]
    [TestCase("Set CohortAggregateContainer:$c1 ID \"7\"", CohortScriptRefusal.PropertyNotAllowed, TestName = "SetOnAPropertyTheFormatDoesNotAllowIsRefused")]
    [TestCase("CreateNewCohortIdentificationConfiguration \"Second\" => $cic2 $c4 $c5 $c6", CohortScriptRefusal.CreateLineNotFirst, TestName = "ASecondCreateLineIsRefused")]
    [TestCase("AddCohortFilter AggregateFilterContainer:$fc1 \"F\" \"x = ix{$j9}.y\" => $f1", CohortScriptRefusal.HandleUsedBeforeCreated, TestName = "AMarkerForAPatientIndexTableNotYetMadeIsRefused")]
    public void ALineThatBreaksARuleIsRefused(string line, CohortScriptRefusal expected)
    {
        var problems = Check(Script(CreateLine, line));

        Assert.That(problems.Select(p => p.Reason), Has.Member(expected), string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void AReferenceWithoutANameIsRefused()
    {
        Assert.That(Check(Script(CreateLine, SetLine("null"))).Select(p => p.Reason), Has.Member(CohortScriptRefusal.MissingName));
    }

    [Test]
    public void AReferenceToAnObjectThatDoesNotExistIsRefused()
    {
        Assert.That(Check(Script(CreateLine, SetLine(id: "987654"))).Select(p => p.Reason), Has.Member(CohortScriptRefusal.ReferenceNotFound));
    }

    [Test]
    public void AReferenceWhoseNameDiffersIsRefused()
    {
        Assert.That(Check(Script(CreateLine, SetLine("\"Another name\""))).Select(p => p.Reason), Has.Member(CohortScriptRefusal.NameMismatch));
    }

    [Test]
    public void ACohortNameAlreadyTakenIsRefused()
    {
        var taken = new CohortIdentificationConfiguration(Repository, $"Taken {Guid.NewGuid():N}");

        Assert.That(Check(Script(CreateLine), taken.Name).Select(p => p.Reason), Has.Member(CohortScriptRefusal.CohortNameTaken));
    }

    [Test]
    public void EveryProblemIsListedWithItsLineInTheFile()
    {
        // the header takes lines 1 and 2, so command n is on file line n + 2
        var problems = Check(Script(CreateLine, "Frobnicate x", SetLine("\"Another name\"")));

        Assert.That(problems.Select(p => (p.FileLine, p.Reason)), Is.SupersetOf(new[]
        {
            (4, CohortScriptRefusal.UnknownCommand), (5, CohortScriptRefusal.NameMismatch)
        }));
    }

    [Test]
    public void NothingIsBuiltWhenTheCheckFindsAProblem()
    {
        var name = $"Not built {Guid.NewGuid():N}";

        Assert.That(() => Rebuild(Script(CreateLine, "Frobnicate x"), name), Throws.TypeOf<CohortScriptProblemsException>());
        Assert.That(Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name), Is.Empty);
    }

    // ---- a line that fails while the rebuild runs

    [Test]
    public void ALineRdmpRefusesStopsTheRebuildAndNamesTheLine()
    {
        var project = WhenIHaveA<Project>();
        var link = $"LinkCohortToProject CohortIdentificationConfiguration:$cic1 Project:{project.ID} \"{project.Name}\"";
        var name = $"Stops {Guid.NewGuid():N}";

        var e = Assert.Throws<CohortScriptRebuildException>(() => Rebuild(Script(CreateLine, link, link), name));

        Assert.Multiple(() =>
        {
            Assert.That(e.Reason, Is.EqualTo(CohortScriptRefusal.LineRefused), "RDMP refuses the second link to the same project.");
            Assert.That(e.CommandRefusal, Is.EqualTo(CohortScriptRefusal.AlreadyLinked), "The command's own reason should be kept.");
            Assert.That(e.FileLine, Is.EqualTo(5), "The message should name the second link line.");
            Assert.That(e.Message, Does.Contain("Line 5").And.Contain("was removed"),
                "The message should name the line and say that the partly built cohort was removed.");
            Assert.That(Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name), Is.Empty,
                "The partly built cohort should be gone.");
        });
    }

    // ---- review findings: quoting, null text, markers outside SQL, malformed entries

    [Test]
    public void AQuotedTargetCannotReachAnExistingObject()
    {
        var existing = new CohortAggregateContainer(Repository, SetOperation.UNION) { Name = "Someone else's" };
        existing.SaveToDatabase();
        var name = $"Quoted {Guid.NewGuid():N}";
        var script = Script(CreateLine, $"SetText \"CohortAggregateContainer:{existing.ID}\" Name \"Changed\"");

        Assert.That(Check(script).Select(p => p.Reason), Has.Member(CohortScriptRefusal.WrongArgumentKind));
        Assert.That(() => Rebuild(script, name), Throws.TypeOf<CohortScriptProblemsException>());
        Assert.That(Repository.GetObjectByID<CohortAggregateContainer>(existing.ID).Name, Is.EqualTo("Someone else's"),
            "The existing container should not change.");
    }

    [TestCase(false, TestName = "SetOnSharedMetadataIsRefused(bare)")]
    [TestCase(true, TestName = "SetOnSharedMetadataIsRefused(quoted)")]
    public void SetOnSharedMetadataIsRefused(bool quoted)
    {
        var target = quoted ? $"\"Catalogue:{_catalogue.ID}\"" : $"Catalogue:{_catalogue.ID}";
        var script = Script(CreateLine, $"Set {target} Name \"Changed\"");

        Assert.That(() => Rebuild(script, $"Shared {Guid.NewGuid():N}"), Throws.TypeOf<CohortScriptProblemsException>()
            .With.Property(nameof(CohortScriptProblemsException.Problems)).Some
            .With.Property(nameof(CohortScriptProblem.Reason)).EqualTo(CohortScriptRefusal.WrongArgumentKind));
        Assert.That(Repository.GetObjectByID<Catalogue>(_catalogue.ID).Name, Does.StartWith("Rebuild catalogue"),
            "The catalogue should not change.");
    }

    [Test]
    public void TheWordNullAsTextAndNullAsAnSqlValueSurviveARoundTrip()
    {
        var original = new CohortIdentificationConfiguration(Repository, $"Null text {Guid.NewGuid():N}") { Description = "null" };
        original.SaveToDatabase();
        original.CreateRootContainerIfNotExists();
        new AnyTableSqlParameter(Repository, original, "DECLARE @x AS varchar(10)") { Value = "NULL" }.SaveToDatabase();

        var rebuilt = RoundTrip(original);

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt.Description, Is.EqualTo("null"), "A description that is the word null should stay that text.");
            Assert.That(rebuilt.GetAllParameters().Single().Value, Is.EqualTo("NULL"),
                "An SQL value NULL should stay NULL, not become no value.");
        });
    }

    [Test]
    public void MarkerLikeTextOutsideSqlSurvivesARoundTrip()
    {
        var original = new CohortIdentificationConfiguration(Repository, $"Marker text {Guid.NewGuid():N}")
        {
            Description = "Literal {$j999} in notes"
        };
        original.SaveToDatabase();
        original.CreateRootContainerIfNotExists();

        Assert.That(RoundTrip(original).Description, Is.EqualTo("Literal {$j999} in notes"),
            "Markers belong to SQL; other text keeps them as they are.");
    }

    [Test]
    public void AnEntryOfCommandsThatIsNotALineIsRefused()
    {
        var text = $"cohort-script-format: \"1\"\nCommands:\n  - {YamlText.DoubleQuoted(CreateLine)}\n" +
                   "  - command: \"Set CohortAggregateContainer:$c2 IsDisabled true\"\n";

        var problems = Check(text);

        Assert.That(problems.Select(p => (p.FileLine, p.Reason)), Has.Member((4, CohortScriptRefusal.NotALine)));
    }

    [Test]
    public void AnIdentifierColumnTheCatalogueDoesNotHaveIsRefusedBeforeTheBuild()
    {
        var line = $"AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"no_such_column\" false => $a1 $a1.no_such_column";

        Assert.That(Check(Script(CreateLine, line)).Select(p => p.Reason), Has.Member(CohortScriptRefusal.UnknownIdentifierColumn));
    }

    [Test]
    public void AProjectSpecificCatalogueNeedsALinkLineBeforeIt()
    {
        var project = WhenIHaveA<Project>();
        var dataset = new ExtractableDataSet(Repository, _catalogue);
        _ = new ExtractableDataSetProject(Repository, dataset, project);
        var link = $"LinkCohortToProject CohortIdentificationConfiguration:$cic1 Project:{project.ID} \"{project.Name}\"";

        Assert.Multiple(() =>
        {
            Assert.That(Check(Script(CreateLine, SetLine())).Select(p => p.Reason), Has.Member(CohortScriptRefusal.ProjectNotLinkedInScript));
            Assert.That(Check(Script(CreateLine, link, SetLine())).Select(p => p.Reason), Has.No.Member(CohortScriptRefusal.ProjectNotLinkedInScript));
        });
    }

    [Test]
    public void FiltersWithNamesOrSqlThatRdmpWouldMisreadSurviveARoundTrip()
    {
        var published = new ExtractionFilter(Repository, "Published", _identifier) { WhereSQL = "1 = 0" };
        published.SaveToDatabase();
        var original = new CohortIdentificationConfiguration(Repository, $"Odd filters {Guid.NewGuid():N}") { Description = "Catalogue:987654" };
        original.SaveToDatabase();
        original.CreateRootContainerIfNotExists();
        var set = new Rdmp.Core.Curation.Data.Aggregation.AggregateConfiguration(Repository, _catalogue, "S") { CountSQL = null };
        set.SaveToDatabase();
        _ = new Rdmp.Core.Curation.Data.Aggregation.AggregateDimension(Repository, _identifier, set);
        original.EnsureNamingConvention(set);
        original.RootCohortAggregateContainer.AddChild(set, 0);
        var fc = new Rdmp.Core.Curation.Data.Aggregation.AggregateFilterContainer(Repository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        var expected = new (string Name, string Sql)[]
        {
            ($"ExtractionFilter:{published.ID}", "1 = 1"), ("null", "NULL"), ("No SQL", null)
        };
        foreach (var (name, sql) in expected)
            new Rdmp.Core.Curation.Data.Aggregation.AggregateFilter(Repository, name, fc) { WhereSQL = sql, IsDisabled = sql == null }
                .SaveToDatabase();

        var saved = fc.GetFilters().Select(f => (f.Name, f.WhereSQL)).ToArray();
        var rebuilt = RoundTrip(original);
        var filters = rebuilt.RootCohortAggregateContainer.GetAggregateConfigurations().Single().RootFilterContainer.GetFilters();

        Assert.Multiple(() =>
        {
            Assert.That(saved.Take(3), Is.EqualTo(expected.Take(3)), "The first three filters should be saved as given.");
            Assert.That(filters.Select(f => (f.Name, f.WhereSQL)), Is.EquivalentTo(saved),
                "Every filter should come back with exactly its saved name and SQL, including none.");
            Assert.That(rebuilt.Description, Is.EqualTo("Catalogue:987654"), "A description that looks like a reference stays text.");
        });
    }

    [TestCase("null", TestName = "AFilterWithoutANameIsRefused(null)")]
    [TestCase("\"   \"", TestName = "AFilterWithoutANameIsRefused(spaces)")]
    public void AFilterWithoutANameIsRefused(string name)
    {
        var script = Script(CreateLine, SetLine(), "AddNewFilterContainer AggregateConfiguration:$a1 => $fc1",
            $"AddCohortFilter AggregateFilterContainer:$fc1 {name} \"1 = 1\" => $f1");

        var e = Assert.Throws<CohortScriptRebuildException>(() => Rebuild(script, $"No name {Guid.NewGuid():N}"));

        Assert.That(e.CommandRefusal, Is.EqualTo(CohortScriptRefusal.MissingArgument),
            "RDMP requires a filter name, so the command refuses a missing or blank one.");
    }

    [Test]
    public void AYamlAliasInCommandsIsRefused()
    {
        var text = "cohort-script-format: \"1\"\nexported-from:\n  cohort-name: &disable \"Set CohortAggregateContainer:$c2 IsDisabled true\"\n" +
                   $"Commands:\n  - {YamlText.DoubleQuoted(CreateLine)}\n  - *disable\n";

        Assert.That(Check(text).Select(p => (p.FileLine, p.Reason)), Has.Member((6, CohortScriptRefusal.NotALine)));
    }

    [Test]
    public void ABareNullWhereAPlainWordBelongsIsRefused()
    {
        Assert.That(Check(Script(CreateLine, "Set CohortAggregateContainer:$c2 IsDisabled null")).Select(p => p.Reason),
            Has.Member(CohortScriptRefusal.WrongArgumentKind));
    }

    // ---- step 4b: clean-up, the delete prompt, renamed objects

    [Test]
    public void AFailedRebuildLeavesNothingBehind()
    {
        var project = WhenIHaveA<Project>();
        var original = RichCohort(project);
        var script = new BuildScriptWriter(original, RepositoryLocator).Document();
        var link = $"LinkCohortToProject CohortIdentificationConfiguration:$cic{original.ID} Project:{project.ID} \"{project.Name}\"";
        script += $"  - {YamlText.DoubleQuoted(link)}\n";   // a second link to the same project, refused at the end
        var before = Counts();

        var e = Assert.Throws<CohortScriptRebuildException>(() => Rebuild(script, $"Fails at the end {Guid.NewGuid():N}"));

        Assert.Multiple(() =>
        {
            Assert.That(e.CommandRefusal, Is.EqualTo(CohortScriptRefusal.AlreadyLinked));
            Assert.That(e.Message, Does.Contain("was removed"));
            Assert.That(Counts(), Is.EqualTo(before), "Every object the rebuild made should be removed again.");
        });
    }

    [Test]
    public void TheCleanUpLeavesOtherCohortsAlone()
    {
        var project = WhenIHaveA<Project>();
        var bystander = RichCohort(project);
        var bystanderSets = bystander.RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively().Count;
        // a copy of the bystander that fails on its last line, a second link to the same project
        var script = new BuildScriptWriter(bystander, RepositoryLocator).Document() +
                     $"  - {YamlText.DoubleQuoted($"LinkCohortToProject CohortIdentificationConfiguration:$cic{bystander.ID} Project:{project.ID} \"{project.Name}\"")}\n";

        Assert.Throws<CohortScriptRebuildException>(() => Rebuild(script, $"Fails {Guid.NewGuid():N}"));

        Assert.Multiple(() =>
        {
            Assert.That(bystander.Exists(), Is.True, "The original cohort should not be touched.");
            Assert.That(bystander.RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively(), Has.Count.EqualTo(bystanderSets));
        });
    }

    [Test]
    public void AGuiRebuildAsksNothingAndRestoresTheDeleteSetting()
    {
        var activator = new PromptRefusingActivator(RepositoryLocator) { InteractiveDeletes = true };
        var script = Script(CreateLine, SetLine(), "Delete CohortAggregateContainer:$c3");

        RebuildWith(activator, script, $"GUI {Guid.NewGuid():N}");
        Assert.Throws<CohortScriptRebuildException>(() =>
            RebuildWith(activator, Script(CreateLine, "Delete CohortAggregateContainer:$c3", "Delete CohortAggregateContainer:$c3"),
                $"GUI fails {Guid.NewGuid():N}"));

        Assert.Multiple(() =>
        {
            Assert.That(activator.Prompts, Is.Empty, "The rebuild should not ask to confirm the deletes it makes.");
            Assert.That(activator.InteractiveDeletes, Is.True, "The delete setting should be restored, also after a failure.");
        });
    }

    [Test]
    public void ARenamedCatalogueIsRefusedUnlessTheOptionAcceptsIt()
    {
        var script = Script(CreateLine, SetLine("\"Its old name\""));
        var name = $"Renamed {Guid.NewGuid():N}";

        Assert.That(() => Rebuild(script, name), Throws.TypeOf<CohortScriptProblemsException>());

        RebuildWith(new ConsoleInputManager(RepositoryLocator, ThrowImmediatelyCheckNotifier.Quiet) { DisallowInput = true },
            script, name, acceptRenamed: true);
        Assert.That(Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name), Has.Length.EqualTo(1),
            "With the option, the cohort should be built from the renamed catalogue.");
    }

    [Test]
    public void ACreateLineThatFailsAfterSavingTheCohortLeavesNothingBehind()
    {
        var activator = new FailingActivator(RepositoryLocator) { FailOnPublishOf = typeof(CohortIdentificationConfiguration) };
        var name = $"Fails in create {Guid.NewGuid():N}";
        var before = Counts();

        var e = Assert.Throws<CohortScriptRebuildException>(() => RebuildWith(activator, Script(CreateLine, SetLine()), name));

        Assert.Multiple(() =>
        {
            Assert.That(e.Message, Does.Contain("was removed"), "The message should say the cohort RDMP saved was removed.");
            Assert.That(Counts(), Is.EqualTo(before), "The cohort and its three containers should be gone.");
        });
    }

    [Test]
    public void AFailedRebuildRemovesOnlyItsOwnCohortWhenAnotherSessionUsedTheSameName()
    {
        var project = WhenIHaveA<Project>();
        var name = $"Same name {Guid.NewGuid():N}";
        CohortIdentificationConfiguration other = null;
        var activator = new FailingActivator(RepositoryLocator)
        {
            // between this rebuild's check and its create line, another session makes a cohort with the same name
            WhenTheRebuildStarts = () =>
            {
                other = new CohortIdentificationConfiguration(Repository, name);
                other.CreateRootContainerIfNotExists();
            }
        };
        var link = $"LinkCohortToProject CohortIdentificationConfiguration:$cic1 Project:{project.ID} \"{project.Name}\"";

        Assert.Throws<CohortScriptRebuildException>(() => RebuildWith(activator, Script(CreateLine, link, link), name));

        var left = Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name);
        Assert.That(left.Select(c => c.ID), Is.EqualTo(new[] { other.ID }),
            "Only this rebuild's cohort should be removed; the other session's cohort of the same name stays.");
    }

    [Test]
    public void ARebuildBindsTheCohortItMadeEvenWhenAnotherSessionUsedTheSameName()
    {
        var name = $"Same name {Guid.NewGuid():N}";
        CohortIdentificationConfiguration other = null;
        var activator = new FailingActivator(RepositoryLocator)
        {
            WhenTheRebuildStarts = () =>
            {
                other = new CohortIdentificationConfiguration(Repository, name);
                other.CreateRootContainerIfNotExists();
            }
        };

        RebuildWith(activator, Script(CreateLine, SetLine()), name);

        var mine = Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single(c => c.ID != other.ID);
        Assert.Multiple(() =>
        {
            Assert.That(mine.RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively(), Has.Count.EqualTo(1),
                "The set should be in the cohort this rebuild made.");
            Assert.That(other.RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively(), Is.Empty,
                "The other session's cohort should be untouched.");
        });
    }

    [Test]
    public void TheDeleteSettingIsRestoredWhenTheRebuildCannotStart()
    {
        var activator = new FailingActivator(RepositoryLocator) { FailOnGetDelegates = true, InteractiveDeletes = true };

        Assert.That(() => RebuildWith(activator, Script(CreateLine), $"Cannot start {Guid.NewGuid():N}"), Throws.Exception);
        Assert.That(activator.InteractiveDeletes, Is.True, "The delete setting should be restored even when the rebuild fails to start.");
    }

    /// <summary>An activator that fails at a chosen point, once, to test the rebuild's failure paths.</summary>
    private sealed class FailingActivator : ConsoleInputManager
    {
        public Type FailOnPublishOf { get; set; }
        public bool FailOnGetDelegates { get; set; }

        /// <summary>Runs once when the rebuild starts, after its check: another session acting in between.</summary>
        public Action WhenTheRebuildStarts { get; set; }

        public FailingActivator(Rdmp.Core.Repositories.IRDMPPlatformRepositoryServiceLocator locator)
            : base(locator, ThrowImmediatelyCheckNotifier.Quiet)
        {
            DisallowInput = true;
        }

        public override void Publish(Rdmp.Core.MapsDirectlyToDatabaseTable.IMapsDirectlyToDatabaseTable databaseEntity)
        {
            if (FailOnPublishOf != null && FailOnPublishOf.IsInstanceOfType(databaseEntity))
            {
                FailOnPublishOf = null;
                throw new InvalidOperationException("A failure after the cohort was saved.");
            }

            base.Publish(databaseEntity);
        }

        public override List<Rdmp.Core.CommandExecution.CommandInvokerDelegate> GetDelegates()
        {
            if (FailOnGetDelegates)
                throw new InvalidOperationException("A failure while the rebuild starts.");
            var action = WhenTheRebuildStarts;
            WhenTheRebuildStarts = null;
            action?.Invoke();
            return base.GetDelegates();
        }
    }

    // a cohort with a set, a filter tree with a parameter and a source, a patient index table joined by the set,
    // a forced join, Top X, a cohort parameter and a project link
    private CohortIdentificationConfiguration RichCohort(Project project)
    {
        var cic = new CohortIdentificationConfiguration(Repository, $"Rich {Guid.NewGuid():N}");
        cic.CreateRootContainerIfNotExists();
        _ = new ProjectCohortIdentificationConfigurationAssociation(Repository, project, cic);
        new AnyTableSqlParameter(Repository, cic, "DECLARE @start AS date") { Value = "'2015-01-01'" }.SaveToDatabase();

        var pit = new Rdmp.Core.Curation.Data.Aggregation.AggregateConfiguration(Repository, _catalogue, "PIT") { CountSQL = null };
        pit.SaveToDatabase();
        _ = new Rdmp.Core.Curation.Data.Aggregation.AggregateDimension(Repository, _identifier, pit);
        cic.EnsureNamingConvention(pit);
        var joinable = new Rdmp.Core.Curation.Data.Cohort.Joinables.JoinableCohortAggregateConfiguration(Repository, cic, pit);

        var set = new Rdmp.Core.Curation.Data.Aggregation.AggregateConfiguration(Repository, _catalogue, "Set") { CountSQL = null };
        set.SaveToDatabase();
        _ = new Rdmp.Core.Curation.Data.Aggregation.AggregateDimension(Repository, _identifier, set);
        cic.EnsureNamingConvention(set);
        cic.RootCohortAggregateContainer.AddChild(set, 0);
        joinable.AddUser(set);
        Repository.AggregateForcedJoinManager.CreateLinkBetween(set, WhenIHaveA<TableInfo>());
        new Rdmp.Core.Curation.Data.AggregateTopX(Repository, set, 10).SaveToDatabase();
        new AnyTableSqlParameter(Repository, set, "DECLARE @window AS int") { Value = "90" }.SaveToDatabase();
        var fc = new Rdmp.Core.Curation.Data.Aggregation.AggregateFilterContainer(Repository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        var filter = new Rdmp.Core.Curation.Data.Aggregation.AggregateFilter(Repository, "F", fc) { WhereSQL = "x = @code" };
        filter.SaveToDatabase();
        var parameter = filter.GetFilterFactory().CreateNewParameter(filter, "DECLARE @code AS varchar(5)");
        parameter.Value = "'A'";
        parameter.SaveToDatabase();
        var child = new Rdmp.Core.Curation.Data.Aggregation.AggregateFilterContainer(Repository, FilterContainerOperation.OR);
        fc.AddChild(child);
        new Rdmp.Core.Curation.Data.Aggregation.AggregateFilter(Repository, "G", child) { WhereSQL = "y = 1" }.SaveToDatabase();
        return cic;
    }

    private Dictionary<string, int> Counts() => new()
    {
        ["cohorts"] = Repository.GetAllObjects<CohortIdentificationConfiguration>().Length,
        ["containers"] = Repository.GetAllObjects<CohortAggregateContainer>().Length,
        ["sets"] = Repository.GetAllObjects<Rdmp.Core.Curation.Data.Aggregation.AggregateConfiguration>().Length,
        ["columns"] = Repository.GetAllObjects<Rdmp.Core.Curation.Data.Aggregation.AggregateDimension>().Length,
        ["filter containers"] = Repository.GetAllObjects<Rdmp.Core.Curation.Data.Aggregation.AggregateFilterContainer>().Length,
        ["filters"] = Repository.GetAllObjects<Rdmp.Core.Curation.Data.Aggregation.AggregateFilter>().Length,
        ["filter parameters"] = Repository.GetAllObjects<Rdmp.Core.Curation.Data.Aggregation.AggregateFilterParameter>().Length,
        ["other parameters"] = Repository.GetAllObjects<AnyTableSqlParameter>().Length,
        ["patient index tables"] = Repository.GetAllObjects<Rdmp.Core.Curation.Data.Cohort.Joinables.JoinableCohortAggregateConfiguration>().Length,
        ["join uses"] = Repository.GetAllObjects<Rdmp.Core.Curation.Data.Cohort.Joinables.JoinableCohortAggregateConfigurationUse>().Length,
        ["top x"] = Repository.GetAllObjects<Rdmp.Core.Curation.Data.AggregateTopX>().Length,
        ["project links"] = Repository.GetAllObjects<ProjectCohortIdentificationConfigurationAssociation>().Length
    };

    private void RebuildWith(Rdmp.Core.CommandExecution.IBasicActivateItems activator, string text, string name, bool acceptRenamed = false)
    {
        var file = new FileInfo(Path.Join(Path.GetTempPath(), $"rdmp-cohortscript-{Guid.NewGuid()}.yaml"));
        File.WriteAllText(file.FullName, text);
        try
        {
            new ExecuteCommandBuildCohortFromScript(activator, file, name, acceptRenamed).Execute();
        }
        finally
        {
            file.Delete();
        }
    }

    // ---- helpers

    private CohortIdentificationConfiguration RoundTrip(CohortIdentificationConfiguration original)
    {
        var name = $"{original.Name} (rebuilt)";
        Rebuild(new BuildScriptWriter(original, RepositoryLocator).Document(), name);
        return Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single();
    }

    private static string Script(params string[] lines) =>
        "cohort-script-format: \"1\"\nCommands:\n" + string.Concat(lines.Select(l => $"  - {YamlText.DoubleQuoted(l)}\n"));

    private List<CohortScriptProblem> Check(string text, string newName = null)
    {
        var problems = new List<CohortScriptProblem>();
        var lines = CohortScriptReader.Read(text, problems);
        new CohortScriptChecker(RepositoryLocator, problems).Check(lines, newName ?? $"Checked {Guid.NewGuid():N}");
        return problems;
    }

    private void Rebuild(string text, string name)
    {
        var file = new FileInfo(Path.Join(Path.GetTempPath(), $"rdmp-cohortscript-{Guid.NewGuid()}.yaml"));
        File.WriteAllText(file.FullName, text);
        try
        {
            var activator = new ConsoleInputManager(RepositoryLocator, ThrowImmediatelyCheckNotifier.Quiet) { DisallowInput = true };
            new ExecuteCommandBuildCohortFromScript(activator, file, name).Execute();
        }
        finally
        {
            file.Delete();
        }
    }
}
