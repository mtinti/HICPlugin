// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdmpLinkedServerCohortSql;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.QueryBuilding.Parameters;
using Rdmp.Core.Repositories;
using Tests.Common;

namespace HICPluginTests;

public sealed class WrapperTests : UnitTests
{
    private CohortFixture _f;
    private TableInfo _table;
    [SetUp]
    protected override void SetUp()
    {
        Repository = new MemoryDataExportRepository();
        _f = new CohortFixture(Repository);
        _table = CohortFixture.Table(_f.Set("R", "sql.example\\inst,1433]x", "it's"));
    }

    internal static string DecodeArgument(string sql)
    {
        var match = Regex.Match(sql, @"OPENQUERY\(\[(?:[^\]]|\]\])*\], N?'(?<value>(?:[^']|'')*)'\)", RegexOptions.Singleline);
        Assert.That(match.Success, Is.True, sql);
        return match.Groups["value"].Value.Replace("''", "'");
    }

    private OpenQueryFragment Wrap(string sql, RemoteBatchForm form = RemoteBatchForm.Guarded,
        params ISqlParameter[] parameters) => SqlServerOpenQueryWrapper.Wrap(new CohortQueryBuilderDependencySql(sql, new ParameterManager(parameters)),
        _table, form, new[] { new ResultColumn("id", "int") });

    [Test]
    public void NamesAndApostrophesSurviveBothForms()
    {
        const string sql = "SELECT 1 AS id /* O'Neil $1 $& */";
        var guarded = Wrap(sql).Sql;
        Assert.That(guarded, Does.StartWith("OPENQUERY([sql.example\\inst,1433]]x], '"));
        Assert.That(DecodeArgument(guarded), Does.Contain("N'it''s'").And.EndsWith(sql));
        var exec = DecodeArgument(Wrap(sql, RemoteBatchForm.UseDatabase).Sql);
        Assert.That(exec, Does.Contain("USE [it''s];").And.Contain("O''Neil").And.Contain("WITH RESULT SETS (([id] int))"));
    }

    [TestCase(false, RemoteBatchForm.Guarded)] [TestCase(true, RemoteBatchForm.Guarded)]
    [TestCase(false, RemoteBatchForm.UseDatabase)] [TestCase(true, RemoteBatchForm.UseDatabase)]
    public void LimitCountsDecodedArgumentIncludingInnerEscapes(bool unicode, RemoteBatchForm form)
    {
        var seed = "SELECT 1 AS id /*" + new string('\'', 200) + (unicode ? "é" : "") + "*/";
        var limit = unicode ? 4000 : 8000;
        var length = DecodeArgument(Wrap(seed, form).Sql).Length;
        var atLimit = seed.Replace("*/", new string('x', limit - length) + "*/");
        var sql = Wrap(atLimit, form).Sql;
        Assert.That(DecodeArgument(sql).Length, Is.EqualTo(limit));
        Assert.That(sql, Does.Contain(unicode ? ", N'" : ", '"));
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => Wrap(atLimit + " ", form))!.Reason,
            Is.EqualTo(LinkedServerRenderReason.BatchTooLong));
    }

    [Test]
    public void ParametersArePrunedClosedAndSorted()
    {
        var end = _f.Parameter(_f.Cic, "@end", "@middle + 1");
        var middle = _f.Parameter(_f.Cic, "@middle", "@start + 1");
        var start = _f.Parameter(_f.Cic, "@start", "3");
        var unused = _f.Parameter(_f.Cic, "@unused", "INSERT INTO @unused VALUES(1)", "TABLE(id int)");
        var arg = DecodeArgument(Wrap("SELECT @end AS id", parameters: [end, middle, start, unused]).Sql);
        Assert.That(arg.IndexOf("DECLARE @start", StringComparison.Ordinal), Is.LessThan(arg.IndexOf("DECLARE @middle", StringComparison.Ordinal)));
        Assert.That(arg.IndexOf("DECLARE @middle", StringComparison.Ordinal), Is.LessThan(arg.IndexOf("DECLARE @end", StringComparison.Ordinal)));
        Assert.That(arg, Does.Not.Contain("@unused"));
    }

    [Test]
    public void CommentsLiteralsAndTableSelfReferencesDoNotCreateCycles()
    {
        var a = _f.Parameter(_f.Cic, "@a", "1 /* nested /* @b */ @b */");
        var b = _f.Parameter(_f.Cic, "@b", "2 -- @a\n");
        var ids = _f.Parameter(_f.Cic, "@ids", "INSERT INTO @ids VALUES (1)", "TABLE(id int)");
        var text = _f.Parameter(_f.Cic, "@text", "N'@text '' @a'", "nvarchar(40)");
        var fragment = Wrap("SELECT @a + @b AS id FROM @ids WHERE @text IS NOT NULL", parameters: [a, b, ids, text]);
        Assert.That(fragment.Warnings, Is.Empty);
        Assert.That(Regex.Matches(DecodeArgument(fragment.Sql), "DECLARE @ids").Count, Is.EqualTo(1));
    }

    [TestCase("id>@p")] [TestCase("id<@p")] [TestCase("id%@p=0")]
    [TestCase("id&@p=0")] [TestCase("id|@p=0")] [TestCase("id^@p=0")]
    public void AdjacentOperatorsDoNotHideParameterDependencies(string predicate)
    {
        var p = _f.Parameter(_f.Cic, "@p", "CASE WHEN 5>@dependency THEN 1 ELSE 2 END");
        var dependency = _f.Parameter(_f.Cic, "@dependency", "3");
        var sql = "SELECT id FROM t WHERE " + predicate;
        var argument = DecodeArgument(Wrap(sql, parameters: [p, dependency]).Sql);
        Assert.That(argument, Does.Contain("DECLARE @p").And.Contain("DECLARE @dependency").And.EndsWith(sql));
        Assert.That(argument.IndexOf("DECLARE @dependency", StringComparison.Ordinal), Is.LessThan(argument.IndexOf("DECLARE @p", StringComparison.Ordinal)));
    }

    [Test]
    public void RealCycleWarnsInCoreOrderAndReservedNameRefuses()
    {
        var a = _f.Parameter(_f.Cic, "@a", "@b"); var b = _f.Parameter(_f.Cic, "@b", "@a");
        var fragment = Wrap("SELECT @a AS id", parameters: [a, b]);
        Assert.That(fragment.Warnings.Single(), Does.Contain("@a").And.Contain("@b"));
        var reserved = _f.Parameter(_f.Cic, "@RDMP_LINKED_DB", "1");
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => Wrap("SELECT 1 AS id", parameters: [reserved]))!.Reason,
            Is.EqualTo(LinkedServerRenderReason.ReservedParameterName));
    }
}
