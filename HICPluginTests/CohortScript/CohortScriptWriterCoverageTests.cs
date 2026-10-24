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
using Rdmp.Core.CohortCreation.Execution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using RdmpCohortScript;
using YamlDotNet.Serialization;

namespace HICPluginTests;

/// <summary>One test per refusal and per line form of the format 1 writer, and the alias marker's edge cases.</summary>
internal partial class CohortScriptWriterTests
{
    // ---- every refusal

    [Test]
    public void ACohortWithoutARootContainerIsRefused()
    {
        var bare = new CohortIdentificationConfiguration(Repository, $"No root {Guid.NewGuid():N}");

        AssertExportRefused(new BuildScriptWriter(bare, RepositoryLocator), CohortScriptRefusal.NoRootContainer);
    }

    [Test]
    public void ASetWithAPivotIsRefused()
    {
        var set = AddSet(_root, null);
        set.PivotOnDimensionID = set.AggregateDimensions.Single().ID;
        set.SaveToDatabase();

        AssertExportRefused(Writer(), CohortScriptRefusal.PivotOnSet);
    }

    [Test]
    public void ASetWithADateAxisIsRefused()
    {
        var set = AddSet(_root, null);
        _ = new AggregateContinuousDateAxis(Repository, set.AggregateDimensions.Single());

        AssertExportRefused(Writer(), CohortScriptRefusal.DateAxisOnSet);
    }

    [Test]
    public void ASetOnACatalogueServedByAnApiIsRefused()
    {
        AddSet(_root, null);
        _catalogue.Name = $"{PluginCohortCompiler.ApiPrefix}Service";
        _catalogue.SaveToDatabase();

        AssertExportRefused(Writer(), CohortScriptRefusal.ApiCatalogue);
    }

    [Test]
    public void ASetWithTwoIdentifierColumnsIsRefusedWithItsOwnReason()
    {
        var set = AddSet(_root, null);
        _other.IsExtractionIdentifier = true;
        _other.SaveToDatabase();
        _ = new AggregateDimension(Repository, _other, set);

        AssertExportRefused(Writer(), CohortScriptRefusal.TooManyIdentifierColumns);
    }

    [Test]
    public void AColumnNameThatCannotBePartOfAHandleIsRefused()
    {
        _identifier.Alias = "patient id";
        _identifier.SaveToDatabase();
        AddSet(_root, null);

        AssertExportRefused(Writer(), CohortScriptRefusal.ColumnNameNotUsableInHandle);
    }

    [Test]
    public void APatientIndexTableColumnWithACommaIsRefused()
    {
        _other.Alias = "first,last";
        _other.SaveToDatabase();
        AddPatientIndexTable();

        AssertExportRefused(Writer(), CohortScriptRefusal.CommaInColumnName);
    }

    [Test]
    public void APatientIndexTableThatUsesOneColumnTwiceIsRefused()
    {
        var joinable = AddPatientIndexTable();
        var table = Repository.GetObjectByID<AggregateConfiguration>(joinable.AggregateConfiguration_ID);
        var last = new AggregateDimension(Repository, _other, table) { SelectSQL = "MAX(date)", Alias = "last_date", GroupBy = false };
        last.SaveToDatabase();
        // the in-memory repository keeps one instance per object, with its columns cached
        joinable.AggregateConfiguration.ClearAllInjections();

        AssertExportRefused(Writer(), CohortScriptRefusal.RepeatedSourceColumn);
    }

    // ---- every line form

    [Test]
    public void AProjectLinkIsWrittenForEachLinkedProject()
    {
        var first = WhenIHaveA<Project>();
        var second = WhenIHaveA<Project>();
        _ = new ProjectCohortIdentificationConfigurationAssociation(Repository, first, _cic);
        _ = new ProjectCohortIdentificationConfigurationAssociation(Repository, second, _cic);

        var lines = Commands(Writer().Document());

        Assert.That(lines.Where(l => l.StartsWith("LinkCohortToProject", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            $"LinkCohortToProject CohortIdentificationConfiguration:$cic{_cic.ID} Project:{first.ID} \"{first.Name}\"",
            $"LinkCohortToProject CohortIdentificationConfiguration:$cic{_cic.ID} Project:{second.ID} \"{second.Name}\""
        }));
    }

