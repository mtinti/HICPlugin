// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FAnsi;
using NUnit.Framework;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Settings;
using Tests.Common;

namespace Rdmp.LinkedServerCohortSql.Tests;

[NonParallelizable]
public sealed class RoutingAndMetadataTests : UnitTests
{
    private CohortFixture _f;
    [SetUp]
    protected override void SetUp() { Repository = new MemoryDataExportRepository(); _f = new CohortFixture(Repository); }

    [Test]
    public void ApiSetWithNoTablesRefusesBeforeCoreRendering()
    {
        var catalogue = new Catalogue(Repository, "API:Example");
        // Use the released host's prefix rather than assuming API implementations are installed.
        var prefix = Rdmp.Core.CohortCreation.Execution.PluginCohortCompiler.ApiPrefix;
        catalogue.Name = prefix + "Example"; catalogue.SaveToDatabase();
        var set = new AggregateConfiguration(Repository, catalogue, "Api"); _f.Root.AddChild(set, 1);
        AssertReason(LinkedServerRenderReason.ApiSetRequiresCache);
    }

    [Test]
    public void DisabledContainerDoesNotChooseHomeOrExposeItsApiSet()
    {
        var ignored = _f.Set("A", "ignored"); var home = _f.Set("B", "home");
        var sub = new CohortAggregateContainer(Repository, SetOperation.UNION) { IsDisabled = true };
        sub.SaveToDatabase(); _f.Root.RemoveChild(ignored); sub.AddChild(ignored, 0); _f.Root.AddChild(sub);
        Assert.That(_f.Build().HomeServerName, Is.EqualTo("home"));
        Assert.That(_f.Build().TablesTouched, Is.EquivalentTo(new[] { CohortFixture.Table(home) }));
    }

    [TestCase(false)] [TestCase(true)]
    public void EmptyChildObeysStrictSetting(bool strict)
    {
        _f.Set("A"); _f.Root.AddChild(new CohortAggregateContainer(Repository, SetOperation.UNION));
        var previous = UserSettings.StrictValidationForCohortBuilderContainers;
        try
        {
            UserSettings.StrictValidationForCohortBuilderContainers = strict;
            if (strict) AssertReason(LinkedServerRenderReason.EmptyContainer); else Assert.DoesNotThrow(() => _f.Build());
        }
        finally { UserSettings.StrictValidationForCohortBuilderContainers = previous; }
    }

    [Test]
    public void MixedServersWithinSetRefuses()
    {
        var a = _f.Set("A");
        var other = new TableInfo(Repository, "[HomeDb].dbo.Other") { Server = "other", Database = "HomeDb", DatabaseType = DatabaseType.MicrosoftSQLServer };
        _f.Dimension(a, other, "extra", "int");
        AssertReason(LinkedServerRenderReason.SetSpansServers);
    }

    [Test]
    public void LowestTableIdChoosesHomeDatabaseAndHeaderListsOtherHomeDatabases()
    {
        var set = _f.Set("A", database: "First");
        var other = new TableInfo(Repository, "[Other].dbo.Second")
            { Server = "home", Database = "Other", DatabaseType = DatabaseType.MicrosoftSQLServer };
        other.SaveToDatabase();
        var extra = _f.Dimension(set, other, "extra", "int");
        _ = new JoinInfo(Repository, extra.ColumnInfo, set.AggregateDimensions.Single(d => d.IsExtractionIdentifier).ColumnInfo,
            ExtractionJoinType.Inner, null);
        var result = _f.Build();
        Assert.That(result.HomeDatabaseName, Is.EqualTo("First"));
        Assert.That(result.Sql, Does.StartWith("USE [First];"));
        Assert.That(result.ToScript(), Does.Contain("Home databases: First, Other; USE names First"));
    }

    [Test]
    public void RemoteSetAndSameServerPitInDifferentDatabasesRefuse()
    {
        _f.Set("A"); var r = _f.Set("R", "remote", "One"); var pit = _f.Set("Pit", "remote", "Two", addToRoot: false);
        _f.Join(r, pit); AssertReason(LinkedServerRenderReason.MultipleRemoteDatabases);
    }

    [Test]
    public void MissingRemoteDatabaseHasTypedError()
    {
        _f.Set("A"); var r = _f.Set("R", "remote"); CohortFixture.Table(r).Database = null;
        AssertReason(LinkedServerRenderReason.MultipleRemoteDatabases);
    }

