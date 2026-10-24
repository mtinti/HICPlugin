// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Linq;
using NUnit.Framework;
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.ReusableLibraryCode.Checks;
using RdmpCohortScript;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// RDMP's "always join everything" setting makes RDMP list every main table of a set's catalogue as a forced join,
/// though no join is saved for it. The export, the manifest and the add-join command must see only the saved joins.
/// </summary>
internal class CohortScriptJoinEverythingTests : UnitTests
{
    private Catalogue _catalogue;
    private ExtractionInformation _identifier;
    private ITableInfo _mainTable;
    private TableInfo _otherTable;

    [SetUp]
    public void MakeCatalogue()
    {
        var throwaway = WhenIHaveA(Repository, out _, out var first);
        _catalogue = throwaway.Catalogue;
        _catalogue.Name = $"Join everything {Guid.NewGuid():N}";
        _catalogue.SaveToDatabase();
        first.IsExtractionIdentifier = true;
        first.SaveToDatabase();
        _identifier = first;
        _mainTable = first.ColumnInfo.TableInfo;
        throwaway.DeleteInDatabase();
        _otherTable = ForcedJoinTable.JoinedTo(Repository, first);
    }

    [Test]
    public void AnExportWithTheSettingOnWritesOnlyTheSavedForcedJoins()
    {
        var (cic, set) = Cohort(joinTheOtherTable: true);

        var (listed, script) = CohortScriptRefreshTests.WithAlwaysJoinEverything(() =>
            (set.ForcedJoins.Select(t => t.ID).ToArray(), new BuildScriptWriter(cic, RepositoryLocator).Document()));

        var joins = script.Split('\n').Where(l => l.Contains("AddForcedJoin", StringComparison.Ordinal)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(listed, Has.Member(_mainTable.ID), "With the setting on, RDMP should list the main table, or this test checks nothing.");
            Assert.That(joins, Has.Length.EqualTo(1), "The script should have one forced join, the saved one.");
            Assert.That(joins.FirstOrDefault(), Does.Contain($"TableInfo:{_otherTable.ID}"), "The script's forced join should be to the saved table.");
        });
    }

    [Test]
    public void TheManifestWithTheSettingOnListsOnlyTheSavedForcedJoins()
    {
        var (cic, _) = Cohort(joinTheOtherTable: true);

        var manifest = CohortScriptRefreshTests.WithAlwaysJoinEverything(() => new CatalogueManifestWriter(RepositoryLocator).ForCohort(cic));

        var tables = manifest[manifest.IndexOf("\ntables:", StringComparison.Ordinal)..manifest.IndexOf("\nprojects:", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(tables, Does.Contain($"\"{_otherTable.ID}\""), "The manifest should list the saved forced-join table.");
            Assert.That(tables, Does.Not.Contain($"\"{_mainTable.ID}\""), "The manifest should not list the main table as a forced join.");
        });
    }

    [Test]
    public void ACohortExportedAndRebuiltWithTheSettingOnGainsNoForcedJoins()
    {
        var (cic, _) = Cohort(joinTheOtherTable: false);
        var name = $"Rebuilt {Guid.NewGuid():N}";

        CohortScriptRefreshTests.WithAlwaysJoinEverything(() =>
        {
            Rebuild(new BuildScriptWriter(cic, RepositoryLocator).Document(), name);
            return 0;
        });

        var rebuilt = Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single()
            .RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively().Single();
        Assert.That(rebuilt.ForcedJoins, Is.Empty, "The rebuilt set should have no saved forced join, as the original had none.");
    }

    [Test]
    public void AForcedJoinToTheMainTableCanBeAddedWithTheSettingOn()
    {
        var name = $"Main join {Guid.NewGuid():N}";
        var column = _identifier.GetRuntimeName();
        var script = Script("CreateNewCohortIdentificationConfiguration \"Main join\" => $cic1 $c1 $c2 $c3",
            $"AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"{column}\" false => $a1 $a1.{column}",
            $"AddForcedJoin AggregateConfiguration:$a1 TableInfo:{_mainTable.ID} \"{_mainTable.Name}\"");

        CohortScriptRefreshTests.WithAlwaysJoinEverything(() =>
        {
            Rebuild(script, name);
            return 0;
        });

        var set = Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single()
            .RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively().Single();
        Assert.That(set.ForcedJoins.Select(t => t.ID), Is.EqualTo(new[] { _mainTable.ID }),
            "The line should save a forced join to the main table, though RDMP already listed it with the setting on.");
    }

    [Test]
    public void AnAddForcedJoinLineThatSavesNothingIsCaughtWithTheSettingOn()
    {
        var repository = new FaultyRepository { IgnoreNewForcedJoins = true };
        var locator = new Rdmp.Core.Repositories.RepositoryProvider(repository);
        var throwaway = WhenIHaveA(repository, out _, out var first);
        var catalogue = throwaway.Catalogue;
        first.IsExtractionIdentifier = true;
        first.SaveToDatabase();
        throwaway.DeleteInDatabase();
        var main = first.ColumnInfo.TableInfo;
        var column = first.GetRuntimeName();
        var script = Script("CreateNewCohortIdentificationConfiguration \"Lost join\" => $cic1 $c1 $c2 $c3",
            $"AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:{catalogue.ID} \"{catalogue.Name}\" \"{column}\" false => $a1 $a1.{column}",
            $"AddForcedJoin AggregateConfiguration:$a1 TableInfo:{main.ID} \"{main.Name}\"");
        var activator = new ConsoleInputManager(locator, ThrowImmediatelyCheckNotifier.Quiet) { DisallowInput = true };

        var e = CohortScriptRefreshTests.WithAlwaysJoinEverything(() => Assert.Throws<CohortScriptRebuildException>(() =>
            CohortScriptRefreshTests.RebuildWith(activator, script, $"Lost join {Guid.NewGuid():N}")));

        Assert.That(e!.Reason, Is.EqualTo(CohortScriptRefusal.LineHadNoEffect),
            "The check after the line should see that no forced join was saved, though RDMP lists the main table.");
    }

    // a cohort with one set on the catalogue, and a saved forced join to the other table when asked
    private (CohortIdentificationConfiguration, AggregateConfiguration) Cohort(bool joinTheOtherTable)
    {
        var cic = new CohortIdentificationConfiguration(Repository, $"Join everything {Guid.NewGuid():N}");
        cic.CreateRootContainerIfNotExists();
        var set = new AggregateConfiguration(Repository, _catalogue, "Set") { CountSQL = null };
        set.SaveToDatabase();
        _ = new AggregateDimension(Repository, _identifier, set);
        cic.EnsureNamingConvention(set);
        cic.RootCohortAggregateContainer.AddChild(set, 0);
        if (joinTheOtherTable)
            Repository.AggregateForcedJoinManager.CreateLinkBetween(set, _otherTable);
        return (cic, set);
    }

    private static string Script(params string[] lines) =>
        "cohort-script-format: \"1\"\nCommands:\n" + string.Concat(lines.Select(l => $"  - {YamlText.DoubleQuoted(l)}\n"));

    private void Rebuild(string text, string name) =>
        CohortScriptRefreshTests.RebuildWith(new ConsoleInputManager(RepositoryLocator, ThrowImmediatelyCheckNotifier.Quiet) { DisallowInput = true },
            text, name);
}