    [Test]
    public void TheCohortsDescriptionTicketAndFolderAreWritten()
    {
        _cic.Description = "About\nthis cohort";
        _cic.Ticket = "T-1";
        _cic.Folder = "\\studies\\diabetes";
        _cic.SaveToDatabase();

        var lines = Parsed().Where(l => l.Command == "SetText" && l.Arguments[0].Text == $"CohortIdentificationConfiguration:$cic{_cic.ID}")
            .ToDictionary(l => l.Arguments[1].Text, l => l.Arguments[2].Text);

        Assert.That(lines, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["Description"] = "About\nthis cohort", ["Ticket"] = "T-1", ["Folder"] = "\\studies\\diabetes"
        }));
    }

    [Test]
    public void CohortAndSetParametersAreWrittenWithNullForAMissingComment()
    {
        new AnyTableSqlParameter(Repository, _cic, "DECLARE @start AS date") { Value = "'2015-01-01'", Comment = "Start" }.SaveToDatabase();
        var set = AddSet(_root, null);
        new AnyTableSqlParameter(Repository, set, "DECLARE @window AS int") { Value = "90" }.SaveToDatabase();

        var lines = Commands(Writer().Document());

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.One.EqualTo(
                $"AddCohortParameter CohortIdentificationConfiguration:$cic{_cic.ID} \"@start\" \"DECLARE @start AS date\" \"'2015-01-01'\" \"Start\""));
            Assert.That(lines, Has.One.EqualTo(
                $"AddCohortParameter AggregateConfiguration:$a{set.ID} \"@window\" \"DECLARE @window AS int\" \"90\" null"));
        });
    }

    [Test]
    public void AParameterValueIsMarkedLikeOtherSql()
    {
        var joinable = AddPatientIndexTable();
        var set = AddSet(_root, null);
        new AnyTableSqlParameter(Repository, set, "DECLARE @first AS date") { Value = $"(SELECT MIN(ix{joinable.ID}.admission_date))" }
            .SaveToDatabase();

        var value = Parsed().Single(l => l.Command == "AddCohortParameter").Arguments[3].Text;

        Assert.That(value, Is.EqualTo($"(SELECT MIN(ix{{$j{joinable.ID}}}.admission_date))"));
    }

    [Test]
    public void ARenamedSetGetsARenameLine()
    {
        var set = AddSet(_root, null);
        set.Name = $"cic_{_cic.ID}_Prescribed near an admission";
        set.SaveToDatabase();

        Assert.That(Commands(Writer().Document()),
            Has.One.EqualTo($"RenameCohortSet AggregateConfiguration:$a{set.ID} \"Prescribed near an admission\""));
    }

    [Test]
    public void ACopyNameIsKeptEvenWhenItLooksLikeADefault()
    {
        var set = AddSet(_root, null);
        set.Name = $"cic_{_cic.ID}_People in {_catalogue.Name} (Copy 1)";
        set.SaveToDatabase();

        Assert.That(Commands(Writer().Document()),
            Has.One.EqualTo($"RenameCohortSet AggregateConfiguration:$a{set.ID} \"People in {_catalogue.Name} (Copy 1)\""),
            "The original set may have been deleted, so the rebuild cannot be trusted to add the suffix itself.");
    }

    [Test]
    public void ADefaultNameGetsNoRenameLine()
    {
        var set = AddSet(_root, null);
        set.Name = $"cic_{_cic.ID}_People in {_catalogue.Name}";
        set.SaveToDatabase();

        Assert.That(Commands(Writer().Document()), Has.None.StartWith("RenameCohortSet"));
    }

    [Test]
    public void ADimensionsSqlIsAlwaysWrittenAndItsAliasAndGroupByWhenSet()
    {
        var joinable = AddPatientIndexTable();
        var table = Repository.GetObjectByID<AggregateConfiguration>(joinable.AggregateConfiguration_ID);
        var date = table.AggregateDimensions.Single(d => d.ExtractionInformation.ID == _other.ID);
        date.Alias = "event_date";
        date.GroupBy = false;
        date.SaveToDatabase();

        var dimensionLines = Parsed().Where(l => l.Command is "Set" or "SetText" && l.Arguments[0].Text.StartsWith("AggregateDimension:", StringComparison.Ordinal))
            .Select(l => (l.Arguments[0].Text, l.Arguments[1].Text, l.Arguments[2].Text)).ToArray();
        var dateTarget = $"AggregateDimension:$a{table.ID}.{_other.GetRuntimeName()}";

        Assert.Multiple(() =>
        {
            Assert.That(dimensionLines.Count(l => l.Item2 == "SelectSQL"), Is.EqualTo(2),
                "Both columns' SQL should be written, even though neither differs from its catalogue column.");
            Assert.That(dimensionLines, Has.Member((dateTarget, "Alias", "event_date")));
            Assert.That(dimensionLines, Has.Member((dateTarget, "GroupBy", "false")));
        });
    }

    [Test]
    public void TopXIsWrittenWithAndWithoutAnOrderByColumn()
    {
        var ordered = AddSet(_root, null);
        new AggregateTopX(Repository, ordered, 100) { OrderByDimensionIfAny_ID = ordered.AggregateDimensions.Single().ID, OrderByDirection = AggregateTopXOrderByDirection.Descending }
            .SaveToDatabase();
        var unordered = AddSet(_root, null);
        new AggregateTopX(Repository, unordered, 5).SaveToDatabase();

        var lines = Commands(Writer().Document());

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.One.EqualTo(
                $"SetTopX AggregateConfiguration:$a{ordered.ID} 100 AggregateDimension:$a{ordered.ID}.{_identifier.GetRuntimeName()} Descending"));
            Assert.That(lines, Has.One.EqualTo($"SetTopX AggregateConfiguration:$a{unordered.ID} 5 null Descending"),
                "Without an order-by column RDMP orders by the count column, descending by default.");
        });
    }

    [Test]
    public void AForcedJoinIsWrittenWithTheTablesIdAndName()
    {
        var set = AddSet(_root, null);
        var table = WhenIHaveA<TableInfo>();
        Repository.AggregateForcedJoinManager.CreateLinkBetween(set, table);

        Assert.That(Commands(Writer().Document()),
            Has.One.EqualTo($"AddForcedJoin AggregateConfiguration:$a{set.ID} TableInfo:{table.ID} \"{table.Name}\""));
    }

    [Test]
    public void AFilterTreeSharedByTwoSetsGetsItsOwnHandlesUnderEach()
    {
        var first = AddSet(_root, "code = 'A'");
        var second = AddSet(_root, null);
        second.OverrideFiltersByUsingParentAggregateConfigurationInstead_ID = first.ID;
        second.SaveToDatabase();
        var fc = first.RootFilterContainer_ID;

        var handles = Parsed().Where(l => l.Command == "AddNewFilterContainer").SelectMany(l => l.Handles).ToArray();

        Assert.That(handles, Is.EqualTo(new[] { $"$fc{fc}", $"$fc{fc}x2" }));
    }

    [Test]
    public void AFilterWhoseSourceIsGoneGetsACommentInsteadOfASourceLine()
    {
        var set = AddSet(_root, "age >= 18");
        var filter = (AggregateFilter)set.RootFilterContainer.GetFilters().Single();
        filter.ClonedFromExtractionFilter_ID = 987654;
        filter.SaveToDatabase();

        var document = Writer().Document();

        Assert.Multiple(() =>
        {
            Assert.That(Commands(document), Has.None.StartWith("SetFilterSource"));
            Assert.That(document, Does.Contain("# copied from published filter 987654, which no longer exists"));
        });
    }

    // ---- the alias marker

    [Test]
    public void TheAliasIsMarkedOnlyInSqlCodeNotInLiteralsCommentsOrNames()
    {
        var joinable = AddPatientIndexTable();
        var id = joinable.ID;
        var sql = $"d >= ix{id}.admission_date AND code = 'ix{id}.column' AND [ix{id}] = 1 -- ix{id} in a comment\n" +
                  $"/* ix{id} /* nested */ ix{id} */ AND other = 'ix2147483648' AND ix2147483648 = 1";
        AddSet(_root, sql);

        var marked = Parsed().Single(l => l.Command == "AddCohortFilter").Arguments[2].Text;

        Assert.That(marked, Is.EqualTo(
            $"d >= ix{{$j{id}}}.admission_date AND code = 'ix{id}.column' AND [ix{id}] = 1 -- ix{id} in a comment\n" +
            $"/* ix{id} /* nested */ ix{id} */ AND other = 'ix2147483648' AND ix2147483648 = 1"),
            "Only the alias in SQL code should be marked; a long number that is no joinable must not break the export.");
    }

    [Test]
    public void TheAliasIsMarkedAsAColumnQualifierInAnyQuotingOrCaseButNotAsAColumnName()
    {
        var joinable = AddPatientIndexTable();
        var id = joinable.ID;
        var sql = $"source.ix{id} = 1 AND ix{id}.a > 0 AND [ix{id}].b > 0 AND \"ix{id}\".c > 0 AND IX{id}.d > 0 " +
                  $"AND ix{id} .e > 0 AND ix{id} = 2";
        AddSet(_root, sql);

        var marked = Parsed().Single(l => l.Command == "AddCohortFilter").Arguments[2].Text;

        Assert.That(marked, Is.EqualTo(
            $"source.ix{id} = 1 AND ix{{$j{id}}}.a > 0 AND [ix{{$j{id}}}].b > 0 AND \"ix{{$j{id}}}\".c > 0 AND IX{{$j{id}}}.d > 0 " +
            $"AND ix{{$j{id}}} .e > 0 AND ix{id} = 2"),
            "The alias is the qualifier before a dot, bare, bracketed or quoted, in any case; a column named ix<id> stays.");
    }

    [Test]
    public void ASetNameThatStillStartsWithThePrefixIsRefused()
    {
        var set = AddSet(_root, null);
        set.Name = $"cic_{_cic.ID}_cic_5_Old name";
        set.SaveToDatabase();

        AssertExportRefused(Writer(), CohortScriptRefusal.SetNameLooksPrefixed);
    }

    // ---- the manifest

    [Test]
    public void TheManifestListsTheCatalogueOfAFiltersSourceFromAnotherCatalogue()
    {
        var elsewhere = MakeCatalogue("Elsewhere", out _, out var elsewhereColumn);
        var published = new ExtractionFilter(Repository, "Adults only", elsewhereColumn) { WhereSQL = "age >= 18", IsMandatory = true };
        published.SaveToDatabase();
        var set = AddSet(_root, "age >= 18");
        var filter = (AggregateFilter)set.RootFilterContainer.GetFilters().Single();
        filter.ClonedFromExtractionFilter_ID = published.ID;
        filter.SaveToDatabase();

        var manifest = Manifest(new CatalogueManifestWriter(RepositoryLocator).ForCohort(_cic));
        var catalogues = ((List<object>)manifest["catalogues"]).Cast<Dictionary<object, object>>().ToArray();

        Assert.That(catalogues.Select(c => c["id"]), Has.Member(elsewhere.ID.ToString()),
            "The catalogue that holds the source filter should be in the manifest.");
    }

    [Test]
    public void AnEmptyCatalogueListIsWrittenAsAnEmptyList()
    {
        var manifest = Manifest(new CatalogueManifestWriter(RepositoryLocator).ForCatalogues(Array.Empty<Catalogue>()));

        Assert.That(manifest["catalogues"], Is.InstanceOf<List<object>>().And.Empty);
    }

    // ---- helpers

    private IEnumerable<ScriptLine> Parsed() => Commands(Writer().Document()).Select(ScriptLine.Parse);

    private static void AssertExportRefused(BuildScriptWriter writer, CohortScriptRefusal expected) =>
        Assert.That(() => writer.Lines(), Throws.TypeOf<CohortScriptExportException>()
            .With.Property(nameof(CohortScriptExportException.Reason)).EqualTo(expected));

    private static Dictionary<object, object> Manifest(string text) =>
        (Dictionary<object, object>)new Deserializer().Deserialize(new StringReader(text));
}
