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
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.ReusableLibraryCode.Checks;
using RdmpCohortScript;
using Tests.Common;
using YamlDotNet.Serialization;

namespace HICPluginTests;

/// <summary>
/// The commands around the build script: the check, the catalogue manifest, the export of many cohorts, and the
/// generated command reference.
/// </summary>
internal class CohortScriptToolsTests : UnitTests
{
    private Catalogue _catalogue;
    private ExtractionInformation _identifier;
    private string _column;
    private DirectoryInfo _folder;

    [SetUp]
    public void MakeCatalogueAndFolder()
    {
        var throwaway = WhenIHaveA(Repository, out _, out var first);
        _catalogue = throwaway.Catalogue;
        _catalogue.Name = $"Tools catalogue {Guid.NewGuid():N}";
        _catalogue.SaveToDatabase();
        first.IsExtractionIdentifier = true;
        first.SaveToDatabase();
        _identifier = first;
        _column = first.GetRuntimeName();
        throwaway.DeleteInDatabase();

        _folder = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"rdmp-cohortscript-{Guid.NewGuid()}"));
    }

    [TearDown]
    public void DeleteFolder() => _folder.Delete(true);

    private const string CreateLine = "CreateNewCohortIdentificationConfiguration \"Tools test\" => $cic1 $c1 $c2 $c3";

    private string SetLine(string import = "false") =>
        $"AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"{_column}\" {import} => $a1 $a1.{_column}";

    // ---- parameters a filter uses

    [TestCase("x = @code", "AggregateFilter:$f1", TestName = "AParameterDeclaredOnTheFilterIsAccepted")]
    [TestCase("x = @code", "AggregateConfiguration:$a1", TestName = "AParameterDeclaredOnTheSetIsAccepted")]
    [TestCase("x = @code", "CohortIdentificationConfiguration:$cic1", TestName = "AParameterDeclaredOnTheCohortIsAccepted")]
    [TestCase("x = @CODE", "AggregateFilter:$f1", TestName = "AParameterIsMatchedWithoutCase")]
    [TestCase("x = '@code' -- @code\n/* @code */ AND y = [@code] AND z = @@ROWCOUNT", null, TestName = "AnAtSignInsideALiteralACommentOrANameIsNotAParameter")]
    public void AFilterWhoseParametersAreDeclaredPassesTheCheck(string sql, string owner)
    {
        var problems = Check(Script(FilterLines(sql, owner)));

        Assert.That(problems.Select(p => p.Reason), Has.No.Member(CohortScriptRefusal.ParameterNotDeclared), string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void AFilterThatUsesAnUndeclaredParameterIsRefusedWithItsLine()
    {
        var problems = Check(Script(FilterLines("x = @code AND y = @other", "AggregateFilter:$f1")));

        var undeclared = problems.Where(p => p.Reason == CohortScriptRefusal.ParameterNotDeclared).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(undeclared, Has.Length.EqualTo(1), "Only @other is undeclared.");
            Assert.That(undeclared.Single().Message, Does.Contain("@other"));
            Assert.That(undeclared.Single().FileLine, Is.EqualTo(7), "The problem should name the filter's line in the file.");
        });
    }

    [Test]
    public void AParameterOfAnotherSetDoesNotCoverAFilter()
    {
        var lines = FilterLines("x = @code", null).Concat(new[]
        {
            $"AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"{_column}\" false => $a2 $a2.{_column}",
            "AddCohortParameter AggregateConfiguration:$a2 \"@code\" \"DECLARE @code AS varchar(5)\" \"'A'\" null"
        }).ToArray();

        Assert.That(Check(Script(lines)).Select(p => p.Reason), Has.Member(CohortScriptRefusal.ParameterNotDeclared));
    }

    [Test]
    public void AParameterDeclaredOnAnotherFilterOfTheSetIsAccepted()
    {
        var lines = FilterLines("x = @code", null).Concat(new[]
        {
            "AddCohortFilter AggregateFilterContainer:$fc1 \"Declares\" \"y = 1\" => $f2",
            "AddCohortParameter AggregateFilter:$f2 \"@code\" \"DECLARE @code AS varchar(5)\" \"'A'\" null"
        }).ToArray();

        Assert.That(Check(Script(lines)).Select(p => p.Reason), Has.No.Member(CohortScriptRefusal.ParameterNotDeclared));
    }

    [TestCase("AggregateConfiguration:$a2", true, TestName = "AParameterOfAJoinedPatientIndexTableIsAccepted")]
    [TestCase("AggregateFilter:$f2", true, TestName = "AParameterOfAJoinedPatientIndexTablesFilterIsAccepted")]
    [TestCase("AggregateConfiguration:$a2", false, TestName = "AParameterOfAPatientIndexTableTheSetDoesNotJoinIsRefused")]
    public void AParameterOfAPatientIndexTable(string owner, bool joined)
    {
        var lines = FilterLines("x = @code", null).Concat(new[]
        {
            $"AddPatientIndexTable CohortIdentificationConfiguration:$cic1 Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"{_column}\" null false => $a2 $j1 $a2.{_column}",
            "AddNewFilterContainer AggregateConfiguration:$a2 => $fc3",
            "AddCohortFilter AggregateFilterContainer:$fc3 \"In the table\" \"y = 1\" => $f2",
            $"AddCohortParameter {owner} \"@code\" \"DECLARE @code AS varchar(5)\" \"'A'\" null"
        }).Concat(joined ? new[] { "UsePatientIndexTable AggregateConfiguration:$a1 JoinableCohortAggregateConfiguration:$j1 Inner" } : Array.Empty<string>())
            .ToArray();

        var reasons = Check(Script(lines)).Select(p => p.Reason);

        if (joined)
            Assert.That(reasons, Has.No.Member(CohortScriptRefusal.ParameterNotDeclared));
        else
            Assert.That(reasons, Has.Member(CohortScriptRefusal.ParameterNotDeclared));
    }

    [TestCase("AggregateConfiguration:$a1 HavingSQL \"count(*) > @minimum\"", false, TestName = "AHavingThatUsesAnUndeclaredParameterIsRefused")]
    [TestCase("AggregateConfiguration:$a1 CountSQL \"count(*) * @weight\"", false, TestName = "ACountColumnThatUsesAnUndeclaredParameterIsRefused")]
    [TestCase("AggregateDimension:$a1.{0} SelectSQL \"LEFT({0}, @length)\"", false, TestName = "AColumnSqlThatUsesAnUndeclaredParameterIsRefused")]
    [TestCase("AggregateConfiguration:$a1 HavingSQL \"count(*) > @minimum\"", true, TestName = "AHavingThatUsesTheSetsParameterIsAccepted")]
    public void SetSqlParameters(string setText, bool declared)
    {
        var lines = new List<string> { CreateLine, SetLine(), $"SetText {string.Format(setText, _column)}" };
        if (declared)
            lines.Add("AddCohortParameter AggregateConfiguration:$a1 \"@minimum\" \"DECLARE @minimum AS int\" \"2\" null");

        var reasons = Check(Script(lines.ToArray())).Select(p => p.Reason);

        if (declared)
            Assert.That(reasons, Has.No.Member(CohortScriptRefusal.ParameterNotDeclared));
        else
            Assert.That(reasons, Has.Member(CohortScriptRefusal.ParameterNotDeclared));
    }

    // a set with a filter in a child filter container, and a parameter on the given owner (none when null)
    private string[] FilterLines(string sql, string owner)
    {
        var lines = new List<string>
        {
            CreateLine,
            SetLine(),
            "AddNewFilterContainer AggregateConfiguration:$a1 => $fc1",
            "AddChildFilterContainer AggregateFilterContainer:$fc1 => $fc2",
            $"AddCohortFilter AggregateFilterContainer:$fc2 \"F\" \"{sql}\" => $f1"
        };
        if (owner != null)
            lines.Add($"AddCohortParameter {owner} \"@code\" \"DECLARE @code AS varchar(5)\" \"'A'\" null");
        return lines.ToArray();
    }

    // ---- rules the rebuild would meet, caught before anything is built

    private string PatientIndexTableLine(string set, string joinable) =>
        $"AddPatientIndexTable CohortIdentificationConfiguration:$cic1 Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"{_column}\" null false => {set} {joinable} {set}.{_column}";

    [Test]
    public void AContainerUsedAfterItsDeleteLineIsRefused()
    {
        var problems = Check(Script(CreateLine, "Delete CohortAggregateContainer:$c3",
            $"AddCatalogueToCohortSet CohortAggregateContainer:$c3 Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"{_column}\" false => $a1 $a1.{_column}"));

        Assert.Multiple(() =>
        {
            Assert.That(problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.HandleUsedAfterDelete }));
            Assert.That(problems.Single().Message, Does.Contain("line 4"), "The message should name the Delete line.");
        });
    }

    [Test]
    public void ASetThatJoinsTwoPatientIndexTablesIsRefused()
    {
        var problems = Check(Script(CreateLine, PatientIndexTableLine("$a2", "$j1"), PatientIndexTableLine("$a3", "$j2"), SetLine(),
            "UsePatientIndexTable AggregateConfiguration:$a1 JoinableCohortAggregateConfiguration:$j1 Inner",
            "UsePatientIndexTable AggregateConfiguration:$a1 JoinableCohortAggregateConfiguration:$j2 Inner"));

        Assert.That(problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.AlreadyJoinsATable }));
    }

    [TestCase("$a2", CohortScriptRefusal.JoinsItself, TestName = "APatientIndexTableThatJoinsItselfIsRefused")]
    [TestCase("$a3", CohortScriptRefusal.JoinFromAPatientIndexTable, TestName = "APatientIndexTableThatJoinsAnotherIsRefused")]
    public void APatientIndexTableJoins(string set, CohortScriptRefusal expected)
    {
        var problems = Check(Script(CreateLine, PatientIndexTableLine("$a2", "$j1"), PatientIndexTableLine("$a3", "$j2"),
            $"UsePatientIndexTable AggregateConfiguration:{set} JoinableCohortAggregateConfiguration:$j1 Inner"));

        Assert.That(problems.Select(p => p.Reason), Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void AParameterDeclaredTwiceOnOneOwnerIsRefused()
    {
        var problems = Check(Script(CreateLine,
            "AddCohortParameter CohortIdentificationConfiguration:$cic1 \"@code\" \"DECLARE @code AS int\" \"1\" null",
            "AddCohortParameter CohortIdentificationConfiguration:$cic1 \"@CODE\" \"DECLARE @CODE AS int\" \"2\" null"));

        Assert.That(problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.ParameterDeclaredTwice }));
    }

    [TestCase("AddCohortFilter AggregateFilterContainer:$fc1 \"Joined\" \"x > ix{$j1}.y\" => $f1", false, TestName = "AFilterMarkerForATableTheSetDoesNotJoinIsRefused")]
    [TestCase("SetText AggregateConfiguration:$a1 HavingSQL \"max(ix{$j1}.y) > 1\"", false, TestName = "AHavingMarkerForATableTheSetDoesNotJoinIsRefused")]
    [TestCase("AddCohortFilter AggregateFilterContainer:$fc1 \"Joined\" \"x > ix{$j1}.y\" => $f1", true, TestName = "AMarkerForAJoinedTableIsAccepted")]
    public void MarkersNeedAJoin(string line, bool joined)
    {
        var lines = new List<string> { CreateLine, PatientIndexTableLine("$a2", "$j1"), SetLine(), "AddNewFilterContainer AggregateConfiguration:$a1 => $fc1", line };
        if (joined)
            lines.Add("UsePatientIndexTable AggregateConfiguration:$a1 JoinableCohortAggregateConfiguration:$j1 Inner");

        var reasons = Check(Script(lines.ToArray())).Select(p => p.Reason);

        Assert.That(reasons, joined ? Is.Empty : Is.EqualTo(new[] { CohortScriptRefusal.MarkerForTableNotJoined }));
    }

    [TestCase("no columns", true, TestName = "AForcedJoinToATableWithNoColumnsIsRefused")]
    [TestCase("no join", true, TestName = "AForcedJoinToATableWithNoJoinIsRefused")]
    [TestCase("joined", false, TestName = "AForcedJoinToAJoinedTableIsAccepted")]
    [TestCase("own table", false, TestName = "AForcedJoinToTheSetsOwnTableIsAccepted")]
    public void ForcedJoins(string table, bool refused)
    {
        var forced = table switch
        {
            "no columns" => new TableInfo(Repository, $"Bare {Guid.NewGuid():N}"),
            "joined" => ForcedJoinTable.JoinedTo(Repository, _identifier),
            "own table" => (TableInfo)_identifier.ColumnInfo.TableInfo,
            _ => (TableInfo)new ColumnInfo(Repository, "id", "int", new TableInfo(Repository, $"Unjoined {Guid.NewGuid():N}")).TableInfo
        };

        var reasons = Check(Script(CreateLine, SetLine(), $"AddForcedJoin AggregateConfiguration:$a1 TableInfo:{forced.ID} \"{forced.Name}\"")).Select(p => p.Reason);

        Assert.That(reasons, refused ? Is.EqualTo(new[] { CohortScriptRefusal.ForcedJoinWithoutJoin }) : Is.Empty);
    }

    // ---- mandatory filters in an authored script

    [Test]
    public void AnAuthoredScriptThatLeavesOutMandatoryFiltersGetsAWarning()
    {
        new ExtractionFilter(Repository, "Adults only", _identifier) { IsMandatory = true, WhereSQL = "age >= 18" }.SaveToDatabase();
        var file = ScriptFile(Script(CreateLine, SetLine()));

        var check = new ExecuteCommandCheckCohortScript(Quiet(), file, authored: true);
        check.Execute();

        Assert.Multiple(() =>
        {
            Assert.That(check.Warnings.Select(w => w.Reason), Is.EqualTo(new[] { CohortScriptRefusal.MandatoryFiltersLeftOut }));
            Assert.That(check.Warnings.Single().Message, Does.Contain("Adults only"), "The warning should name the mandatory filter.");
        });
    }

    [TestCase(false, "false", TestName = "AnExportedScriptGetsNoWarningAboutMandatoryFilters")]
    [TestCase(true, "true", TestName = "AnAuthoredScriptThatImportsMandatoryFiltersGetsNoWarning")]
    public void NoWarningAboutMandatoryFilters(bool authored, string import)
    {
        new ExtractionFilter(Repository, "Adults only", _identifier) { IsMandatory = true, WhereSQL = "age >= 18" }.SaveToDatabase();
        var file = ScriptFile(Script(CreateLine, SetLine(import)));

        var check = new ExecuteCommandCheckCohortScript(Quiet(), file, authored);
        check.Execute();

        Assert.That(check.Warnings, Is.Empty);
    }

    // ---- a set's top filter container

    [Test]
    public void AnotherTopFilterContainerOnASetWithMandatoryFiltersIsRefused()
    {
        new ExtractionFilter(Repository, "Adults only", _identifier) { IsMandatory = true, WhereSQL = "age >= 18" }.SaveToDatabase();

        var problems = Check(Script(CreateLine, SetLine("true"), "AddNewFilterContainer AggregateConfiguration:$a1 => $fc1"));

        Assert.That(problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.TopFilterContainerExists }),
            string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void ASecondTopFilterContainerIsRefused()
    {
        var problems = Check(Script(CreateLine, SetLine(), "AddNewFilterContainer AggregateConfiguration:$a1 => $fc1",
            "AddNewFilterContainer AggregateConfiguration:$a1 => $fc2"));

        Assert.That(problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.TopFilterContainerExists }));
    }

    [Test]
    public void ACatalogueWithoutMandatoryFiltersLetsATrueSetHaveANewTopFilterContainer()
    {
        var problems = Check(Script(CreateLine, SetLine("true"), "AddNewFilterContainer AggregateConfiguration:$a1 => $fc1"));

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void AnAuthoredSetAddsItsOwnFiltersBesideTheMandatoryOnes()
    {
        new ExtractionFilter(Repository, "Adults only", _identifier) { IsMandatory = true, WhereSQL = "age >= 18" }.SaveToDatabase();
        var name = $"Mandatory and own {Guid.NewGuid():N}";

        Build(Script(CreateLine, SetLine("true"), "UseTopFilterContainer AggregateConfiguration:$a1 => $fc1",
            "AddCohortFilter AggregateFilterContainer:$fc1 \"Own\" \"x = 1\" => $f1"), name);

        var set = Built(name).RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively().Single();
        var root = (AggregateFilterContainer)set.RootFilterContainer;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetFilters().Select(f => f.Name), Is.EquivalentTo(new[] { "Adults only", "Own" }),
                "The set's own filter should sit beside the mandatory one.");
            Assert.That(root.Operation, Is.EqualTo(FilterContainerOperation.AND));
            Assert.That(root.GetSubContainers(), Is.Empty);
        });
    }

    // a mandatory filter's parameter comes with its copy, so the set's own filters may use it
    [TestCase("true", false, TestName = "AnOwnFilterMayUseAParameterOfACopiedMandatoryFilter")]
    [TestCase("false", true, TestName = "AParameterOfAMandatoryFilterThatIsNotCopiedIsRefused")]
    public void AParameterOfAMandatoryFilter(string import, bool refused)
    {
        var mandatory = new ExtractionFilter(Repository, "Coded", _identifier) { IsMandatory = true, WhereSQL = "1 = @code" };
        mandatory.SaveToDatabase();
        new ExtractionFilterParameter(Repository, "DECLARE @code AS int", mandatory) { Value = "1" }.SaveToDatabase();
        var script = Script(CreateLine, SetLine(import),
            $"{(import == "true" ? "UseTopFilterContainer" : "AddNewFilterContainer")} AggregateConfiguration:$a1 => $fc1",
            "AddCohortFilter AggregateFilterContainer:$fc1 \"Own\" \"2 > @code\" => $f1");

        var reasons = Check(script).Select(p => p.Reason).ToArray();

        if (refused)
        {
            Assert.That(reasons, Has.Member(CohortScriptRefusal.ParameterNotDeclared));
            return;
        }

        Assert.That(reasons, Is.Empty);
        var name = $"Imported parameter {Guid.NewGuid():N}";
        Build(script, name);
        var set = Built(name).RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively().Single();
        var filters = ((AggregateFilterContainer)set.RootFilterContainer).GetFilters();
        Assert.Multiple(() =>
        {
            Assert.That(filters.Select(f => f.Name), Is.EquivalentTo(new[] { "Coded", "Own" }));
            Assert.That(filters.SelectMany(f => f.GetAllParameters()).Select(p => p.ParameterName), Is.EqualTo(new[] { "@code" }),
                "The copy of the mandatory filter should bring its parameter.");
        });
    }

    // RDMP renames a parameter that an earlier copy already has: the second @code becomes @code2
    [Test]
    public void AnOwnFilterMayUseTheNameRdmpGivesAClashingCopiedParameter()
    {
        foreach (var (name, sql) in new[] { ("First coded", "1 = @code"), ("Second coded", "2 = @code") })
        {
            var mandatory = new ExtractionFilter(Repository, name, _identifier) { IsMandatory = true, WhereSQL = sql };
            mandatory.SaveToDatabase();
            new ExtractionFilterParameter(Repository, "DECLARE @code AS int", mandatory) { Value = "1" }.SaveToDatabase();
        }

        var script = Script(CreateLine, SetLine("true"), "UseTopFilterContainer AggregateConfiguration:$a1 => $fc1",
            "AddCohortFilter AggregateFilterContainer:$fc1 \"Own\" \"3 > @code2\" => $f1");

        var problems = Check(script);
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));

        var built = $"Renamed parameter {Guid.NewGuid():N}";
        Build(script, built);
        var set = Built(built).RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively().Single();
        var parameters = ((AggregateFilterContainer)set.RootFilterContainer).GetFilters().SelectMany(f => f.GetAllParameters());
        Assert.That(parameters.Select(p => p.ParameterName), Is.EquivalentTo(new[] { "@code", "@code2" }),
            "The check should predict the names RDMP gives the copies.");
    }

    // RDMP copies only the parameters a filter's SQL uses, so a saved parameter the SQL dropped is not copied
    [Test]
    public void AnOwnFilterMayNotUseASavedParameterTheCopiedFilterNoLongerUses()
    {
        var mandatory = new ExtractionFilter(Repository, "No longer coded", _identifier) { IsMandatory = true, WhereSQL = "1 = 1" };
        mandatory.SaveToDatabase();
        new ExtractionFilterParameter(Repository, "DECLARE @code AS int", mandatory) { Value = "1" }.SaveToDatabase();

        var problems = Check(Script(CreateLine, SetLine("true"), "UseTopFilterContainer AggregateConfiguration:$a1 => $fc1",
            "AddCohortFilter AggregateFilterContainer:$fc1 \"Own\" \"2 > @code\" => $f1"));

        Assert.That(problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.ParameterNotDeclared }));
    }

    [TestCase("TRUE", TestName = "AnImportFlagInCapitalsIsRefused(TRUE)")]
    [TestCase("False", TestName = "AnImportFlagInCapitalsIsRefused(False)")]
    public void AnImportFlagInCapitalsIsRefused(string import)
    {
        var problems = Check(Script(CreateLine, SetLine(import)));

        Assert.That(problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.WrongArgumentKind }),
            string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void UseTopFilterContainerMakesOneWhenTheSetHasNone()
    {
        var name = $"No mandatory {Guid.NewGuid():N}";

        Build(Script(CreateLine, SetLine("true"), "UseTopFilterContainer AggregateConfiguration:$a1 => $fc1",
            "AddCohortFilter AggregateFilterContainer:$fc1 \"Own\" \"x = 1\" => $f1"), name);

        var set = Built(name).RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively().Single();
        Assert.That(((AggregateFilterContainer)set.RootFilterContainer).GetFilters().Select(f => f.Name), Is.EqualTo(new[] { "Own" }));
    }

    // ---- Top X

    // RDMP 9.3.0 writes no ORDER BY in a cohort set's query, so a column to order by does not fix the patients
    [TestCase("null", TestName = "ATopXWithoutAColumnToOrderByGetsAWarning")]
    [TestCase("AggregateDimension:$a1.{0}", TestName = "ATopXWithAColumnToOrderByGetsAWarningToo")]
    public void TopXWarning(string orderBy)
    {
        var file = ScriptFile(Script(CreateLine, SetLine(), $"SetTopX AggregateConfiguration:$a1 10 {string.Format(orderBy, _column)} Descending"));

        var check = new ExecuteCommandCheckCohortScript(Quiet(), file);
        check.Execute();

        Assert.That(check.Warnings.Select(w => w.Reason), Is.EqualTo(new[] { CohortScriptRefusal.TopXNotFixed }));
    }

    // the warning rests on this: if RDMP starts writing the ORDER BY, this fails and the warning should change
    [TestCase(false, null, null, TestName = "ATopXSetsCohortQueryHasNoOrderBy(plain)")]
    [TestCase(true, null, null, TestName = "ATopXSetsCohortQueryHasNoOrderBy(plain, with a column to order by)")]
    [TestCase(true, "count(*)", null, TestName = "ATopXSetsCohortQueryHasNoOrderBy(count column)")]
    [TestCase(true, null, "count(*) >= 1", TestName = "ATopXSetsCohortQueryHasNoOrderBy(HAVING)")]
    public void ATopXSetsCohortQueryHasNoOrderBy(bool orderByColumn, string countSql, string havingSql)
    {
        var table = _identifier.ColumnInfo.TableInfo;
        table.Server = "localhost";
        table.Database = "TopXDb";
        table.SaveToDatabase();
        var cic = PlainCohort();
        var set = new AggregateConfiguration(Repository, _catalogue, "Set") { CountSQL = countSql, HavingSQL = havingSql };
        set.SaveToDatabase();
        var dimension = new AggregateDimension(Repository, _identifier, set);
        cic.EnsureNamingConvention(set);
        cic.RootCohortAggregateContainer.AddChild(set, 0);
        new AggregateTopX(Repository, set, 10)
        {
            OrderByDimensionIfAny_ID = orderByColumn ? dimension.ID : null,
            OrderByDirection = AggregateTopXOrderByDirection.Descending
        }.SaveToDatabase();

        var sql = new CohortQueryBuilder(cic, null).SQL;

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("TOP 10"));
            Assert.That(sql, Does.Not.Contain("order by").IgnoreCase);
        });
    }

    // ---- the check command

    [Test]
    public void TheCheckAcceptsARenamedCatalogueWhenAskedLikeTheRebuild()
    {
        var file = ScriptFile(Script(CreateLine, SetLine()));
        var oldName = _catalogue.Name;
        _catalogue.Name = $"Renamed {Guid.NewGuid():N}";
        _catalogue.SaveToDatabase();

        var refused = Assert.Throws<CohortScriptProblemsException>(() => new ExecuteCommandCheckCohortScript(Quiet(), file).Execute());
        var accepting = new ExecuteCommandCheckCohortScript(Quiet(), file, acceptRenamedObjects: true);
        accepting.Execute();

        Assert.Multiple(() =>
        {
            Assert.That(refused!.Problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.NameMismatch }));
            Assert.That(accepting.Warnings.Select(w => w.Reason), Is.EqualTo(new[] { CohortScriptRefusal.NameMismatch }));
            Assert.That(accepting.Warnings.Single().Message, Does.Contain(oldName).And.Contain(_catalogue.Name));
        });
    }

    [Test]
    public void TheCheckListsEveryProblemAndBuildsNothing()
    {
        var before = Repository.GetAllObjects<CohortIdentificationConfiguration>().Length;
        var file = ScriptFile(Script(CreateLine, "Frobnicate CohortAggregateContainer:$c1", "Set AggregateConfiguration:$a9 Order 0"));

        var e = Assert.Throws<CohortScriptProblemsException>(() => new ExecuteCommandCheckCohortScript(Quiet(), file).Execute());

        Assert.Multiple(() =>
        {
            Assert.That(e!.Problems.Select(p => p.Reason),
                Is.EquivalentTo(new[] { CohortScriptRefusal.UnknownCommand, CohortScriptRefusal.HandleUsedBeforeCreated }));
            Assert.That(Repository.GetAllObjects<CohortIdentificationConfiguration>(), Has.Length.EqualTo(before),
                "The check should build nothing.");
        });
    }

    [Test]
    public void TheCheckPassesAnExportedCohort()
    {
        var file = ScriptFile(new BuildScriptWriter(CohortWithLinkAndForcedJoin(WhenIHaveA<Project>()), RepositoryLocator).Document());

        Assert.DoesNotThrow(() => new ExecuteCommandCheckCohortScript(Quiet(), file).Execute());
    }

    // ---- the catalogue manifest

    [Test]
    public void TheManifestOfACohortIsTheSameAsTheOneItsExportWrites()
    {
        var cic = CohortWithLinkAndForcedJoin(WhenIHaveA<Project>());
        new ExecuteCommandExportCohortAsScript(Quiet(), cic, _folder).Execute();
        var manifest = new FileInfo(Path.Join(_folder.FullName, "manifest.yaml"));

        new ExecuteCommandExportCatalogueManifest(Quiet(), cic, null, manifest).Execute();

        Assert.That(File.ReadAllText(manifest.FullName),
            Is.EqualTo(File.ReadAllText(Path.Join(_folder.FullName, cic.Name, CohortExportFiles.ManifestFile))));
    }

    [Test]
    public void TwoCohortsWithTheSameCataloguesButDifferentProjectsGetDifferentProjectLists()
    {
        var first = WhenIHaveA<Project>();
        var second = WhenIHaveA<Project>();
        var a = Manifest(CohortWithLinkAndForcedJoin(first));
        var b = Manifest(CohortWithLinkAndForcedJoin(second));

        Assert.Multiple(() =>
        {
            Assert.That(Ids(a, "projects"), Is.EqualTo(new[] { first.ID.ToString() }));
            Assert.That(Ids(b, "projects"), Is.EqualTo(new[] { second.ID.ToString() }));
            Assert.That(Ids(a, "catalogues"), Is.EqualTo(Ids(b, "catalogues")), "Both cohorts use the same catalogue.");
        });
    }

    [Test]
    public void TheManifestGivesEveryIdAndNameTheScriptOfItsCohortUses()
    {
        var cic = CohortWithLinkAndForcedJoin(WhenIHaveA<Project>());
        var manifest = Manifest(cic);
        var script = new BuildScriptWriter(cic, RepositoryLocator).Lines().Select(ScriptLine.Parse).ToArray();

        var listed = new Dictionary<string, Dictionary<string, string>>
        {
            ["Catalogue"] = IdsAndNames(List(manifest, "catalogues")),
            ["TableInfo"] = IdsAndNames(List(manifest, "tables")),
            ["Project"] = IdsAndNames(List(manifest, "projects")),
            ["ExtractionFilter"] = IdsAndNames(List(manifest, "catalogues").SelectMany(c => List(c, "filters")))
        };

        var references = script.Where(l => CohortScriptFormat.Commands[l.Command].Reference != null).ToArray();
        Assert.That(references.Select(l => l.Command).Distinct(),
            Is.EquivalentTo(new[] { "LinkCohortToProject", "AddCatalogueToCohortSet", "AddForcedJoin", "SetFilterSource" }),
            "The cohort should refer to a project, a catalogue, a table and a published filter.");
        Assert.Multiple(() =>
        {
            foreach (var line in references)
            {
                var reference = CohortScriptFormat.Commands[line.Command].Reference;
                CohortScriptFormat.TrySplitTyped(line.Arguments[reference.IdArgument].Text, out var typeName, out var id);
                Assert.That(listed[typeName].GetValueOrDefault(id), Is.EqualTo(line.Arguments[reference.NameArgument].Text),
                    $"The manifest should list {typeName} {id} with the name the script gives.");
            }
        });
    }

    [Test]
    public void TheManifestOfChosenCataloguesListsOnlyThose()
    {
        var other = WhenIHaveA<Catalogue>();
        var manifest = new FileInfo(Path.Join(_folder.FullName, "manifest.yaml"));

        new ExecuteCommandExportCatalogueManifest(Quiet(), null, new[] { _catalogue }, manifest).Execute();

        var ids = Ids(Yaml(File.ReadAllText(manifest.FullName)), "catalogues");
        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.EqualTo(new[] { _catalogue.ID.ToString() }));
            Assert.That(ids, Has.No.Member(other.ID.ToString()));
        });
    }

    [Test]
    public void TheManifestCommandLinesOfTheReadmeWorkOnTheCommandLine()
    {
        var other = WhenIHaveA<Catalogue>();
        var chosen = Path.Join(_folder.FullName, "chosen.yaml");
        var all = Path.Join(_folder.FullName, "all.yaml");
        var cohort = Path.Join(_folder.FullName, "cohort.yaml");
        var cic = PlainCohort();

        var results = new[]
        {
            CommandLine.Run(Quiet(), "ExportCatalogueManifest", "null", $"Catalogue:{_catalogue.ID},{other.ID}", chosen),
            CommandLine.Run(Quiet(), "ExportCatalogueManifest", "null", "null", all),
            CommandLine.Run(Quiet(), "ExportCatalogueManifest", $"CohortIdentificationConfiguration:{cic.ID}", "null", cohort)
        };

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.ImpossibleReason), Is.All.Null);
            Assert.That(Ids(Yaml(File.ReadAllText(chosen)), "catalogues"), Is.EquivalentTo(new[] { _catalogue.ID.ToString(), other.ID.ToString() }),
                "Two ids after one type should choose both catalogues.");
            Assert.That(Ids(Yaml(File.ReadAllText(all)), "catalogues"), Has.Member(_catalogue.ID.ToString()));
            Assert.That(File.ReadAllText(cohort), Is.EqualTo(new CatalogueManifestWriter(RepositoryLocator).ForCohort(cic)));
        });
    }

    [Test]
    public void TheManifestOfEveryCatalogueLeavesOutCataloguesAScriptCannotUse()
    {
        var internalOne = CatalogueWithIdentifier("Internal");
        internalOne.IsInternalDataset = true;
        internalOne.SaveToDatabase();
        var api = CatalogueWithIdentifier("API_Lookup");
        var noIdentifier = WhenIHaveA<Catalogue>();
        var manifest = new FileInfo(Path.Join(_folder.FullName, "manifest.yaml"));

        new ExecuteCommandExportCatalogueManifest(Quiet(), null, null, manifest).Execute();

        var ids = Ids(Yaml(File.ReadAllText(manifest.FullName)), "catalogues");
        Assert.Multiple(() =>
        {
            Assert.That(ids, Has.Member(_catalogue.ID.ToString()));
            Assert.That(ids, Has.No.Member(internalOne.ID.ToString()), "A cohort cannot use an internal dataset.");
            Assert.That(ids, Has.No.Member(api.ID.ToString()), "A set cannot use a catalogue served by an API.");
            Assert.That(ids, Has.No.Member(noIdentifier.ID.ToString()), "A set cannot use a catalogue with no identifier column.");
        });
    }

    private Catalogue CatalogueWithIdentifier(string name)
    {
        var throwaway = WhenIHaveA(Repository, out _, out var column);
        var catalogue = (Catalogue)throwaway.Catalogue;
        catalogue.Name = name;
        catalogue.SaveToDatabase();
        column.IsExtractionIdentifier = true;
        column.SaveToDatabase();
        throwaway.DeleteInDatabase();
        return catalogue;
    }

    // ---- whose export a folder holds

    [Test]
    public void TheExportRefusesAFolderThatHoldsAnotherCohortWithTheSameName()
    {
        var first = PlainCohort();
        var second = new CohortIdentificationConfiguration(Repository, first.Name);
        second.CreateRootContainerIfNotExists();
        new ExecuteCommandExportCohortAsScript(Quiet(), first, _folder).Execute();
        var folder = Path.Join(_folder.FullName, first.Name);
        File.WriteAllText(Path.Join(folder, CohortExportFiles.RequirementFile), "written by hand for the first");

        var e = Assert.Throws<CohortScriptExportException>(() => new ExecuteCommandExportCohortAsScript(Quiet(), second, _folder).Execute());

        Assert.Multiple(() =>
        {
            Assert.That(e!.Reason, Is.EqualTo(CohortScriptRefusal.FolderHoldsAnotherExport));
            Assert.That(e.Message, Does.Contain($"cohort {first.ID}").And.Contain($"cohort {second.ID}"), "The message should name both cohorts.");
            Assert.That(File.ReadAllText(Path.Join(folder, CohortExportFiles.ScriptFile)), Does.Contain($"cohort-id: \"{first.ID}\""),
                "The first cohort's script should stay beside its requirement.");
        });
    }

    [Test]
    public void TheExportReplacesAnEarlierExportOfTheSameCohort()
    {
        var cic = PlainCohort();
        new ExecuteCommandExportCohortAsScript(Quiet(), cic, _folder).Execute();
        cic.Description = "Changed since the first export";
        cic.SaveToDatabase();

        new ExecuteCommandExportCohortAsScript(Quiet(), cic, _folder).Execute();

        Assert.That(File.ReadAllText(Path.Join(_folder.FullName, cic.Name, CohortExportFiles.ScriptFile)), Does.Contain("Changed since the first export"));
    }

    [Test]
    public void TheExportRefusesAFolderWhoseScriptDoesNotSayWhichCohortItCameFrom()
    {
        var cic = PlainCohort();
        var folder = Directory.CreateDirectory(Path.Join(_folder.FullName, cic.Name));
        var authored = Script(CreateLine);
        File.WriteAllText(Path.Join(folder.FullName, CohortExportFiles.ScriptFile), authored);

        var e = Assert.Throws<CohortScriptExportException>(() => new ExecuteCommandExportCohortAsScript(Quiet(), cic, _folder).Execute());

        Assert.Multiple(() =>
        {
            Assert.That(e!.Reason, Is.EqualTo(CohortScriptRefusal.FolderHoldsAnotherExport));
            Assert.That(File.ReadAllText(Path.Join(folder.FullName, CohortExportFiles.ScriptFile)), Is.EqualTo(authored));
        });
    }

    [Test]
    public void TheBulkExportRefusesAListedFolderThatHoldsAnotherCohortsExport()
    {
        var first = PlainCohort();
        var second = PlainCohort();
        Export($"{first.ID} shared\n");
        var script = ScriptIn("shared");

        var e = Assert.Throws<CohortScriptProblemsException>(() => Export($"{second.ID} shared\n"));

        Assert.Multiple(() =>
        {
            Assert.That(e!.Problems.Select(p => (p.FileLine, p.Reason)),
                Is.EqualTo(new[] { (1, CohortScriptRefusal.FolderHoldsAnotherExport) }));
            Assert.That(ScriptIn("shared"), Is.EqualTo(script), "The first cohort's export should be kept.");
        });
    }

    // ---- the export of many cohorts

    [Test]
    public void TheBulkExportWritesEachCohortIntoItsListedFolderAndKeepsItsRequirement()
    {
        var first = PlainCohort();
        var second = PlainCohort();
        var firstFolder = Directory.CreateDirectory(Path.Join(_folder.FullName, "first"));
        File.WriteAllText(Path.Join(firstFolder.FullName, CohortExportFiles.RequirementFile), "written by hand");

        Export($"# cohort, folder\n{first.ID} first\n\n{second.ID}\tsub folder/second\n");

        Assert.Multiple(() =>
        {
            Assert.That(ScriptIn("first"), Does.Contain(first.Name), "The first cohort should be in its own folder.");
            Assert.That(ScriptIn(Path.Join("sub folder", "second")), Does.Contain(second.Name),
                "The second cohort should be in its own folder.");
            Assert.That(File.ReadAllText(Path.Join(firstFolder.FullName, CohortExportFiles.RequirementFile)), Is.EqualTo("written by hand"),
                "A requirement written by hand should stay beside its script.");
            Assert.That(File.Exists(Path.Join(_folder.FullName, "sub folder", "second", CohortExportFiles.RequirementFile)), Is.True,
                "A new folder should get a requirement placeholder.");
        });
    }

    [Test]
    public void TheBulkExportSendsARenamedCohortToItsListedFolder()
    {
        var cic = PlainCohort();
        Export($"{cic.ID} kept-folder\n");
        cic.Name = $"Renamed {Guid.NewGuid():N}";
        cic.SaveToDatabase();

        Export($"{cic.ID} kept-folder\n");

        Assert.Multiple(() =>
        {
            Assert.That(ScriptIn("kept-folder"), Does.Contain(cic.Name), "The listed folder should hold the export under the new name.");
            Assert.That(Directory.GetDirectories(_folder.FullName).Select(Path.GetFileName), Is.EqualTo(new[] { "kept-folder" }),
                "No folder named after the cohort should appear.");
        });
    }

    [TestCase("{0} same\n{1} SAME\n", TestName = "TheBulkExportRefusesTwoCohortsListedForOneFolder")]
    [TestCase("{0} one\n{0} two\n", TestName = "TheBulkExportRefusesACohortListedTwice")]
    [TestCase("{0} one\n987654 two\n", TestName = "TheBulkExportRefusesAnUnknownCohort")]
    [TestCase("{0} one\nnot-a-number two\n", TestName = "TheBulkExportRefusesALineWithoutAnId")]
    public void TheBulkExportRefusesABadListBeforeWritingAnything(string list)
    {
        var first = PlainCohort();
        var second = PlainCohort();

        var e = Assert.Throws<CohortScriptProblemsException>(() => Export(string.Format(list, first.ID, second.ID)));

        Assert.Multiple(() =>
        {
            Assert.That(e!.Problems.Select(p => p.FileLine), Is.EqualTo(new[] { 2 }), "The problem should name the second line of the list.");
            Assert.That(Directory.GetDirectories(_folder.FullName), Is.Empty, "Nothing should be written.");
        });
    }

    [Test]
    public void TheBulkExportRefusesACohortTheFormatCannotCarryBeforeWritingAnything()
    {
        var good = PlainCohort();
        var noRoot = new CohortIdentificationConfiguration(Repository, $"No root {Guid.NewGuid():N}");

        var e = Assert.Throws<CohortScriptProblemsException>(() => Export($"{good.ID} good\n{noRoot.ID} bad\n"));

        Assert.Multiple(() =>
        {
            Assert.That(e!.Problems.Select(p => p.Reason), Is.EqualTo(new[] { CohortScriptRefusal.NoRootContainer }));
            Assert.That(Directory.GetDirectories(_folder.FullName), Is.Empty, "Nothing should be written, not even the good cohort.");
        });
    }

    [TestCase("shared", "alias", true, TestName = "TheBulkExportRefusesTwoFoldersThatALinkMakesOne")]
    [TestCase(@"real/inner", @"linked/inner", true, TestName = "TheBulkExportRefusesTwoFoldersWhoseParentsALinkMakesOne")]
    [TestCase("later", "alias", false, TestName = "TheBulkExportRefusesALinkToAFolderAnEarlierExportWouldMake")]
    public void TheBulkExportFollowsLinks(string realFolder, string viaLink, bool targetExists)
    {
        var real = new DirectoryInfo(Path.Join(_folder.FullName, realFolder));
        if (targetExists)
        {
            real.Create();
            File.WriteAllText(Path.Join(real.FullName, CohortExportFiles.RequirementFile), "written by hand");
        }

        var linkTarget = Path.Join(_folder.FullName, realFolder.Split('/')[0]);
        try
        {
            Directory.CreateSymbolicLink(Path.Join(_folder.FullName, viaLink.Split('/')[0]), linkTarget);
        }
        catch (Exception notAllowed) when (notAllowed is IOException or UnauthorizedAccessException)
        {
            Assert.Ignore($"This machine does not allow folder links: {notAllowed.Message}");
        }

        var first = PlainCohort();
        var second = PlainCohort();

        var e = Assert.Throws<CohortScriptProblemsException>(() => Export($"{first.ID} {realFolder}\n{second.ID} {viaLink}\n"));

        real.Refresh();
        Assert.Multiple(() =>
        {
            Assert.That(e!.Problems.Select(p => p.FileLine), Is.EqualTo(new[] { 2 }), "The problem should name the second line.");
            if (targetExists)
                Assert.That(Directory.GetFiles(real.FullName).Select(Path.GetFileName), Is.EqualTo(new[] { CohortExportFiles.RequirementFile }),
                    "Nothing should be written into the shared folder.");
            else
                Assert.That(real.Exists, Is.False, "Nothing should be written, so the link's target should not be made.");
        });
    }

    // ---- the desktop client

    [Test]
    public void TheCohortMenuHasTheFourEntriesInOneSubmenu()
    {
        var entries = new CohortScriptPluginUserInterface(Quiet()).GetAdditionalRightClickMenuItems(PlainCohort()).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(c => c.GetCommandName()), Is.EquivalentTo(new[]
            {
                CohortScriptPluginUserInterface.ExportLabel, CohortScriptPluginUserInterface.ManifestLabel,
                CohortScriptPluginUserInterface.BuildLabel, CohortScriptPluginUserInterface.CheckLabel
            }));
            Assert.That(entries.Select(c => c.SuggestedCategory), Is.All.EqualTo(CohortScriptPluginUserInterface.MenuCategory));
        });
    }

    // ---- the command reference

    [Test]
    public void TheReferenceDescribesEveryCommandWithAnExample()
    {
        var describe = new ExecuteCommandDescribeCohortScriptCommands(Quiet());
        describe.Execute();

        Assert.Multiple(() =>
        {
            foreach (var name in CohortScriptFormat.Commands.Keys)
            {
                Assert.That(describe.Reference, Does.Contain($"### {name}{Environment.NewLine}"), $"The reference should have a section for {name}.");
                Assert.That(CohortScriptReference.Examples.Any(e => e.StartsWith(name + " ", StringComparison.Ordinal)), Is.True,
                    $"The reference should have an example of {name}.");
            }
        });
    }

    [Test]
    public void EveryArgumentHasADescriptionInTheReference()
    {
        var reference = CohortScriptReference.Markdown(Quiet()).Replace("\r\n", "\n");

        // an argument line without a description ends at its kind: "1. name (text in quotes, or null)"
        var undescribed = reference.Split('\n').Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+\. \w+ \([^)]*\)$"));

        Assert.That(undescribed, Is.Empty);
    }

    [Test]
    public void TheCommittedCommandReferenceIsTheGeneratedOne()
    {
        var committed = File.ReadAllText(Path.Join(TestContext.CurrentContext.TestDirectory, "CohortScript", "COMMANDS.md"));
        var generated = CohortScriptReference.Markdown(Quiet()).Replace("\r\n", "\n");

        // when they differ, the new reference is left beside the test, ready to copy over the committed file
        if (committed.Replace("\r\n", "\n") != generated)
            File.WriteAllText(Path.Join(TestContext.CurrentContext.TestDirectory, "CohortScript", "COMMANDS.generated.md"), generated);
        Assert.That(committed.Replace("\r\n", "\n"), Is.EqualTo(generated),
            "RdmpCohortScript/COMMANDS.md should be the output of DescribeCohortScriptCommands. Copy CohortScript/COMMANDS.generated.md from the test output over it.");
    }

    // ---- helpers

    private ConsoleInputManager Quiet() => new(RepositoryLocator, ThrowImmediatelyCheckNotifier.Quiet) { DisallowInput = true };

    private CohortIdentificationConfiguration PlainCohort()
    {
        var cic = new CohortIdentificationConfiguration(Repository, $"Bulk {Guid.NewGuid():N}");
        cic.CreateRootContainerIfNotExists();
        return cic;
    }

    // a cohort linked to the project, with a set on the catalogue that has a forced join and a filter copied from a
    // published filter
    private CohortIdentificationConfiguration CohortWithLinkAndForcedJoin(Project project)
    {
        var cic = new CohortIdentificationConfiguration(Repository, $"Manifest {Guid.NewGuid():N}");
        cic.CreateRootContainerIfNotExists();
        _ = new ProjectCohortIdentificationConfigurationAssociation(Repository, project, cic);

        var set = new AggregateConfiguration(Repository, _catalogue, "Set") { CountSQL = null };
        set.SaveToDatabase();
        _ = new AggregateDimension(Repository, _identifier, set);
        cic.EnsureNamingConvention(set);
        cic.RootCohortAggregateContainer.AddChild(set, 0);
        Repository.AggregateForcedJoinManager.CreateLinkBetween(set, ForcedJoinTable.JoinedTo(Repository, _identifier));

        var published = new ExtractionFilter(Repository, $"Published {Guid.NewGuid():N}", _identifier) { WhereSQL = "x = 1" };
        published.SaveToDatabase();
        var fc = new AggregateFilterContainer(Repository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        new AggregateFilter(Repository, "Copied", fc) { WhereSQL = "x = 1", ClonedFromExtractionFilter_ID = published.ID }.SaveToDatabase();
        return cic;
    }

    private Dictionary<object, object> Manifest(CohortIdentificationConfiguration cic) =>
        Yaml(new CatalogueManifestWriter(RepositoryLocator).ForCohort(cic));

    private static Dictionary<object, object> Yaml(string text) => new Deserializer().Deserialize<Dictionary<object, object>>(text);

    private static IEnumerable<Dictionary<object, object>> List(Dictionary<object, object> map, string key) =>
        map[key] is List<object> list ? list.Cast<Dictionary<object, object>>() : Enumerable.Empty<Dictionary<object, object>>();

    private static string[] Ids(Dictionary<object, object> map, string key) => List(map, key).Select(e => (string)e["id"]).ToArray();

    private static Dictionary<string, string> IdsAndNames(IEnumerable<Dictionary<object, object>> entries) =>
        entries.ToDictionary(e => (string)e["id"], e => (string)e["name"]);

    private void Export(string list)
    {
        var listFile = Path.Join(_folder.FullName, $"list-{Guid.NewGuid():N}.txt");
        File.WriteAllText(listFile, list);
        try
        {
            new ExecuteCommandExportCohortsAsScripts(Quiet(), new FileInfo(listFile)).Execute();
        }
        finally
        {
            File.Delete(listFile);
        }
    }

    private string ScriptIn(string folder) => File.ReadAllText(Path.Join(_folder.FullName, folder, CohortExportFiles.ScriptFile));

    private FileInfo ScriptFile(string text)
    {
        var file = new FileInfo(Path.Join(_folder.FullName, $"script-{Guid.NewGuid():N}.yaml"));
        File.WriteAllText(file.FullName, text);
        return file;
    }

    private static string Script(params string[] lines) =>
        "cohort-script-format: \"1\"\nCommands:\n" + string.Concat(lines.Select(l => $"  - {YamlText.DoubleQuoted(l)}\n"));

    private void Build(string text, string name) =>
        new ExecuteCommandBuildCohortFromScript(Quiet(), ScriptFile(text), name).Execute();

    private CohortIdentificationConfiguration Built(string name) =>
        Repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", name).Single();

    private List<CohortScriptProblem> Check(string text)
    {
        var problems = new List<CohortScriptProblem>();
        var lines = CohortScriptReader.Read(text, problems);
        new CohortScriptChecker(RepositoryLocator, problems).Check(lines, $"Checked {Guid.NewGuid():N}");
        return problems;
    }
}
