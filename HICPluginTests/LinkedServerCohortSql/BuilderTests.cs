// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdmpLinkedServerCohortSql;
using FAnsi;
using NUnit.Framework;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Settings;
using Tests.Common;

namespace HICPluginTests;

[NonParallelizable]
public sealed class BuilderTests : UnitTests
{
    private CohortFixture _f;
    [SetUp]
    protected override void SetUp()
    {
        Repository = new MemoryDataExportRepository();
        _f = new CohortFixture(Repository);
    }

    [Test]
    public void SingleServerMatchesCoreIncludingParameterRenamesAndNestedExcept()
    {
        var a = _f.Set("A");
        var b = _f.Set("B");
        _f.Parameter(a, "@p", "1"); _f.Parameter(b, "@p", "2");
        _f.Filter(a, "id = @p"); _f.Filter(b, "id = @p");
        var sub = new CohortAggregateContainer(Repository, SetOperation.EXCEPT);
        _f.Root.RemoveChild(b); sub.AddChild(b, 1); _f.Root.AddChild(sub);
        var result = _f.Build();
        var core = new CohortQueryBuilder(_f.Cic, null);
        var coreSql = core.SQL;
        // Equal to core apart from indentation: core tabs every line of a nested set, which also tabs the inside of a
        // multi-line string literal; the composite splices leaf text untabbed (plan 0.7.9.2).
        Assert.That(result.Sql[(result.Sql.IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length)..].Replace("\t", ""),
            Is.EqualTo(coreSql.Replace("\t", "")));
        Assert.That(result.Sql, Does.Contain("@p_2"));
        Assert.That(result.ToScript(), Does.Contain("Links used: none").And.Not.Contain("OPENQUERY"));
    }

    [Test]
    public void ShapeAIsOpaqueToOuterRenamesAndIndentation()
    {
        var a = _f.Set("A"); var b = _f.Set("B"); var remote = _f.Set("R", "remote");
        _f.Parameter(a, "@p", "1"); _f.Parameter(b, "@p", "2"); _f.Parameter(remote, "@p", "3");
        _f.Filter(a, "id=@p"); _f.Filter(b, "id=@p"); _f.Filter(remote, "id=@p -- $1 $& O'Neil\n");
        var before = _f.Build().Sql;
        var sub = new CohortAggregateContainer(Repository, SetOperation.INTERSECT);
        _f.Root.RemoveChild(remote); sub.AddChild(remote, 3); _f.Root.AddChild(sub);
        var after = _f.Build().Sql;
        Assert.That(WrapperTests.DecodeArgument(after), Is.EqualTo(WrapperTests.DecodeArgument(before)));
        Assert.That(WrapperTests.DecodeArgument(after), Does.Contain("SET @p=3;").And.Not.Contain("@p_2"));
        // UseDatabase form: the inner EXEC literal doubles quotes once more than the OPENQUERY literal
        Assert.That(after, Does.Contain("$1 $& O''''Neil"));
        Assert.That(WrapperTests.DecodeArgument(after), Does.Contain("O''Neil"));
    }

    [Test]
    public void ShapeBExposesCountAndDoesNotImportPitParameters()
    {
        var home = _f.Set("A", idType: "varchar(10)", collation: "Latin1_General_CI_AS");
        var pit = _f.Set("Pit", "remote", idType: "varchar(10)", collation: "Latin1_General_CS_AS", addToRoot: false);
        pit.CountSQL = "COUNT(*) AS n"; pit.SaveToDatabase();
        _f.Parameter(pit, "@pit", "2"); _f.Filter(pit, "id <> @pit");
        var join = _f.Join(home, pit); _f.Filter(home, join.GetJoinTableAlias() + ".n > 0");
        var sql = _f.Build().Sql;
        Assert.That(sql, Does.Contain("[id] COLLATE Latin1_General_CI_AS AS [id], [n]"));
        Assert.That(sql[..sql.IndexOf("OPENQUERY", StringComparison.Ordinal)], Does.Not.Contain("DECLARE @pit"));
        Assert.That(WrapperTests.DecodeArgument(sql), Does.Contain("DECLARE @pit"));
    }

