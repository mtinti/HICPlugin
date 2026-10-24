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
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.CohortCreation.Execution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using RdmpCohortScript;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// The plugin's script commands, each run as a typed line: by name, with text arguments, through RDMP's command
/// invoker, with an interactive activator that refuses every prompt. Refusals are checked by their reason code.
/// </summary>
internal class CohortScriptCommandTests : UnitTests
{
    private Catalogue _catalogue;
    private ExtractionInformation _identifier;
    private ExtractionInformation _other;
    private CohortIdentificationConfiguration _cic;
    private CohortAggregateContainer _root;
    private PromptRefusingActivator _activator;

    [SetUp]
    public void MakeCohort()
    {
        _catalogue = MakeCatalogue("Commands catalogue", out _identifier, out _other);
        _cic = new CohortIdentificationConfiguration(Repository, $"Commands {Guid.NewGuid():N}");
        _cic.CreateRootContainerIfNotExists();
        _root = _cic.RootCohortAggregateContainer;
        _activator = new PromptRefusingActivator(RepositoryLocator);
    }

    // ---- AddCatalogueToCohortSet

    [Test]
    public void AddCatalogueToCohortSetMakesASetThroughTheNamedIdentifierColumn()
    {
        MarkOtherColumnAsIdentifier();

        var result = Run("AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}",
            $"Catalogue:{_catalogue.ID}", _catalogue.Name, _other.GetRuntimeName(), "false");

        var set = _root.GetAggregateConfigurations().Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, $"The line should complete. RDMP said: {result.ImpossibleReason}");
            Assert.That(_activator.Prompts, Is.Empty,
                "The command should not ask which identifier column to use when the line names it.");
            Assert.That(set.AggregateDimensions.Single().ExtractionInformation.ID, Is.EqualTo(_other.ID),
                "The set should use the second identifier column, as the line says.");
        });
    }

    [Test]
    public void AddCatalogueToCohortSetRefusesToGuessBetweenTwoIdentifierColumns()
    {
        MarkOtherColumnAsIdentifier();

        var result = Run("AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}",
            $"Catalogue:{_catalogue.ID}", _catalogue.Name, "null", "false");

        AssertRefused(result, CohortScriptRefusal.AmbiguousIdentifierColumn);
        Assert.That(result.ImpossibleReason, Does.Contain(_other.GetRuntimeName()),
            "The message should name the identifier columns to choose from.");
    }

    [Test]
    public void AddCatalogueToCohortSetRefusesACatalogueWithNoIdentifierColumn()
    {
        _identifier.IsExtractionIdentifier = false;
        _identifier.SaveToDatabase();

        var result = Run("AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}",
            $"Catalogue:{_catalogue.ID}", _catalogue.Name, "null", "false");

        AssertRefused(result, CohortScriptRefusal.NoIdentifierColumn);
    }

    [Test]
    public void AddCatalogueToCohortSetRefusesACatalogueServedByAnApi()
    {
        _catalogue.Name = $"{PluginCohortCompiler.ApiPrefix}Some API";
        _catalogue.SaveToDatabase();

        var result = Run("AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}",
            $"Catalogue:{_catalogue.ID}", "null", "null", "false");

        AssertRefused(result, CohortScriptRefusal.ApiCatalogue);
    }

    [Test]
    public void AddCatalogueToCohortSetLeavesMandatoryFiltersOutWhenTheLineSaysFalse()
    {
        MakeMandatoryFilter();

        Run("AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}", $"Catalogue:{_catalogue.ID}",
            _catalogue.Name, "null", "false");

        var set = _root.GetAggregateConfigurations().Single();
        Assert.That(set.RootFilterContainer?.GetFilters() ?? Array.Empty<IFilter>(), Is.Empty,
            "With false, the set should not get the catalogue's mandatory filter.");
    }

    [Test]
    public void AddCatalogueToCohortSetCopiesMandatoryFiltersWhenTheLineSaysTrue()
    {
        MakeMandatoryFilter();

        Run("AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}", $"Catalogue:{_catalogue.ID}",
            _catalogue.Name, "null", "true");

        var set = _root.GetAggregateConfigurations().Single();
        Assert.That(set.RootFilterContainer?.GetFilters().Select(f => f.WhereSQL), Is.EqualTo(new[] { "age >= 18" }),
            "With true, the set should get the catalogue's mandatory filter.");
    }

    [Test]
    public void AddCatalogueToCohortSetRefusesAnIdWhoseNameDiffers()
    {
        var result = Run("AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}",
            $"Catalogue:{_catalogue.ID}", "Another catalogue", "null", "false");

        AssertRefused(result, CohortScriptRefusal.NameMismatch);
        Assert.Multiple(() =>
        {
            Assert.That(result.ImpossibleReason, Does.Contain("\"Another catalogue\"").And.Contain($"\"{_catalogue.Name}\""),
                "The message should give the name in the script and the catalogue's current name.");
            Assert.That(_root.GetAggregateConfigurations(), Is.Empty, "No set should be made.");
        });
    }

    [Test]
    public void AddCatalogueToCohortSetSkipsTheNameCheckWhenTheNameIsNull()
    {
        var result = Run("AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}",
            $"Catalogue:{_catalogue.ID}", "null", "null", "false");

        Assert.That(result.Completed, Is.True, $"A typed line may give null for the name. RDMP said: {result.ImpossibleReason}");
    }

    // ---- AddPatientIndexTable and UsePatientIndexTable

    [Test]
    public void AddPatientIndexTableMakesAJoinableWithTheIdentifierAndTheExtraColumns()
    {
        var result = Run("AddPatientIndexTable", $"CohortIdentificationConfiguration:{_cic.ID}",
            $"Catalogue:{_catalogue.ID}", _catalogue.Name, "null", _other.GetRuntimeName(), "false");

        var joinable = _cic.GetAllJoinables().Single();
        var columns = joinable.AggregateConfiguration.AggregateDimensions.Select(d => d.ExtractionInformation.ID);
        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, $"The line should complete. RDMP said: {result.ImpossibleReason}");
            Assert.That(_activator.Prompts, Is.Empty, "The command should not ask anything.");
            Assert.That(columns, Is.EquivalentTo(new[] { _identifier.ID, _other.ID }),
                "The patient index table should select the identifier and the extra column.");
            Assert.That(joinable.AggregateConfiguration.Name, Does.StartWith($"cic_{_cic.ID}_"),
                "The table's set should carry its cohort's prefix.");
        });
    }

    [Test]
    public void AddPatientIndexTableRefusesAColumnTheCatalogueDoesNotHave()
    {
        var result = Run("AddPatientIndexTable", $"CohortIdentificationConfiguration:{_cic.ID}",
            $"Catalogue:{_catalogue.ID}", _catalogue.Name, "null", "no_such_column", "false");

        AssertRefused(result, CohortScriptRefusal.UnknownColumn);
        Assert.That(_cic.GetAllJoinables(), Is.Empty, "No patient index table should be made.");
    }

    [Test]
    public void AddPatientIndexTableRefusesASecondIdentifierColumnBeforeMakingAnything()
    {
        MarkOtherColumnAsIdentifier();
        var aggregates = Repository.GetAllObjects<AggregateConfiguration>().Length;
        var dimensions = Repository.GetAllObjects<AggregateDimension>().Length;

        var result = Run("AddPatientIndexTable", $"CohortIdentificationConfiguration:{_cic.ID}",
            $"Catalogue:{_catalogue.ID}", _catalogue.Name, _identifier.GetRuntimeName(), _other.GetRuntimeName(), "false");

        AssertRefused(result, CohortScriptRefusal.IdentifierAsExtraColumn);
        Assert.Multiple(() =>
        {
            Assert.That(Repository.GetAllObjects<AggregateConfiguration>(), Has.Length.EqualTo(aggregates),
                "No set should be left behind.");
            Assert.That(Repository.GetAllObjects<AggregateDimension>(), Has.Length.EqualTo(dimensions),
                "No column should be left behind.");
            Assert.That(_cic.GetAllJoinables(), Is.Empty, "No patient index table should be made.");
        });
    }

    [Test]
    public void UsePatientIndexTableLinksTheSetWithTheGivenJoinType()
    {
        var joinable = AddPatientIndexTable(_cic);
        var set = AddSet();

        var result = Run("UsePatientIndexTable", $"AggregateConfiguration:{set.ID}",
            $"JoinableCohortAggregateConfiguration:{joinable.ID}", "Inner");

        var use = set.PatientIndexJoinablesUsed.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, $"The line should complete. RDMP said: {result.ImpossibleReason}");
            Assert.That(use.JoinableCohortAggregateConfiguration_ID, Is.EqualTo(joinable.ID));
            Assert.That(use.JoinType, Is.EqualTo(ExtractionJoinType.Inner));
        });
    }

    [Test]
    public void UsePatientIndexTableRefusesATableOfAnotherCohort()
    {
        var other = new CohortIdentificationConfiguration(Repository, $"Other {Guid.NewGuid():N}");
        other.CreateRootContainerIfNotExists();
        var joinable = AddPatientIndexTable(other);
        var set = AddSet();

        var result = Run("UsePatientIndexTable", $"AggregateConfiguration:{set.ID}",
            $"JoinableCohortAggregateConfiguration:{joinable.ID}", "Inner");

        AssertRefused(result, CohortScriptRefusal.DifferentCohorts);
    }

    [Test]
    public void UsePatientIndexTableRefusesASetThatBelongsToNoCohort()
    {
        var joinable = AddPatientIndexTable(_cic);
        var loose = new AggregateConfiguration(Repository, _catalogue, "Not in a cohort");
        _ = new AggregateDimension(Repository, _identifier, loose);

        var result = Run("UsePatientIndexTable", $"AggregateConfiguration:{loose.ID}",
            $"JoinableCohortAggregateConfiguration:{joinable.ID}", "Inner");

        AssertRefused(result, CohortScriptRefusal.NotInACohort);
    }

    [Test]
    public void UsePatientIndexTableRefusesASecondTableForTheSameSet()
    {
        var first = AddPatientIndexTable(_cic);
        var second = AddPatientIndexTable(_cic);
        var set = AddSet();
        Run("UsePatientIndexTable", $"AggregateConfiguration:{set.ID}", $"JoinableCohortAggregateConfiguration:{first.ID}", "Inner");

        var result = Run("UsePatientIndexTable", $"AggregateConfiguration:{set.ID}",
            $"JoinableCohortAggregateConfiguration:{second.ID}", "Inner");

        AssertRefused(result, CohortScriptRefusal.AlreadyJoinsATable);
    }

    // ---- AddForcedJoin

    [Test]
    public void AddForcedJoinForcesTheTableIntoTheSetsQuery()
    {
        var set = AddSet();
        var table = WhenIHaveA<TableInfo>();

        var result = Run("AddForcedJoin", $"AggregateConfiguration:{set.ID}", $"TableInfo:{table.ID}", table.Name);

        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, $"The line should complete. RDMP said: {result.ImpossibleReason}");
            Assert.That(set.ForcedJoins.Select(t => t.ID), Is.EqualTo(new[] { table.ID }));
        });
    }

    [Test]
    public void AddForcedJoinRefusesAnIdWhoseNameDiffers()
    {
        var set = AddSet();
        var table = WhenIHaveA<TableInfo>();

        var result = Run("AddForcedJoin", $"AggregateConfiguration:{set.ID}", $"TableInfo:{table.ID}", "[Other]..[Table]");

        AssertRefused(result, CohortScriptRefusal.NameMismatch);
        Assert.That(set.ForcedJoins, Is.Empty, "No forced join should be made.");
    }

    [Test]
    public void AddForcedJoinRefusesASecondJoinToTheSameTable()
    {
        var set = AddSet();
        var table = WhenIHaveA<TableInfo>();
        Run("AddForcedJoin", $"AggregateConfiguration:{set.ID}", $"TableInfo:{table.ID}", table.Name);

        var result = Run("AddForcedJoin", $"AggregateConfiguration:{set.ID}", $"TableInfo:{table.ID}", table.Name);

        AssertRefused(result, CohortScriptRefusal.AlreadyForcedJoin);
    }

    // ---- LinkCohortToProject

    [Test]
    public void LinkCohortToProjectLinksTheCohortOnceOnly()
    {
        var project = WhenIHaveA<Project>();

        var first = Run("LinkCohortToProject", $"CohortIdentificationConfiguration:{_cic.ID}", $"Project:{project.ID}",
            project.Name);
        var second = Run("LinkCohortToProject", $"CohortIdentificationConfiguration:{_cic.ID}", $"Project:{project.ID}",
            project.Name);

        var links = RepositoryLocator.DataExportRepository
            .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", _cic.ID);
        Assert.Multiple(() =>
        {
            Assert.That(first.Completed, Is.True, $"The first line should complete. RDMP said: {first.ImpossibleReason}");
            Assert.That(links.Select(l => l.Project_ID), Is.EqualTo(new[] { project.ID }), "The cohort should be linked to the project.");
        });
        AssertRefused(second, CohortScriptRefusal.AlreadyLinked);
    }

    // ---- AddCohortParameter

    [Test]
    public void AddCohortParameterAddsAParameterToACohortWithNoSetsToASetAndToAFilter()
    {
        var onCohort = Run("AddCohortParameter", $"CohortIdentificationConfiguration:{_cic.ID}", "@studyStart",
            "DECLARE @studyStart AS date", "'2015-01-01'", "Start of the study");
        var set = AddSet();
        var onSet = Run("AddCohortParameter", $"AggregateConfiguration:{set.ID}", "@window", "DECLARE @window AS int",
            "90", "null");
        var filter = AddFilter(set, "code = @code");
        var onFilter = Run("AddCohortParameter", $"AggregateFilter:{filter.ID}", "@code",
            "DECLARE @code AS varchar(10)", "'E11%'", "null");

        var cohortParameter = _cic.GetAllParameters().Single();
        var setParameter = Repository.GetAllObjects<AnyTableSqlParameter>()
            .Single(p => p.ReferencedObjectType == nameof(AggregateConfiguration) && p.ReferencedObjectID == set.ID);
        var filterParameter = filter.GetAllParameters().Single();
        Assert.Multiple(() =>
        {
            Assert.That(new[] { onCohort.Completed, onSet.Completed, onFilter.Completed }, Is.All.True,
                "All three lines should complete.");
            Assert.That((cohortParameter.ParameterName, cohortParameter.Value, cohortParameter.Comment),
                Is.EqualTo(("@studyStart", "'2015-01-01'", "Start of the study")));
            Assert.That((setParameter.ParameterName, setParameter.Value), Is.EqualTo(("@window", "90")));
            Assert.That(filterParameter, Is.InstanceOf<AggregateFilterParameter>());
            Assert.That((filterParameter.ParameterName, filterParameter.Value), Is.EqualTo(("@code", "'E11%'")));
        });
    }

    [Test]
    public void AddCohortParameterRefusesADeclarationOfAnotherName()
    {
        var result = Run("AddCohortParameter", $"CohortIdentificationConfiguration:{_cic.ID}", "@studyStart",
            "DECLARE @other AS date", "'2015-01-01'", "null");

        AssertRefused(result, CohortScriptRefusal.DeclarationMismatch);
        Assert.That(_cic.GetAllParameters(), Is.Empty, "No parameter should be made.");
    }

    [Test]
    public void AddCohortParameterRefusesANameThatRdmpReserves()
    {
        var reserved = AnyTableSqlParameter.ProhibitedParameterNames.First();

        var result = Run("AddCohortParameter", $"CohortIdentificationConfiguration:{_cic.ID}", reserved,
            $"DECLARE {reserved} AS int", "1", "null");

        AssertRefused(result, CohortScriptRefusal.ReservedParameterName);
    }

    [Test]
    public void AddCohortParameterRefusesASecondParameterOfTheSameName()
    {
        Run("AddCohortParameter", $"CohortIdentificationConfiguration:{_cic.ID}", "@studyStart",
            "DECLARE @studyStart AS date", "'2015-01-01'", "null");

        var result = Run("AddCohortParameter", $"CohortIdentificationConfiguration:{_cic.ID}", "@studyStart",
            "DECLARE @studyStart AS date", "'2016-01-01'", "null");

        AssertRefused(result, CohortScriptRefusal.DuplicateParameter);
    }

    // ---- AddChildFilterContainer and SetFilterSource

    [Test]
    public void AddChildFilterContainerAddsAnAndContainerUnderTheGivenOne()
    {
        var set = AddSet();
        var root = (AggregateFilterContainer)set.RootFilterContainer;

        var result = Run("AddChildFilterContainer", $"AggregateFilterContainer:{root.ID}");

        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, $"The line should complete. RDMP said: {result.ImpossibleReason}");
            Assert.That(root.GetSubContainers().Single().Operation, Is.EqualTo(FilterContainerOperation.AND));
        });
    }

    [Test]
    public void SetFilterSourceRecordsThePublishedFilterAFilterWasCopiedFrom()
    {
        var set = AddSet();
        var filter = AddFilter(set, "age >= 18");
        var published = new ExtractionFilter(Repository, "Adults only", _other) { WhereSQL = "age >= 18" };
        published.SaveToDatabase();

        var result = Run("SetFilterSource", $"AggregateFilter:{filter.ID}", $"ExtractionFilter:{published.ID}",
            published.Name);

        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, $"The line should complete. RDMP said: {result.ImpossibleReason}");
            Assert.That(Repository.GetObjectByID<AggregateFilter>(filter.ID).ClonedFromExtractionFilter_ID,
                Is.EqualTo(published.ID));
        });
    }

    // ---- RenameCohortSet

    [Test]
    public void RenameCohortSetKeepsTheCohortsPrefix()
    {
        var set = AddSet();

        var result = Run("RenameCohortSet", $"AggregateConfiguration:{set.ID}", "Prescribed near an admission");

        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, $"The line should complete. RDMP said: {result.ImpossibleReason}");
            Assert.That(Repository.GetObjectByID<AggregateConfiguration>(set.ID).Name,
                Is.EqualTo($"cic_{_cic.ID}_Prescribed near an admission"));
        });
    }

    [Test]
    public void RenameCohortSetRefusesANameThatAlreadyHasAPrefix()
    {
        var set = AddSet();

        var result = Run("RenameCohortSet", $"AggregateConfiguration:{set.ID}", "cic_99_Old name");

        AssertRefused(result, CohortScriptRefusal.NameHasPrefix);
    }

    [Test]
    public void RenameCohortSetRefusesANameAnotherSetOfTheCohortHas()
    {
        var first = AddSet();
        var second = AddSet();
        Run("RenameCohortSet", $"AggregateConfiguration:{first.ID}", "Same name");

        var result = Run("RenameCohortSet", $"AggregateConfiguration:{second.ID}", "Same name");

        AssertRefused(result, CohortScriptRefusal.NameTaken);
        Assert.That(result.ImpossibleReason, Does.Contain($"Aggregate {first.ID}"), "The message should name the set that has the name.");
    }

    [Test]
    public void RenameCohortSetRefusesANameAnAggregateOutsideTheCohortTreeHas()
    {
        var set = AddSet();
        var loose = new AggregateConfiguration(Repository, _catalogue, $"cic_{_cic.ID}_Requested");
        loose.SaveToDatabase();

        var result = Run("RenameCohortSet", $"AggregateConfiguration:{set.ID}", "Requested");

        AssertRefused(result, CohortScriptRefusal.NameTaken);
        Assert.That(Repository.GetObjectByID<AggregateConfiguration>(set.ID).Name, Does.Not.Contain("Copy"),
            "The set should keep its old name rather than get a copy suffix.");
    }

    // ---- SetTopX

    [Test]
    public void SetTopXKeepsTheGivenNumberOfRowsOrderedByAColumnOfTheSet()
    {
        var set = AddSet();
        var dimension = set.AggregateDimensions.Single();

        var result = Run("SetTopX", $"AggregateConfiguration:{set.ID}", "100", $"AggregateDimension:{dimension.ID}",
            "Descending");

        var topX = set.GetTopXIfAny();
        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.True, $"The line should complete. RDMP said: {result.ImpossibleReason}");
            Assert.That(topX?.TopX, Is.EqualTo(100));
            Assert.That(topX?.OrderByDimensionIfAny_ID, Is.EqualTo(dimension.ID));
            Assert.That(topX?.OrderByDirection, Is.EqualTo(AggregateTopXOrderByDirection.Descending));
        });
    }

    [Test]
    public void SetTopXRefusesAColumnOfAnotherSet()
    {
        var set = AddSet();
        var otherSet = AddSet();

        var result = Run("SetTopX", $"AggregateConfiguration:{set.ID}", "100",
            $"AggregateDimension:{otherSet.AggregateDimensions.Single().ID}", "Descending");

        AssertRefused(result, CohortScriptRefusal.OrderByOtherSet);
        Assert.That(set.GetTopXIfAny(), Is.Null, "No Top X should be made.");
    }

    // ---- frozen cohorts

    [Test]
    public void EveryCommandRefusesToChangeAFrozenCohort()
    {
        var set = AddSet();
        var filter = AddFilter(set, "code = 'A'");
        var joinable = AddPatientIndexTable(_cic);
        var table = WhenIHaveA<TableInfo>();
        var project = WhenIHaveA<Project>();
        var published = new ExtractionFilter(Repository, "Published", _other) { WhereSQL = "1 = 1" };
        published.SaveToDatabase();
        var fc = (AggregateFilterContainer)set.RootFilterContainer;
        _cic.Freeze();

        var lines = new[]
        {
            new[] { "AddCatalogueToCohortSet", $"CohortAggregateContainer:{_root.ID}", $"Catalogue:{_catalogue.ID}", "null", "null", "false" },
            new[] { "AddPatientIndexTable", $"CohortIdentificationConfiguration:{_cic.ID}", $"Catalogue:{_catalogue.ID}", "null", "null", "null", "false" },
            new[] { "UsePatientIndexTable", $"AggregateConfiguration:{set.ID}", $"JoinableCohortAggregateConfiguration:{joinable.ID}", "Inner" },
            new[] { "AddForcedJoin", $"AggregateConfiguration:{set.ID}", $"TableInfo:{table.ID}", "null" },
            new[] { "LinkCohortToProject", $"CohortIdentificationConfiguration:{_cic.ID}", $"Project:{project.ID}", "null" },
            new[] { "AddCohortParameter", $"AggregateConfiguration:{set.ID}", "@window", "DECLARE @window AS int", "90", "null" },
            new[] { "AddChildFilterContainer", $"AggregateFilterContainer:{fc.ID}" },
            new[] { "SetFilterSource", $"AggregateFilter:{filter.ID}", $"ExtractionFilter:{published.ID}", "null" },
            new[] { "RenameCohortSet", $"AggregateConfiguration:{set.ID}", "New name" },
            new[] { "SetTopX", $"AggregateConfiguration:{set.ID}", "10", "null", "Ascending" }
        };

        var refusals = lines.Select(line => (line[0], ((CohortScriptCommand)CommandLine.RunCommand(_activator, line).Command)?.Refusal))
            .ToArray();

        Assert.That(refusals.Where(r => r.Item2 != CohortScriptRefusal.ReadOnly).Select(r => r.Item1), Is.Empty,
            "Every plugin command should refuse to change a frozen cohort.");
    }

    // ---- helpers

    private (bool Completed, string ImpossibleReason, IAtomicCommand Command) Run(params string[] line) =>
        CommandLine.RunCommand(_activator, line);

    private static void AssertRefused((bool Completed, string ImpossibleReason, IAtomicCommand Command) result,
        CohortScriptRefusal expected)
    {
        Assert.That(result.Completed, Is.False, "The line should not run.");
        var command = result.Command as CohortScriptCommand;
        Assert.That(command?.Refusal, Is.EqualTo(expected), $"The refusal should be {expected}. RDMP said: {result.ImpossibleReason}");
    }

    private void MarkOtherColumnAsIdentifier()
    {
        _other.IsExtractionIdentifier = true;
        _other.SaveToDatabase();
    }

    private void MakeMandatoryFilter()
    {
        new ExtractionFilter(Repository, "Adults only", _other) { WhereSQL = "age >= 18", IsMandatory = true }
            .SaveToDatabase();
    }

    private JoinableCohortAggregateConfiguration AddPatientIndexTable(CohortIdentificationConfiguration cic)
    {
        var result = Run("AddPatientIndexTable", $"CohortIdentificationConfiguration:{cic.ID}", $"Catalogue:{_catalogue.ID}",
            _catalogue.Name, "null", "null", "false");
        Assert.That(result.Completed, Is.True, $"Setting up a patient index table should work. RDMP said: {result.ImpossibleReason}");
        return ((ExecuteCommandAddPatientIndexTable)result.Command).CreatedJoinable;
    }

    private AggregateConfiguration AddSet()
    {
        var set = new AggregateConfiguration(Repository, _catalogue, "Commands set");
        _ = new AggregateDimension(Repository, _identifier, set);
        _cic.EnsureNamingConvention(set);
        _root.AddChild(set, 0);
        var fc = new AggregateFilterContainer(Repository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        return set;
    }

    private AggregateFilter AddFilter(AggregateConfiguration set, string whereSql)
    {
        var filter = new AggregateFilter(Repository, "A filter", (AggregateFilterContainer)set.RootFilterContainer)
        {
            WhereSQL = whereSql
        };
        filter.SaveToDatabase();
        return filter;
    }

    private Catalogue MakeCatalogue(string name, out ExtractionInformation idEi, out ExtractionInformation otherEi)
    {
        var throwaway = WhenIHaveA(Repository, out otherEi, out var firstEi);
        var cata = throwaway.Catalogue;
        cata.Name = name;
        cata.SaveToDatabase();
        firstEi.IsExtractionIdentifier = true;
        firstEi.SaveToDatabase();
        idEi = firstEi;
        throwaway.DeleteInDatabase();
        return cata;
    }
}