    [Test]
    public void MultipleAndNestedPatientIndexesRefuseMalformedCatalogue()
    {
        var home = _f.Set("A"); var p = _f.Set("P", "remote", addToRoot: false); var q = _f.Set("Q", "remote", addToRoot: false);
        var first = _f.Join(home, p);
        var second = new JoinableCohortAggregateConfiguration(Repository, _f.Cic, q);
        Repository.InsertAndHydrate(new JoinableCohortAggregateConfigurationUse(), new Dictionary<string, object>
        { ["AggregateConfiguration_ID"] = home.ID, ["JoinableCohortAggregateConfiguration_ID"] = second.ID });
        home.ClearAllInjections(); AssertReason(LinkedServerRenderReason.MultiplePatientIndexTables);
        var extra = home.PatientIndexJoinablesUsed.Single(j => j.ID != first.ID); extra.DeleteInDatabase(); home.ClearAllInjections();
        Repository.InsertAndHydrate(new JoinableCohortAggregateConfigurationUse(), new Dictionary<string, object>
        { ["AggregateConfiguration_ID"] = p.ID, ["JoinableCohortAggregateConfiguration_ID"] = second.ID });
        p.ClearAllInjections(); AssertReason(LinkedServerRenderReason.NestedPatientIndexTable);
    }

    [TestCase(false)] [TestCase(true)]
    public void UnaliasedRemoteIdentifierRefuses(bool patientIndex)
    {
        var home = _f.Set("A"); var r = _f.Set("R", "remote", addToRoot: !patientIndex);
        if (patientIndex) _f.Join(home, r);
        var id = r.AggregateDimensions.Single();
        id.SelectSQL = "CAST(" + id.SelectSQL + " AS bigint)"; id.SaveToDatabase(); r.ClearAllInjections();
        AssertReason(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias);
    }

    [Test]
    public void UnaliasedCountRefusesWhenCollationProjectionNeedsItsName()
    {
        var home = _f.Set("A", idType: "varchar(10)", collation: "Latin1_General_CI_AS");
        var pit = _f.Set("P", "remote", idType: "varchar(10)", collation: "Latin1_General_CS_AS", addToRoot: false);
        pit.CountSQL = "COUNT(*)"; pit.SaveToDatabase(); _f.Join(home, pit);
        AssertReason(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias);
    }

    [Test]
    public void GlobalsOverrideRemoteLocalsAndCycleWarningsReachHeader()
    {
        _f.Set("A"); var r = _f.Set("R", "remote");
        _f.Parameter(r, "@p", "1"); _f.Parameter(_f.Cic, "@p", "9"); _f.Filter(r, "id=@p");
        Assert.That(WrapperTests.DecodeArgument(_f.Build().Sql), Does.Contain("SET @p=9;").And.Not.Contain("SET @p=1;"));
        _f.Parameter(r, "@a", "@b"); _f.Parameter(r, "@b", "@a"); _f.Filter(r, "id=@a");
        var result = _f.Build(); Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(result.ToScript(), Does.Contain("-- WARNING: Set"));
    }

    [TestCase("unknown", null)] [TestCase(null, null)] [TestCase("int", null)]
    [TestCase("varchar(10)", "Latin1_General_CS_AS")]
    public void CollationIsOnlyAddedWhenBothPlainCharacterTypesAreKnown(string type, string expected)
    {
        var a = _f.Set("A", idType: "varchar(10)", collation: "Latin1_General_CS_AS");
        var b = _f.Set("B", "remote", idType: type, collation: "Latin1_General_CI_AS");
        Assert.That(CollationClause.Decide(a.AggregateDimensions.Single(), b.AggregateDimensions.Single()),
            Is.EqualTo(expected == null ? null : " COLLATE " + expected));
    }

    [Test]
    public void DuplicateColumnMetadataCollapsesButConflictingTypeRefuses()
    {
        var pit = _f.Set("T", "remote", "Db", addToRoot: false); var table = CohortFixture.Table(pit);
        var duplicate = new ColumnInfo(Repository, "[Db]..[T].[id]", "int", table);
        pit.CountSQL = "MIN(id) AS n";
        Assert.That(new SqlServerResultColumnResolver(pit).Resolve().Last().SqlType, Is.EqualTo("int"));
        duplicate.Data_type = "bigint"; duplicate.SaveToDatabase();
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => new SqlServerResultColumnResolver(pit).Resolve())!.Reason,
            Is.EqualTo(LinkedServerRenderReason.RemoteResultTypeUnknown));
    }

    [Test]
    public void ExplicitCollationMakesNumericToCharacterCastDescribable()
    {
        var pit = _f.Set("T", "remote", addToRoot: false);
        pit.CountSQL = "CAST(MIN(id) AS varchar(10)) COLLATE Latin1_General_CS_AS AS n";
        Assert.That(new SqlServerResultColumnResolver(pit).Resolve().Last(), Is.EqualTo(new ResultColumn("n", "varchar(10)", "Latin1_General_CS_AS")));
    }

    private void AssertReason(LinkedServerRenderReason reason) => Assert.That(Assert.Throws<LinkedServerRenderException>(() => _f.Build())!.Reason, Is.EqualTo(reason));
}
