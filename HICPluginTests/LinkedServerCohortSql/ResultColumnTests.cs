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
    public void TypesFollowSqlServerRules(string expression, string source, string expected)
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", source, addToRoot: false);
        pit.CountSQL = expression + " AS result";
        Assert.That(new SqlServerResultColumnResolver(pit).Resolve().Single(c => c.Name == "result").SqlType, Is.EqualTo(expected));
    }

    [TestCase("MIN(id)")]
    [TestCase("CAST(MIN(id) AS nvarchar(10))")]
    [TestCase("CAST([Db]..[T].[id] AS nvarchar(10))")]
    public void CharacterResultsPreserveSourceCollation(string expression)
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", "varchar(10)", "Latin1_General_CS_AS", addToRoot: false);
        pit.CountSQL = expression + " AS result";
        Assert.That(new SqlServerResultColumnResolver(pit).Resolve().Single(c => c.Name == "result").Collation, Is.EqualTo("Latin1_General_CS_AS"));
    }

    [TestCase("DATEDIFF(day,id,GETDATE())", "datetime")]
    [TestCase("CAST(id AS varchar(10))", "int")]
    [TestCase("SUM(id)", "bit")]
    public void UnsupportedOrUnknownCollationRefuses(string expression, string source)
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", source, addToRoot: false);
        pit.CountSQL = expression + " AS result";
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => new SqlServerResultColumnResolver(pit).Resolve())!.Reason,
            Is.EqualTo(LinkedServerRenderReason.RemoteResultTypeUnknown));
    }

    [Test]
    public void DeclaredColumnsFollowDimensionOrderNotRowOrder()
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", "int", addToRoot: false);
        var identifier = pit.AggregateDimensions.Single();
        identifier.Order = 2; identifier.SaveToDatabase();
        var first = f.AddDimension(pit, "d_first", "datetime", order: 0);
        var last = f.AddDimension(pit, "d_last", "datetime", order: 1);
        pit.CountSQL = "COUNT(*) AS n";
        var declared = new SqlServerResultColumnResolver(pit).Resolve().Select(c => c.Name).ToArray();
        // core's AggregateBuilder sorts the select list by Order with the count column last
        Assert.That(declared, Is.EqualTo(new[] { "d_first", "d_last", identifier.GetRuntimeName(), "n" }));
    }

    [Test]
    public void DimensionsSharingAnOrderAreRefused()
    {
        var f = new CohortFixture(new MemoryDataExportRepository());
        var pit = f.Set("T", "remote", "Db", "int", addToRoot: false);
        pit.AggregateDimensions.Single().Order = 5; pit.AggregateDimensions.Single().SaveToDatabase();
        f.AddDimension(pit, "d", "datetime", order: 5);
        pit.CountSQL = "COUNT(*) AS n"; // the sorted branch; without CountSQL/HAVING core keeps enumeration order
        var ex = Assert.Throws<LinkedServerRenderException>(() => new SqlServerResultColumnResolver(pit).Resolve());
        Assert.That(ex!.Reason, Is.EqualTo(LinkedServerRenderReason.RemoteResultTypeUnknown));
        Assert.That(ex.Message, Does.Contain("share Order 5"));
    }

    [Test]
    public void WithoutCountOrHavingDeclarationFollowsEnumerationOrderNotOrder()
    {
        // CohortQueryBuilderHelper preassembles "SELECT id, other" from AggregateDimensions in
        // enumeration order when there is no CountSQL/HAVING; Order must not be applied there.
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
