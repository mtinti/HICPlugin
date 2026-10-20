// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdmpLinkedServerCohortSql;
using NUnit.Framework;
using Rdmp.Core.Repositories;
using Tests.Common;

namespace HICPluginTests;

/// <summary>Tests of how the plugin works out the name, SQL type and collation of each column a remote set returns.</summary>
public sealed class ResultColumnTests : UnitTests
{
    [TestCase("COUNT(*)", "int", "int")]
    [TestCase("count_big(*)", "int", "bigint")]
    [TestCase("Min(id)", "datetime2", "datetime2(7)")]
    [TestCase("MAX([Db].dbo.[T].[id])", "int", "int")]
    [TestCase("SUM(id)", "smallint", "int")]
    [TestCase("AVG(id)", "decimal(5,2)", "decimal(38,6)")]
    [TestCase("SUM(id)", "decimal(5,2)", "decimal(38,2)")]
    [TestCase("AVG(id)", "decimal(20,10)", "decimal(38,10)")]
    [TestCase("SUM(id)", "smallmoney", "money")]
    [TestCase("AVG(id)", "real", "float")]
    [TestCase("CAST(AVG(id) AS decimal(18,4))", "int", "decimal(18,4)")]
    public void DeclaredTypesFollowSqlServerRules(string expression, string source, string expected)
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", source, addToRoot: false);
        pit.CountSQL = expression + " AS result";
        Assert.That(new SqlServerResultColumnResolver(pit).Resolve().Single(c => c.Name == "result").SqlType, Is.EqualTo(expected));
    }

    [TestCase("MIN(id)")]
    [TestCase("CAST(MIN(id) AS nvarchar(10))")]
    [TestCase("CAST([Db]..[T].[id] AS nvarchar(10))")]
    public void TextResultsKeepTheSourceColumnsCollation(string expression)
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", "varchar(10)", "Latin1_General_CS_AS", addToRoot: false);
        pit.CountSQL = expression + " AS result";
        Assert.That(new SqlServerResultColumnResolver(pit).Resolve().Single(c => c.Name == "result").Collation, Is.EqualTo("Latin1_General_CS_AS"));
    }

    [TestCase("DATEDIFF(day,id,GETDATE())", "datetime")]
    [TestCase("CAST(id AS varchar(10))", "int")]
    [TestCase("SUM(id)", "bit")]
    public void ResolverRefusesExpressionsWhoseTypeOrCollationItCannotWorkOut(string expression, string source)
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", source, addToRoot: false);
        pit.CountSQL = expression + " AS result";
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => new SqlServerResultColumnResolver(pit).Resolve())!.Reason,
            Is.EqualTo(LinkedServerRenderReason.RemoteResultTypeUnknown));
    }

    [Test]
    public void WithACountTheDeclaredColumnsFollowOrderWithTheCountLast()
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", "int", addToRoot: false);
        var identifier = pit.AggregateDimensions.Single();
        identifier.Order = 2; identifier.SaveToDatabase();
        var first = f.AddDimension(pit, "d_first", "datetime", order: 0);
        var last = f.AddDimension(pit, "d_last", "datetime", order: 1);
        pit.CountSQL = "COUNT(*) AS n";
        var declared = new SqlServerResultColumnResolver(pit).Resolve().Select(c => c.Name).ToArray();
        // With a count, RDMP sorts the columns by Order and puts the count last.
        Assert.That(declared, Is.EqualTo(new[] { "d_first", "d_last", identifier.GetRuntimeName(), "n" }));
    }

    [Test]
    public void ResolverRefusesTwoColumnsWithTheSameOrder()
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", "int", addToRoot: false);
        pit.AggregateDimensions.Single().Order = 5; pit.AggregateDimensions.Single().SaveToDatabase();
        f.AddDimension(pit, "d", "datetime", order: 5);
        pit.CountSQL = "COUNT(*) AS n"; // a count makes RDMP sort by Order; without a count or HAVING it keeps the listed order
        var ex = Assert.Throws<LinkedServerRenderException>(() => new SqlServerResultColumnResolver(pit).Resolve());
        Assert.That(ex!.Reason, Is.EqualTo(LinkedServerRenderReason.RemoteResultTypeUnknown));
        Assert.That(ex.Message, Does.Contain("Two columns have Order 5, so RDMP may write them in either order. Give each column a different Order."));
        // The remedy works: with different Orders, the columns can be declared.
        pit.AggregateDimensions.Single(d => d.IsExtractionIdentifier).Order = 4; pit.AggregateDimensions.Single(d => d.IsExtractionIdentifier).SaveToDatabase();
        pit.ClearAllInjections();
        Assert.That(new SqlServerResultColumnResolver(pit).Resolve().Select(c => c.Name).Last(), Is.EqualTo("n"));
    }

    [Test]
    public void WithoutCountOrHavingTheDeclaredColumnsKeepTheListedOrder()
    {
        // Without a count or HAVING, RDMP writes "SELECT id, other" in the order the dimensions are listed, and ignores Order.
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", "int", addToRoot: false);
        var identifier = pit.AggregateDimensions.Single();
        identifier.Order = 9; identifier.SaveToDatabase();
        var other = f.AddDimension(pit, "other", "int", order: 0);
        var enumeration = pit.AggregateDimensions.Select(d => d.GetRuntimeName()).ToArray();
        var declared = new SqlServerResultColumnResolver(pit).Resolve().Select(c => c.Name).ToArray();
        Assert.That(declared, Is.EqualTo(enumeration));
        Assert.That(declared, Does.Contain(other.GetRuntimeName()));
    }
}