/// <summary>The same rule on a database platform, where the saved joins are read with a query of their own.</summary>
public class CohortScriptJoinEverythingDatabaseTests : CohortScriptLifecycleBase
{
    [Test]
    public void OnADatabaseTheExportWithTheSettingOnWritesOnlyTheSavedForcedJoins()
    {
        StartWithACleanDatabase();
        var catalogue = Patients(Unique("join"), new[] { 1, 2 });
        var cic = new CohortIdentificationConfiguration(CatalogueRepository, Unique("join"));
        cic.CreateRootContainerIfNotExists();
        var set = AddSet(cic, cic.RootCohortAggregateContainer, catalogue, 0);
        var main = catalogue.GetTableInfosIdeallyJustFromMainTables().Single();
        var other = ForcedJoinTable.JoinedTo(CatalogueRepository, catalogue.GetAllExtractionInformation(ExtractionCategory.Any).First());
        CatalogueRepository.AggregateForcedJoinManager.CreateLinkBetween(set, other);
        CatalogueRepository.AggregateForcedJoinManager.CreateLinkBetween(set, main);

        var (listed, saved, script) = CohortScriptRefreshTests.WithAlwaysJoinEverything(() =>
            (set.ForcedJoins.Select(t => t.ID).ToArray(), SavedForcedJoins.Of(set).Select(t => t.ID).ToArray(),
                new BuildScriptWriter(cic, RepositoryLocator).Document()));

        Assert.Multiple(() =>
        {
            Assert.That(listed, Has.Member(main.ID), "With the setting on, RDMP should list the main table, or this test checks nothing.");
            Assert.That(saved, Is.EquivalentTo(new[] { other.ID, main.ID }), "Both saved forced joins should be read, the one to the main table too.");
            Assert.That(script.Split('\n').Count(l => l.Contains("AddForcedJoin", StringComparison.Ordinal)), Is.EqualTo(2),
                "The script should have the two saved forced joins.");
        });
    }
}
