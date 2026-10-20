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
/// <summary>
/// Tests of the script for inspection, without a database. "RDMP's query builder" (<c>CohortQueryBuilder</c>) is what the
/// plugin is compared with. The home server is where the script runs; a set on any other server is read through a linked
/// server with OPENQUERY.
/// </summary>
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
    public void OneServerCohortMatchesRdmpQueryIncludingParameterRenamesAndNestedExcept()
    {
        var a = _f.Set("A");
        var b = _f.Set("B");
        _f.Parameter(a, "@p", "1"); _f.Parameter(b, "@p", "2");
        _f.Filter(a, "id = @p"); _f.Filter(b, "id = @p");
        var sub = new CohortAggregateContainer(Repository, SetOperation.EXCEPT);
        _f.Root.RemoveChild(b); sub.AddChild(b, 1); _f.Root.AddChild(sub);
        var result = _f.Build();
        var rdmpQuery = new CohortQueryBuilder(_f.Cic, null);
        var rdmpSql = rdmpQuery.SQL;
        // The SQL equals RDMP's apart from indentation. RDMP indents every line of a nested set, including the inside of a
        // string that spans several lines. The plugin puts each set's SQL in after indenting, so it does not.
        Assert.That(result.Sql[(result.Sql.IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length)..].Replace("\t", ""),
            Is.EqualTo(rdmpSql.Replace("\t", "")));
        Assert.That(result.Sql, Does.Contain("@p_2"));
        Assert.That(result.ToScript(), Does.Contain("It reads from no linked server.").And.Not.Contain("OPENQUERY"));
    }

    [Test]
    public void RemoteSetSqlIsNotRenamedOrIndentedByTheHomeQuery()
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
        // In the USE form the SQL sits in an EXEC string inside the OPENQUERY string, so each quote is doubled once more.
        Assert.That(after, Does.Contain("$1 $& O''''Neil"));
        Assert.That(WrapperTests.DecodeArgument(after), Does.Contain("O''Neil"));
    }

    [Test]
    public void RemotePatientIndexTableReturnsItsCountAndKeepsItsParametersOnTheLinkedServer()
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
    public void EachRemoteQueryUsesItsOwnDatabaseAndASharedPatientIndexTableIsRepeated()
    {
        var a = _f.Set("A"); var b = _f.Set("B");
        var pit = _f.Set("Pit", "remote", "DbOne", addToRoot: false);
        _f.Join(a, pit); _f.Join(b, pit);
        _f.Set("R", "remote", "DbTwo"); _f.Set("S", "other", "DbOne");
        var text = _f.Build().ToScript();
        Assert.That(text, Does.Contain("switches to database DbOne with USE").And.Contain("switches to database DbTwo with USE"));
        Assert.That(text, Does.Contain("other: the query switches to database DbOne with USE"), "every remote query uses the USE form");
        Assert.That(text, Does.Not.Contain("runs in the login's default database"));
        Assert.That(text, Does.Contain("repeats the query for patient index table Pit 2 times"));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(text, "OPENQUERY\\(").Count, Is.EqualTo(4));
    }

    [TestCase(null)] [TestCase("")] [TestCase("   ")]
    public void BuilderRefusesATableWithNoServerBeforeRdmpWritesAnySql(string server)
    {
        var set = _f.Set("A", server);
        var ex = Assert.Throws<LinkedServerRenderException>(() => _f.Build());
        Assert.That(ex!.Reason, Is.EqualTo(LinkedServerRenderReason.ServerUnknown));
        Assert.That(ex.Message, Does.Contain("Set the table's server in RDMP."));
        // The remedy works: with a server, the cohort builds.
        var table = CohortFixture.Table(set); table.Server = "home"; table.SaveToDatabase();
        Assert.That(_f.Build().Sql, Does.StartWith("USE"));
    }

    // A refusal raised while wrapping a remote set names that set once, shows no reason code, and ends with one full stop.
    [Test]
    public void WrappedRefusalNamesItsSetOnceAndHidesTheReasonCode()
    {
        _f.Set("A");
        var remote = _f.Set("RemoteSet", "remote");
        var parameter = _f.Parameter(remote, "@rdmp_linked_db", "1"); var filter = _f.Filter(remote, "id = @rdmp_linked_db");
        var ex = Assert.Throws<LinkedServerRenderException>(() => _f.Build());
        Assert.That(ex!.Reason, Is.EqualTo(LinkedServerRenderReason.ReservedParameterName));
        Assert.That(ex.Message, Is.EqualTo($"Cannot write linked-server SQL for set {remote}. The plugin uses the name @rdmp_linked_db " +
                                           "for its own database check. Rename your parameter."));
        Assert.That(ex.Detail, Does.Not.Contain(remote.ToString()), "the detail does not repeat the set");
        // The remedy works: after renaming the parameter, the cohort builds.
        parameter.ParameterSQL = "DECLARE @mine AS int"; parameter.SaveToDatabase();
        filter.WhereSQL = "id = @mine"; filter.SaveToDatabase();
        remote.ClearAllInjections();
        Assert.That(_f.Build().Sql, Does.Contain("OPENQUERY"));
    }

    [Test]
    public void ServerNamesThatDifferOnlyByCaseOrSpacesAreTheSameServer()
    {
        _f.Set("A", "HOME"); _f.Set("B", "home ");
        Assert.That(_f.Build().Sql, Does.Not.Contain("OPENQUERY"));
    }

    [Test]
    public void HomeDatabaseNameIsQuotedAndAMissingDatabaseIsRefused()
    {
        var a = _f.Set("A", database: "my db]x");
        Assert.That(_f.Build().Sql, Does.StartWith("USE [my db]]x];"));
        var recorded = CohortFixture.Table(a).Database;
        CohortFixture.Table(a).Database = null;
        var ex = Assert.Throws<LinkedServerRenderException>(() => _f.Build());
        Assert.That(ex!.Reason, Is.EqualTo(LinkedServerRenderReason.HomeDatabaseUnknown));
        Assert.That(ex.Message, Does.Contain("Synchronise the table in RDMP so its database is recorded."));
        // The remedy works: with its database recorded, the cohort builds.
        CohortFixture.Table(a).Database = recorded;
        Assert.That(_f.Build().Sql, Does.StartWith("USE [my db]]x];"));
    }

    [TestCase(true)] [TestCase(false)]
    public void BuilderRefusesACohortWhoseOnlySetIsDisabled(bool strict)
    {
        var previous = UserSettings.StrictValidationForCohortBuilderContainers;
        try
        {
            UserSettings.StrictValidationForCohortBuilderContainers = strict;
            var a = _f.Set("A"); a.IsDisabled = true; a.SaveToDatabase();
            var ex = Assert.Throws<LinkedServerRenderException>(() => _f.Build());
            Assert.That(ex!.Reason, Is.EqualTo(LinkedServerRenderReason.EmptyContainer));
            Assert.That(ex.Message, Does.Contain("Add or enable a set, or disable the empty container."));
            // The remedy works: with the set enabled, the cohort builds.
            a.IsDisabled = false; a.SaveToDatabase();
            Assert.That(_f.Build().Sql, Does.StartWith("USE"));
        }
        finally { UserSettings.StrictValidationForCohortBuilderContainers = previous; }
    }

    [Test]
    public void BuilderRefusesARemoteSetWhosePatientIndexTableIsOnAnotherServer()
    {
        _f.Set("A"); var remote = _f.Set("R", "remote");
        var pit = _f.Set("Pit", "home", addToRoot: false); _f.Join(remote, pit);
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => _f.Build())!.Reason, Is.EqualTo(LinkedServerRenderReason.JoinSpansServers));
        // The remedy works: with both on the same linked server, the cohort builds.
        var table = CohortFixture.Table(pit); table.Server = "remote"; table.SaveToDatabase();
        Assert.That(_f.Build().Sql, Does.Contain("OPENQUERY"));
    }

    [TestCase(true)] [TestCase(false)]
    public void BuilderRefusesAServerThatIsNotSqlServer(bool home)
    {
        var a = _f.Set("A"); var r = _f.Set("R", "remote");
        CohortFixture.Table(home ? a : r).DatabaseType = DatabaseType.PostgreSql;
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => _f.Build())!.Reason,
            Is.EqualTo(home ? LinkedServerRenderReason.UnsupportedHomeDatabaseType : LinkedServerRenderReason.UnsupportedRemoteDatabaseType));
    }

    [Test]
    public void ARemoteSetInOneDatabaseStillUsesTheUseForm()
    {
        // Link logins often open in master, where the checked form always fails. So every remote query uses the USE form,
        // however many databases the linked server serves.
        var f = new CohortFixture(new MemoryDataExportRepository());
        f.Set("Home");
        f.Set("Far", server: "link1", database: "OnlyDb");
        var result = f.Build();
        Assert.That(result.Sql, Does.Contain("EXEC(''USE [OnlyDb];"));
        Assert.That(result.Sql, Does.Not.Contain("@rdmp_linked_db"));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void ACountWhoseTypeThePluginCannotWorkOutUsesTheCheckedFormWithAWarning()
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        f.Set("Home");
        var far = f.Set("Far", server: "link1", database: "OnlyDb");
        far.CountSQL = "DATEDIFF(day, id, GETDATE()) AS age"; far.SaveToDatabase(); // the plugin cannot work out the type of DATEDIFF
        var result = f.Build();
        Assert.That(result.Sql, Does.Contain("@rdmp_linked_db"));
        Assert.That(result.Sql, Does.Not.Contain("EXEC(''USE [OnlyDb];"));
        Assert.That(result.Warnings.Single(), Does.StartWith($"Set {far}: the plugin could not describe its columns. The plugin cannot describe DATEDIFF")
            .And.Contain("which must be OnlyDb"));
        Assert.That(result.ToScript(), Does.Contain($"link1: the query for set {far} runs in the login's default database, which must be OnlyDb."));
        // The remedy works: with an explicit CAST, the plugin knows the type and uses the USE form again.
        far.CountSQL = "CAST(DATEDIFF(day, id, GETDATE()) AS int) AS age"; far.SaveToDatabase();
        var fixedResult = f.Build();
        Assert.That(fixedResult.Sql, Does.Contain("EXEC(''USE [OnlyDb];"));
        Assert.That(fixedResult.Warnings, Is.Empty);
    }

    // A remote count without an alias makes the plugin use the checked form, and the warning says so; with an alias the USE
    // form comes back.
    [Test]
    public void ARemoteCountWithoutAnAliasUsesTheCheckedFormUntilItGetsOne()
    {
        _f.Set("Home");
        var far = _f.Set("Far", "link1", "OnlyDb");
        far.CountSQL = "COUNT(*)"; far.SaveToDatabase();
        Assert.That(_f.Build().Warnings.Single(), Does.Contain("The count column COUNT(*) has no alias. Give it one, for example COUNT(*) AS n."));
        far.CountSQL = "COUNT(*) AS n"; far.SaveToDatabase();
        var result = _f.Build();
        Assert.That(result.Warnings, Is.Empty);
        Assert.That(result.Sql, Does.Contain("EXEC(''USE [OnlyDb];"));
    }
}