    [Test]
    public void MultipleDatabasesChooseExecPerLinkAndSharedPitGetsIndependentBlocks()
    {
        var a = _f.Set("A"); var b = _f.Set("B");
        var pit = _f.Set("Pit", "remote", "DbOne", addToRoot: false);
        _f.Join(a, pit); _f.Join(b, pit);
        _f.Set("R", "remote", "DbTwo"); _f.Set("S", "other", "DbOne");
        var text = _f.Build().ToScript();
        Assert.That(text, Does.Contain("EXEC USE [DbOne]").And.Contain("EXEC USE [DbTwo]"));
        Assert.That(text, Does.Contain("other: EXEC USE [DbOne]"), "every remote block takes the USE form (v56)");
        Assert.That(text, Does.Not.Contain("guarded form"));
        Assert.That(text, Does.Contain("rendered 2 times"));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(text, "OPENQUERY\\(").Count, Is.EqualTo(4));
    }

    [TestCase(null)] [TestCase("")] [TestCase("   ")]
    public void MissingServerRefusesBeforeCore(string server)
    {
        _f.Set("A", server);
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => _f.Build())!.Reason, Is.EqualTo(LinkedServerRenderReason.SetSpansServers));
    }

    [Test]
    public void CaseAndWhitespaceDoNotCreateSelfLink()
    {
        _f.Set("A", "HOME"); _f.Set("B", "home ");
        Assert.That(_f.Build().Sql, Does.Not.Contain("OPENQUERY"));
    }

    [Test]
    public void HomeDatabaseIsQuotedAndMissingDatabaseHasTypedError()
    {
        var a = _f.Set("A", database: "my db]x");
        Assert.That(_f.Build().Sql, Does.StartWith("USE [my db]]x];"));
        CohortFixture.Table(a).Database = null;
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => _f.Build())!.Reason, Is.EqualTo(LinkedServerRenderReason.HomeDatabaseUnknown));
    }

    [TestCase(true)] [TestCase(false)]
    public void DisabledChildrenAlwaysProduceEmptyError(bool strict)
    {
        var previous = UserSettings.StrictValidationForCohortBuilderContainers;
        try
        {
            UserSettings.StrictValidationForCohortBuilderContainers = strict;
            var a = _f.Set("A"); a.IsDisabled = true;
            Assert.That(Assert.Throws<LinkedServerRenderException>(() => _f.Build())!.Reason, Is.EqualTo(LinkedServerRenderReason.EmptyContainer));
        }
        finally { UserSettings.StrictValidationForCohortBuilderContainers = previous; }
    }

    [Test]
    public void CrossServerPitOnRemoteSetRefuses()
    {
        _f.Set("A"); var remote = _f.Set("R", "remote");
        var pit = _f.Set("Pit", "home", addToRoot: false); _f.Join(remote, pit);
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => _f.Build())!.Reason, Is.EqualTo(LinkedServerRenderReason.JoinSpansServers));
    }

    [TestCase(true)] [TestCase(false)]
    public void NonSqlServerHasTypedError(bool home)
    {
        var a = _f.Set("A"); var r = _f.Set("R", "remote");
        CohortFixture.Table(home ? a : r).DatabaseType = DatabaseType.PostgreSql;
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => _f.Build())!.Reason,
            Is.EqualTo(home ? LinkedServerRenderReason.UnsupportedHomeDatabaseType : LinkedServerRenderReason.UnsupportedRemoteDatabaseType));
    }

    [Test]
    public void SingleFarSideDatabaseStillUsesTheUseDatabaseForm()
    {
        // First production run: the link login's default database was master, so the guarded form can never
        // run there. Every remote block takes the EXEC USE form regardless of how many databases a link carries.
        var f = new CohortFixture(new MemoryDataExportRepository());
        f.Set("Home");
        f.Set("Far", server: "link1", database: "OnlyDb");
        var result = f.Build();
        Assert.That(result.Sql, Does.Contain("EXEC(''USE [OnlyDb];"));
        Assert.That(result.Sql, Does.Not.Contain("@rdmp_linked_db"));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void UnderivableResultTypeFallsBackToGuardedFormWithWarning()
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        f.Set("Home");
        var far = f.Set("Far", server: "link1", database: "OnlyDb");
        far.CountSQL = "DATEDIFF(day, id, GETDATE()) AS age"; far.SaveToDatabase(); // not in the type grammar
        var result = f.Build();
        Assert.That(result.Sql, Does.Contain("@rdmp_linked_db"));
        Assert.That(result.Sql, Does.Not.Contain("EXEC(''USE [OnlyDb];"));
        Assert.That(result.Warnings.Single(), Does.Contain("guarded form used because").And.Contain("OnlyDb"));
    }
}
