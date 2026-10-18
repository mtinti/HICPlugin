// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdmpLinkedServerCohortSql;
using NUnit.Framework;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Repositories;
using Tests.Common;

namespace HICPluginTests;

/// <summary>Plan 0.7.7: the staged shape, in memory (executed cases are in <see cref="LoopbackDatabaseTests"/>).</summary>
[NonParallelizable]
public sealed class StagedTests : UnitTests
{
    private CohortFixture _f;

    [SetUp]
    protected override void SetUp()
    {
        Repository = new MemoryDataExportRepository();
        _f = new CohortFixture(Repository);
    }

    private static int Index(string text, string value)
    {
        var index = text.IndexOf(value, StringComparison.Ordinal);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), $"missing: {value}");
        return index;
    }

    private (AggregateConfiguration Home, AggregateConfiguration Remote, AggregateConfiguration Pit, AggregateConfiguration Consumer) Mixed()
    {
        var home = _f.Set("A");
        var remote = _f.Set("R", "remote");
        var pit = _f.Set("P", "remote", addToRoot: false);
        var consumer = _f.Set("B");
        var second = _f.Set("C");
        _f.Join(consumer, pit); _f.Join(second, pit);
        return (home, remote, pit, consumer);
    }

    [Test]
    public void BothShapesRenderFromOneRenderAndShareTheHeaderApartFromShapeLines()
    {
        Mixed();
        var rendered = new LinkedServerCohortSqlBuilder().Render(_f.Cic, staged: true);
        var composite = CompositeScriptWriter.Write(rendered).HeaderLines;
        var staged = StagedScriptWriter.Write(rendered).HeaderLines;
        Assert.That(composite[0], Does.Contain("Shape: composite"));
        Assert.That(staged[0], Does.Contain("Shape: staged"));
        Assert.That(composite[1], Is.EqualTo("-- " + RenderedCohort.ReadOnlyLine));
        Assert.That(staged[^1], Is.EqualTo("-- " + RenderedCohort.TempTablesLine));
        Assert.That(composite, Has.One.Contains("rendered 2 times"));
        Assert.That(staged, Has.One.Match("staged once as #rdmp_pit\\d+, used by 2 sets"));
        static bool ShapeSpecific(string l) => l.Contains("Shape:") || l.Contains("READ, DO NOT RUN") || l.Contains("Patient index table") || l.Contains("Temp tables (");
        Assert.That(staged.Where(l => !ShapeSpecific(l)), Is.EqualTo(composite.Where(l => !ShapeSpecific(l))));
        Assert.That(staged.Count, Is.EqualTo(composite.Count));
    }

    [Test]
    public void StagedStructureIsInTheSpecifiedOrder()
    {
        var (home, remote, pit, consumer) = Mixed();
        home.Name = $"cic_{_f.Cic.ID}_O'Neil é"; home.SaveToDatabase();
        _f.Parameter(home, "@p", "1"); _f.Filter(home, "id = @p");
        var result = _f.BuildStaged();
        var sql = result.Sql;
        Assert.That(result.Shape, Is.EqualTo(CohortSqlShape.Staged));
        var jid = pit.JoinableCohortAggregateConfiguration.ID;
        Assert.That(sql, Does.StartWith("USE [HomeDb];"));
        var drops = Index(sql, "IF OBJECT_ID('tempdb..#rdmp_cohort') IS NOT NULL DROP TABLE #rdmp_cohort;");
        foreach (var table in new[] { $"#rdmp_r{remote.ID}", $"#rdmp_pit{jid}", $"#rdmp_s{home.ID}", $"#rdmp_s{consumer.ID}" })
            Assert.That(Index(sql, $"IF OBJECT_ID('tempdb..{table}') IS NOT NULL DROP TABLE {table};"), Is.LessThan(drops));
        var xact = Index(sql, "SET XACT_ABORT ON;");
        Assert.That(drops, Is.LessThan(xact));
        var declare = Index(sql, "DECLARE @p");
        var tryAt = Index(sql, "BEGIN TRY");
        Assert.That(xact, Is.LessThan(declare)); Assert.That(declare, Is.LessThan(tryAt));
        Assert.That(sql.Split("DECLARE @p").Length, Is.EqualTo(2), "declarations once");
        var farRemote = Index(sql, $"INTO #rdmp_r{remote.ID} FROM OPENQUERY(");
        var farPit = Index(sql, $"INTO #rdmp_pit{jid} FROM OPENQUERY(");
        Assert.That(sql.Split($"INTO #rdmp_pit{jid} FROM").Length, Is.EqualTo(2), "one stage per far-side patient index");
        var firstHome = Index(sql, $"INTO #rdmp_s{home.ID}");
        Assert.That(Math.Max(farRemote, farPit), Is.LessThan(firstHome), "far side first");
        foreach (var set in new[] { home, consumer }) Assert.That(sql.Split($"INTO #rdmp_s{set.ID}").Length, Is.EqualTo(2));
        Assert.That(sql, Does.Contain($"#rdmp_pit{jid}").And.Not.Contain("/*LINKED_"));
        Assert.That(home.ToString(), Is.EqualTo("O'Neil é"), "ToString is what the cohort builder tree shows");
        var into = Index(sql, "SELECT * INTO #rdmp_cohort FROM");
        var grid = Index(sql, $"SELECT 1 AS [n], N'O''Neil é' AS [set], {home.ID} AS [id], N'#rdmp_s{home.ID}' AS [table], COUNT(*) AS [rows] FROM #rdmp_s{home.ID}");
        var total = Index(sql, "AS [n], N'Final cohort' AS [set], NULL AS [id], N'#rdmp_cohort' AS [table], COUNT(*) AS [rows] FROM #rdmp_cohort" +
                               Environment.NewLine + "ORDER BY [n]");
        var select = Index(sql, "SELECT * FROM #rdmp_cohort" + Environment.NewLine + ";");
        Assert.That(firstHome, Is.LessThan(into)); Assert.That(into, Is.LessThan(grid)); Assert.That(grid, Is.LessThan(total));
        Assert.That(total, Is.LessThan(select));
        var nl = Environment.NewLine;
        var endTry = Index(sql, "SET XACT_ABORT OFF;" + nl + "SET NOCOUNT OFF;" + nl + "END TRY" + nl + "BEGIN CATCH" + nl +
                                "SET XACT_ABORT OFF;" + nl + "SET NOCOUNT OFF;" + nl + "THROW;" + nl + "END CATCH;");
        Assert.That(select, Is.LessThan(endTry));
        var dropBlock = Index(sql, "-- To clear the temp tables in this window, uncomment and run:");
        Assert.That(sql[dropBlock..].TrimEnd(), Does.EndWith("-- DROP TABLE #rdmp_cohort;"));
        Assert.That(result.ToScript(), Does.Contain("close this query window first"));
    }

    [TestCase("/*x*/ SELECT a FROM t", "FROM t")]
    [TestCase("/* a /* FROM */ b FROM */ SELECT a FROM t", "FROM t")]
    [TestCase("SELECT N'x FROM y' AS a, 'it''s FROM' AS b FROM t", "FROM t")]
    [TestCase("SELECT [FROM], [a]]FROM] FROM t", "FROM t")]
    [TestCase("SELECT \"FROM\", \"a\"\"FROM\" FROM t", "FROM t")]
    [TestCase("SELECT FROMAGE, xFROM, éFROM, FROMé, @FROM, #FROM, $FROM FROM t", "FROM t")]
    [TestCase("SELECT (SELECT 1 FROM x) AS a\r\n-- FROM comment\r\nFROM t", "FROM t")]
    [TestCase("SELECT a\r\nfrom t", "from t")]
    [TestCase("SELECT [a]FROM t", "FROM t")]
    public void ScannerFindsTheFirstTopLevelWholeWordFrom(string sql, string tail) =>
        Assert.That(sql[StagedScriptWriter.FindTopLevelFrom(sql)..], Is.EqualTo(tail));

    [TestCase("SELECT 1 -- FROM t")]
    [TestCase("SELECT (SELECT a FROM t) AS b")]
    [TestCase("SELECT 'FROM' AS x")]
    public void ScannerThrowsWithoutATopLevelFrom(string sql) =>
        Assert.Throws<InvalidOperationException>(() => StagedScriptWriter.FindTopLevelFrom(sql));

    [Test]
    public void IntoIsInsertedBeforeTheCoreFromOnEveryCoreShape()
    {
        var plain = _f.Set("Plain");
        var filtered = _f.Set("Filtered"); _f.Filter(filtered, "id > 1 -- trailing comment");
        var counted = _f.Set("Counted"); counted.CountSQL = "COUNT(*) AS n"; counted.HavingSQL = "COUNT(*) > 1"; counted.SaveToDatabase();
        var pit = _f.Set("P", addToRoot: false); var user = _f.Set("U"); _f.Join(user, pit);
        var sql = _f.BuildStaged().Sql;
        foreach (var set in new[] { plain, filtered, counted, user })
            Assert.That(sql, Does.Match($"INTO #rdmp_s{set.ID}\\r?\\nFROM"), set.Name);
    }

    private LinkedServerRenderException Refused(LinkedServerRenderReason reason)
    {
        Assert.That(_f.Build().Sql, Does.StartWith("USE"), "the composite still renders");
        var ex = Assert.Throws<LinkedServerRenderException>(() => _f.BuildStaged());
        Assert.That(ex!.Reason, Is.EqualTo(reason), ex.Message);
        return ex;
    }

    private AggregateConfiguration CountedWithMarker(int idOrder, int markerOrder, string countSql = "COUNT(*) AS n", string having = null)
    {
        var set = _f.Set("A");
        var id = set.AggregateDimensions.Single(); id.Order = idOrder; id.SaveToDatabase();
        _f.AddDimension(set, "marker", "int", markerOrder);
        set.CountSQL = countSql; set.HavingSQL = having; set.SaveToDatabase();
        return set;
    }

    [TestCase(1, 1, "COUNT(*) AS n", null)]                  // duplicate Order
    [TestCase(1, int.MaxValue, "COUNT(*) AS n", null)]       // ties the count column (v66)
    [TestCase(1, -1, "COUNT(*) AS n", null)]                 // negative Order with a count overflows the comparator
    [TestCase(int.MinValue, 1, null, "COUNT(*) > 0")]        // HAVING only, still overflows (v66)
    public void AmbiguousCoreColumnOrderIsRefused(int idOrder, int markerOrder, string countSql, string having)
    {
        CountedWithMarker(idOrder, markerOrder, countSql, having);
        Assert.That(Refused(LinkedServerRenderReason.StagedColumnOrderAmbiguous).Message, Does.Contain("composite still renders"));
    }

    [TestCase(int.MinValue, -1, null, "COUNT(*) > 0")]       // difference fits in an int
    [TestCase(0, 5, "COUNT(*) AS n", null)]
    public void DeterminedCoreColumnOrderIsAccepted(int idOrder, int markerOrder, string countSql, string having)
    {
        var set = CountedWithMarker(idOrder, markerOrder, countSql, having);
        var expected = countSql == null ? "SELECT [id] AS [id], [marker] AS [marker] FROM #rdmp_s" : "SELECT [id] AS [id], [marker] AS [marker], [n] AS [n] FROM #rdmp_s";
        Assert.That(_f.BuildStaged().Sql, Does.Contain(expected + set.ID));
    }

    [Test]
    public void BranchOneUsesEnumerationOrderAndSelectsEveryDimension()
    {
        var set = CountedWithMarker(9, 1, countSql: null);
        Assert.That(_f.BuildStaged().Sql, Does.Contain($"SELECT [id] AS [id], [marker] AS [marker] FROM #rdmp_s{set.ID}"));
    }

    [Test]
    public void UnaliasedExpressionsAndCaseOnlyDuplicateNamesAreRefused()
    {
        var set = _f.Set("A");
        var id = set.AggregateDimensions.Single(); id.SelectSQL = "CAST([HomeDb].[dbo].[A].[id] AS bigint)"; id.SaveToDatabase();
        set.ClearAllInjections();
        Refused(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias);
        id.Alias = "id"; id.SaveToDatabase(); set.ClearAllInjections();
        Assert.That(_f.BuildStaged().Sql, Does.Contain("SELECT [id] AS [id] FROM"));
        set.CountSQL = "COUNT(*)"; set.SaveToDatabase();
        Refused(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias);
        set.CountSQL = "COUNT(*) AS ID"; set.SaveToDatabase();
        Assert.That(Refused(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias).Message, Does.Contain("ignoring case"));
    }

    [TestCase("CASE WHEN [HomeDb].[dbo].[A].[label] = 'x' THEN 'a' ELSE 'b' END", false)]
    [TestCase("CAST([HomeDb].[dbo].[A].[number] AS varchar(10))", false)]
    [TestCase("CAST([HomeDb].[dbo].[A].[label] COLLATE Latin1_General_CS_AS AS varchar(10))", false)]
    [TestCase("MIN([HomeDb].[dbo].[A].[label] COLLATE Latin1_General_CS_AS)", false)]
    [TestCase("CASE WHEN [HomeDb].[dbo].[A].[label] = 'x' THEN 'a' ELSE 'b' END COLLATE Latin1_General_CI_AS", true)]
    [TestCase("CAST([HomeDb].[dbo].[A].[number] AS varchar(10)) COLLATE Latin1_General_CI_AS", true)]
    [TestCase("MIN([HomeDb].[dbo].[A].[label])", true)]
    [TestCase("CAST([HomeDb].[dbo].[A].[label] AS varchar(10))", true)]
    [TestCase("[HomeDb].[dbo].[A].[label]", true)]
    public void HomeCharacterColumnsNeedAKnownCollation(string expression, bool accepted)
    {
        var set = _f.Set("A");
        var table = CohortFixture.Table(set);
        _f.Dimension(set, table, "label", "varchar(20)", "Latin1_General_CS_AS");
        _f.Dimension(set, table, "number", "int");
        foreach (var extra in set.AggregateDimensions.Where(d => !d.IsExtractionIdentifier).ToArray()) extra.DeleteInDatabase();
        set.ClearAllInjections();
        var marker = _f.AddDimension(set, "marker", "varchar(20)", 1, "Latin1_General_CS_AS");
        marker.SelectSQL = expression; marker.Alias = "marker"; marker.SaveToDatabase(); set.ClearAllInjections();
        if (!accepted)
        {
            Assert.That(Refused(LinkedServerRenderReason.StagedCollationUnknown).Message, Does.Contain("add a trailing COLLATE, or use the composite to read"));
            return;
        }
        var collation = CoreSelectList.TrailingCollation(expression, out _);
        Assert.That(_f.BuildStaged().Sql, Does.Contain(collation == null ? "[marker] AS [marker]" : $"[marker] COLLATE {collation} AS [marker]"));
    }

    [Test]
    public void NumericToCharacterCountIsRefusedNamingTheSetAndColumn()
    {
        var set = _f.Set("A"); set.CountSQL = "CAST(COUNT(*) AS varchar(10)) AS n"; set.SaveToDatabase();
        var message = Refused(LinkedServerRenderReason.StagedCollationUnknown).Message;
        Assert.That(message, Does.Contain(set.ToString()).And.Contain("CAST(COUNT(*) AS varchar(10))"));
        set.CountSQL = "CAST(COUNT(*) AS varchar(10)) COLLATE Latin1_General_CI_AS AS n"; set.SaveToDatabase();
        Assert.That(_f.BuildStaged().Sql, Does.Contain("[n] COLLATE Latin1_General_CI_AS AS [n]"));
    }

    [Test]
    public void TopXCarriesAWarningInBothShapes()
    {
        var set = _f.Set("A"); _f.Set("B");
        _ = new AggregateTopX(Repository, set, 5);
        set.ClearAllInjections();
        foreach (var result in new[] { _f.Build(), _f.BuildStaged() })
            Assert.That(result.ToScript(), Does.Contain($"-- WARNING: {set} uses TOP 5 without ORDER BY; membership is not deterministic"));
    }

    [Test]
    public void WholeRemoteSetDeclaresEveryDimension()
    {
        _f.Set("A");
        var remote = _f.Set("R", "remote");
        _f.AddDimension(remote, "marker", "int", 1);
        var script = _f.Build().ToScript();
        Assert.That(script, Does.Contain("declared columns: [id] int, [marker] int"));
        Assert.That(_f.BuildStaged().Sql, Does.Contain($"SELECT [id], [marker] INTO #rdmp_r{remote.ID} FROM OPENQUERY("));
    }

    // Plan 0.7.9.2. Core collapses each filter onto one line, so the literal reaches the leaf text through HAVING (or a
    // dimension's SQL), which core writes as typed.
    [Test]
    public void CompositeDoesNotIndentInsideNestedLeaves()
    {
        _f.Set("A");
        var nested = _f.Set("B", addToRoot: false);
        var literal = "'a" + Environment.NewLine + "b'";
        nested.HavingSQL = "MIN(label) = " + literal; nested.SaveToDatabase();
        var sub = new CohortAggregateContainer(Repository, SetOperation.UNION);
        _f.Root.AddChild(sub); sub.AddChild(nested, 1);
        Assert.That(new Rdmp.Core.QueryBuilding.CohortQueryBuilder(_f.Cic, null).SQL, Does.Contain("'a" + Environment.NewLine + "\tb'"),
            "core's own composite indents the literal");
        Assert.That(_f.Build().Sql, Does.Contain(literal));
        Assert.That(_f.BuildStaged().Sql, Does.Contain(literal));
    }

    // Review of 1.0.2: a leading COUNT(, MIN( or CAST( is not the whole expression unless its parenthesis closes at the end.
    [TestCase("COUNT(*) * 1.0 / NULLIF(COUNT(*) + 1, 0)")]
    [TestCase("CAST([HomeDb].[dbo].[P].[id] AS varchar(5)) + CAST([HomeDb].[dbo].[P].[id] AS varchar(5))")]
    [TestCase("MIN([HomeDb].[dbo].[P].[id]) + MAX([HomeDb].[dbo].[P].[id])")]
    [TestCase("COUNT(*) + 1")]
    public void CompoundExpressionsAreNotDescribedAndFallBackToGuarded(string expression)
    {
        var home = _f.Set("A");
        var pit = _f.Set("P", "remote", addToRoot: false);
        pit.CountSQL = expression + " AS ratio"; pit.SaveToDatabase();
        _f.Join(home, pit);
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => new SqlServerResultColumnResolver(pit).Resolve())!.Reason,
            Is.EqualTo(LinkedServerRenderReason.RemoteResultTypeUnknown));
        Assert.That(_f.Build().Warnings, Has.One.Contains("guarded form used because"));
    }

    [TestCase("COUNT(*)", "int")]
    [TestCase("COUNT( DISTINCT [HomeDb].[dbo].[P].[id] )", "int")]
    [TestCase("CAST(COUNT(*) * 1.0 / 3 AS decimal(10,4))", "decimal(10,4)")]
    [TestCase("MAX([HomeDb].[dbo].[P].[id])", "int")]
    public void SingleCallsAreStillDescribed(string expression, string type)
    {
        var pit = _f.Set("P", "remote", addToRoot: false);
        pit.CountSQL = expression + " AS result"; pit.SaveToDatabase();
        Assert.That(new SqlServerResultColumnResolver(pit).Resolve().Last(), Is.EqualTo(new ResultColumn("result", type)));
    }

    [TestCase("[patient id]")] [TestCase("\"patient id\"")] [TestCase("  [patient id]  ")] [TestCase("patient_id")]
    public void AliasesAreNamedAsSqlServerNamesThem(string alias)
    {
        var set = _f.Set("A"); var remote = _f.Set("R", "remote");
        foreach (var s in new[] { set, remote })
        {
            var id = s.AggregateDimensions.Single(); id.Alias = alias; id.SaveToDatabase(); s.ClearAllInjections();
        }
        var name = alias.Trim().Trim('[', ']', '"');
        Assert.That(CoreSelectList.NameOf(set.AggregateDimensions.Single()), Is.EqualTo(name));
        var sql = _f.BuildStaged().Sql;
        Assert.That(sql, Does.Contain($"SELECT [{name}] AS [{name}] FROM #rdmp_s{set.ID}").And.Contain($"SELECT [{name}] AS [{name}] FROM #rdmp_r{remote.ID}"));
        Assert.That(sql, Does.Not.Contain("[[").And.Not.Contain("[\""));
    }

    [Test]
    public void QuotedAliasesThatDifferOnlyByCaseAreRefused()
    {
        var set = _f.Set("A");
        var id = set.AggregateDimensions.Single(); id.Alias = "[Chi]"; id.SaveToDatabase(); set.ClearAllInjections();
        set.CountSQL = "COUNT(*) AS \"CHI\""; set.SaveToDatabase();
        Assert.That(Refused(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias).Message, Does.Contain("ignoring case"));
    }

    // Review of v68: a line comment ends at a bare carriage return too, so the '(' after it is code.
    [Test]
    public void LineCommentEndingAtCarriageReturnDoesNotHideTheRestOfTheExpression()
    {
        var expression = "COUNT(* --comment\r) * 1.0 / NULLIF(\nCOUNT(*) + 1, 0)";
        Assert.That(SqlText.IsSingleCall(expression), Is.False);
        Assert.That(SqlText.IsSingleCall("COUNT(* --comment )\r)"), Is.True);
        var pit = _f.Set("P", "remote", addToRoot: false);
        pit.CountSQL = expression + " AS ratio"; pit.SaveToDatabase();
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => new SqlServerResultColumnResolver(pit).Resolve())!.Reason,
            Is.EqualTo(LinkedServerRenderReason.RemoteResultTypeUnknown));
    }

    [TestCase("'patient_id'", "patient_id")] [TestCase("'it''s id'", "it's id")] [TestCase(" 'patient id' ", "patient id")]
    public void SingleQuotedAliasesAreUnquoted(string alias, string name)
    {
        var set = _f.Set("A");
        var id = set.AggregateDimensions.Single(); id.Alias = alias; id.SaveToDatabase(); set.ClearAllInjections();
        Assert.That(CoreSelectList.NameOf(set.AggregateDimensions.Single()), Is.EqualTo(name));
        var quoted = "[" + name + "]";
        Assert.That(_f.BuildStaged().Sql, Does.Contain($"SELECT {quoted} AS {quoted} FROM #rdmp_s{set.ID}"));
    }

    // The grid lists sets in cohort tree order (the order of the arithmetic), not render order.
    [Test]
    public void GridFollowsTheCohortTree()
    {
        var a = _f.Set("A"); var b = _f.Set("B", addToRoot: false); var c = _f.Set("C");
        var sub = new CohortAggregateContainer(Repository, SetOperation.UNION);
        _f.Root.AddChild(sub); sub.AddChild(b, 1);
        a.Order = 9; a.SaveToDatabase(); // render order would put A after the others
        var expected = LinkedServerCohortSqlBuilder.TreeOrder(_f.Root).Select(s => s.ID).ToArray();
        var sql = _f.BuildStaged().Sql;
        var positions = expected.Select(id => Index(sql, $"AS [id], N'#rdmp_s{id}' AS [table]")).ToArray();
        Assert.That(positions, Is.Ordered);
        Assert.That(sql.IndexOf("SELECT 1 AS [n]", StringComparison.Ordinal), Is.LessThan(positions[0] + 1));
        Assert.That(sql, Does.Contain($"SELECT {expected.Length + 1} AS [n], N'Final cohort'"));
    }
}
