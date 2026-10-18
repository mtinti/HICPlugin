// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdmpLinkedServerCohortSql;
using System.Data.Common;
using System.Data;
using FAnsi.Discovery;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Rdmp.Core.Curation;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.QueryBuilding.Parameters;
using Rdmp.Core.Repositories;
using Rdmp.Core.Databases;
using Rdmp.Core.MapsDirectlyToDatabaseTable.Versioning;
using Rdmp.Core.QueryCaching.Aggregation;
using Rdmp.Core.QueryCaching.Aggregation.Arguments;
using Rdmp.Core.ReusableLibraryCode.Checks;
using Rdmp.Core.ReusableLibraryCode.DataAccess;
using Tests.Common;
using YamlDotNet.Serialization;

namespace HICPluginTests;

[NonParallelizable]
[Category("Database")]
public sealed class LoopbackDatabaseTests : DatabaseTests
{
    private DiscoveredServer _server;
    private DbConnection _connection;
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private string _dbA, _dbB, _link, _wrongLink;
    private readonly List<string> _createdDatabases = new();
    private readonly List<string> _createdLinks = new();

    protected override void OneTimeSetUp()
    {
        // The base constructor supplies fresh catalogue/data-export repositories. Its full Startup
        // additionally requires Logging/DQE databases, which this SQL-only plugin never uses.
        // SQL login when TestDatabases.txt names one (container CI), else Windows authentication (LocalDB CI).
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = TestDatabaseSettings.ServerName, InitialCatalog = "master", TrustServerCertificate = true
        };
        if (IntegratedSecurity) builder.IntegratedSecurity = true;
        else { builder.UserID = TestDatabaseSettings.Username; builder.Password = TestDatabaseSettings.Password; }
        _server = new DiscoveredServer(builder);
        _connection = _server.GetConnection(); _connection.Open();
        _dbA = "RDMP_LINKED_TEST_" + _suffix;
        _dbB = "RDMP_LINKED_TEST_it's_" + _suffix;
        _link = "RDMP_LINKED_" + _suffix;
        _wrongLink = "RDMP_LINKED_WRONG_" + _suffix;
        foreach (var db in new[] { _dbA, _dbB })
        {
            Execute("CREATE DATABASE " + Quote(db)); _createdDatabases.Add(db);
            Execute($"""
                USE {Quote(db)};
                CREATE TABLE dbo.Patients(id int NOT NULL PRIMARY KEY, label varchar(20) COLLATE Latin1_General_CS_AS, cutoff decimal(8,3));
                INSERT INTO dbo.Patients VALUES (1,'a',1.018),(2,'A',2),(3,'O''Neil',3);
                CREATE TABLE dbo.Contacts(id int NOT NULL, amount decimal(5,2), status varchar(10) COLLATE Latin1_General_CS_AS, eventDate datetime2,
                    smallValue smallint, realValue real, moneyValue smallmoney);
                INSERT INTO dbo.Contacts VALUES (1,1.01,'a','2020-01-01',20000,1.25,200000),
                    (1,1.02,'a','2020-01-02',20000,1.75,200000),(2,2.00,'A','2020-02-01',1,2,1);
                CREATE TABLE dbo.CaseHome(id varchar(10) COLLATE Latin1_General_CI_AS);
                INSERT INTO dbo.CaseHome VALUES ('a'),('A');
                CREATE TABLE dbo.CaseRemote(id varchar(10) COLLATE Latin1_General_CS_AS);
                INSERT INTO dbo.CaseRemote VALUES ('a'),('A');
                CREATE TABLE dbo.MarkA(id int NOT NULL, marker int NOT NULL);
                INSERT INTO dbo.MarkA VALUES (1,2);
                CREATE TABLE dbo.MarkB(id int NOT NULL, marker int NOT NULL);
                INSERT INTO dbo.MarkB VALUES (1,1);
                CREATE TABLE dbo.Texts(id int NOT NULL, t varchar(20) COLLATE Latin1_General_CS_AS);
                INSERT INTO dbo.Texts VALUES (1, 'a' + {NewLineSql} + 'b'), (2, 'ab');
                CREATE TABLE dbo.CaseTuples(id int NOT NULL, label varchar(20) COLLATE Latin1_General_CS_AS);
                INSERT INTO dbo.CaseTuples VALUES (1,'A'),(2,'a');
                USE master;
                """);
        }
        try
        {
            CreateLink(_link, _dbA);
            CreateLink(_wrongLink, "master");
            Assert.That(Query($"SELECT n FROM OPENQUERY({Quote(_link)}, 'SELECT 1 AS n')").Single(), Is.EqualTo(1));
        }
        catch (DbException ex)
        {
            if (Environment.GetEnvironmentVariable("RDMP_LINKED_REQUIRE_LINK") == "1") Assert.Fail("Required loopback link unavailable: " + ex.Message);
            Assert.Inconclusive("Loopback link needs ALTER ANY LINKED SERVER and a SQL login: " + ex.Message);
        }
    }

    // Environment.NewLine as T-SQL, for the multi-line literal fixture (plan 0.7.9.2).
    private static string NewLineSql => Environment.NewLine == "\r\n" ? "CHAR(13) + CHAR(10)" : "CHAR(10)";

    private void CreateLink(string name, string database)
    {
        Execute("EXEC master.dbo.sp_addlinkedserver @server=@name,@srvproduct=N'',@provider=N'MSOLEDBSQL',@datasrc=@source,@catalog=@catalog;",
            ("@name", name), ("@source", Environment.GetEnvironmentVariable("RDMP_LINKED_DATA_SOURCE") ?? "localhost"), ("@catalog", database));
        _createdLinks.Add(name);
        // With Windows authentication the link uses the caller's own login; with a SQL login, that login.
        if (IntegratedSecurity)
            Execute("EXEC master.dbo.sp_addlinkedsrvlogin @rmtsrvname=@name,@useself=N'True',@locallogin=NULL;", ("@name", name));
        else
            Execute("EXEC master.dbo.sp_addlinkedsrvlogin @rmtsrvname=@name,@useself=N'False',@locallogin=NULL,@rmtuser=@user,@rmtpassword=@password;",
                ("@name", name), ("@user", TestDatabaseSettings.Username), ("@password", TestDatabaseSettings.Password));
    }

    private static bool IntegratedSecurity => string.IsNullOrWhiteSpace(TestDatabaseSettings.Username);

    [OneTimeTearDown]
    public void RemoveFixture()
    {
        if (_connection == null) return;
        try
        {
            Execute("USE master;");
            foreach (var link in _createdLinks) Execute("EXEC master.dbo.sp_dropserver @server=@name,@droplogins='droplogins';", ("@name", link));
            foreach (var database in _createdDatabases) Execute($"ALTER DATABASE {Quote(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Quote(database)};");
        }
        finally { _connection.Dispose(); }
    }

    private AggregateConfiguration ImportSet(CohortFixture fixture, string table, string database = null, string server = null,
        bool addToRoot = true, string identifier = "id")
    {
        database ??= _dbA;
        var importer = new TableInfoImporter(CatalogueRepository, _server.ExpectDatabase(database).ExpectTable(table));
        importer.DoImport(out var info, out var columns);
        info.Server = server ?? TestDatabaseSettings.ServerName; info.SaveToDatabase();
        var forward = new ForwardEngineerCatalogue(info, columns);
        forward.ExecuteForwardEngineering(out var catalogue, out _, out var extraction);
        var ei = extraction.Single(e => e.ColumnInfo.GetRuntimeName() == identifier);
        ei.IsExtractionIdentifier = true; ei.SaveToDatabase();
        var aggregate = new AggregateConfiguration(CatalogueRepository, catalogue, $"cic_{fixture.Cic.ID}_{table}_{Guid.NewGuid():N}") { CountSQL = null };
        aggregate.SaveToDatabase();
        _ = new AggregateDimension(CatalogueRepository, ei, aggregate);
        if (addToRoot) fixture.Root.AddChild(aggregate, fixture.Root.GetOrderedContents().Count() + 1);
        return aggregate;
    }

    /// <summary>Both shapes executed (plan 0.7.7 p): each returns <paramref name="expected"/>. Returns the composite's rows.</summary>
    private List<object> AssertShapes(CohortFixture fixture, params object[] expected)
    {
        var rendered = fixture.Build();
        var actual = Query(rendered.ToScript());
        Assert.That(actual, Is.EquivalentTo(expected), "composite");
        Assert.That(QueryStaged(fixture.BuildStaged().ToScript()), Is.EquivalentTo(expected), "staged");
        return actual;
    }

    private void AssertMatchesCore(CohortFixture fixture, params object[] expected)
    {
        var rendered = fixture.Build();
        var actual = AssertShapes(fixture, expected);
        var tables = rendered.TablesTouched.Select(t => (Table: t, Server: t.Server)).ToArray();
        try
        {
            foreach (var (table, _) in tables) { table.Server = TestDatabaseSettings.ServerName; table.SaveToDatabase(); }
            var reference = new CohortQueryBuilder(fixture.Cic, null).SQL;
            Assert.That(actual, Is.EquivalentTo(Query($"USE {Quote(rendered.HomeDatabaseName)};" + reference)));
        }
        finally { foreach (var (table, server) in tables) { table.Server = server; table.SaveToDatabase(); } }
    }

    [TestCase(SetOperation.UNION)] [TestCase(SetOperation.INTERSECT)] [TestCase(SetOperation.EXCEPT)]
    public void ShapeAExecutesWithTheSameMembershipAsCore(SetOperation operation)
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients"); var remote = ImportSet(f, "Patients", server: _link);
        f.Filter(home, "id <= 2"); f.Filter(remote, "id >= 2");
        var root = f.Root; root.Operation = operation; root.SaveToDatabase();
        AssertMatchesCore(f, operation switch { SetOperation.UNION => [1, 2, 3], SetOperation.INTERSECT => [2], _ => [1] });
    }

    [Test]
    public void ShapeBCountAndRemoteParameterMatchCore()
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients"); var pit = ImportSet(f, "Contacts", server: _link, addToRoot: false);
        pit.CountSQL = "COUNT(*) AS n"; pit.SaveToDatabase();
        f.Parameter(pit, "@minimum", "0"); f.Filter(pit, "id > @minimum");
        var join = f.Join(home, pit); f.Filter(home, join.GetJoinTableAlias() + ".n > 1");
        AssertMatchesCore(f, 1);
    }

    [TestCase(false)] [TestCase(true)]
    public void SharedPatientIndexExecutesInHomeAndWholeRemoteSets(bool exec)
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients"); var remote = ImportSet(f, "Patients", server: _link);
        var pit = ImportSet(f, "Contacts", server: _link, addToRoot: false);
        pit.CountSQL = "COUNT(*) AS n"; pit.SaveToDatabase();
        f.Parameter(pit, "@minimum", "0"); f.Filter(pit, "id>@minimum");
        foreach (var set in new[] { home, remote })
        {
            var join = f.Join(set, pit); f.Filter(set, join.GetJoinTableAlias() + ".n>1");
        }
        if (exec) { var empty = ImportSet(f, "Patients", _dbB, _link); f.Filter(empty, "1=0"); }
        Assert.That(f.Build().ToScript(), Does.Contain("rendered 2 times"));
        var staged = f.BuildStaged();
        var jid = pit.JoinableCohortAggregateConfiguration.ID;
        Assert.That(staged.Sql.Split($"INTO #rdmp_pit{jid} FROM OPENQUERY(").Length, Is.EqualTo(2), "one stage for the far-side patient index");
        Assert.That(staged.ToScript(), Does.Contain($"staged once as #rdmp_pit{jid}, used by 1 sets; also rendered inside 1 remote set(s)"));
        AssertMatchesCore(f, 1);
    }

    [Test]
    public void ShapeBHomeParameterRenameLeavesRemoteLiteralUnchanged()
    {
        var f = new CohortFixture(CatalogueRepository);
        var first = ImportSet(f, "Patients"); var second = ImportSet(f, "Patients");
        var pit = ImportSet(f, "Contacts", server: _link, addToRoot: false);
        var join = f.Join(second, pit);
        f.Parameter(first, "@p", "1"); f.Filter(first, "id=@p");
        f.Parameter(second, "@p", "2"); f.Filter(second, "Patients.id=@p AND " + join.GetJoinTableAlias() + ".id IS NOT NULL");
        f.Parameter(pit, "@minimum", "0"); f.Filter(pit, "id>@minimum");
        var before = WrapperTests.DecodeArgument(f.Build().Sql);
        var nested = new CohortAggregateContainer(CatalogueRepository, SetOperation.UNION);
        var root = f.Root; root.RemoveChild(second); nested.AddChild(second, 2); root.AddChild(nested);
        Assert.That(f.Build().Sql, Does.Contain("@p_2"));
        Assert.That(WrapperTests.DecodeArgument(f.Build().Sql), Is.EqualTo(before));
        AssertMatchesCore(f, 1, 2);
    }

    [TestCase(false, RemoteBatchForm.Guarded)] [TestCase(true, RemoteBatchForm.Guarded)]
    [TestCase(false, RemoteBatchForm.UseDatabase)] [TestCase(true, RemoteBatchForm.UseDatabase)]
    public void MaximumLengthArgumentsExecute(bool unicode, RemoteBatchForm form)
    {
        var f = new CohortFixture(CatalogueRepository); var remote = ImportSet(f, "Patients", server: _link);
        var table = CohortFixture.Table(remote);
        var seed = "SELECT 1 AS id /*" + new string('\'', 30) + (unicode ? "é" : "") + "*/";
        string Wrap(string sql) => SqlServerOpenQueryWrapper.Wrap(new CohortQueryBuilderDependencySql(sql, new ParameterManager()),
            table, form, new[] { new ResultColumn("id", "int") }).Sql;
        var length = WrapperTests.DecodeArgument(Wrap(seed)).Length;
        var sqlAtLimit = seed.Replace("*/", new string('x', (unicode ? 4000 : 8000) - length) + "*/");
        Assert.That(Query("SELECT id FROM " + Wrap(sqlAtLimit)), Is.EquivalentTo(new[] { 1 }));
    }

    [TestCase(RemoteBatchForm.Guarded)] [TestCase(RemoteBatchForm.UseDatabase)]
    public void RemoteUnicodeLiteralSurvivesBothQuotingLayers(RemoteBatchForm form)
    {
        var f = new CohortFixture(CatalogueRepository); var remote = ImportSet(f, "Patients", server: _link);
        var block = new CohortQueryBuilderDependencySql("SELECT N'你好' AS id", new ParameterManager());
        var sql = SqlServerOpenQueryWrapper.Wrap(block, CohortFixture.Table(remote), form,
            new[] { new ResultColumn("id", "nvarchar(10)", "Latin1_General_CI_AS") }).Sql;
        Assert.That(Query("SELECT id FROM " + sql), Is.EquivalentTo(new[] { "你好" }));
    }

    [Test]
    public void HomeContextAndClipboardPayloadRunFromMaster()
    {
        var f = new CohortFixture(CatalogueRepository); var home = ImportSet(f, "Patients");
        f.Filter(home, $"DB_ID() = DB_ID(N'{_dbA}')");
        var script = f.Build().ToScript(); Execute("USE master;");
        Assert.That(Query("-- Linked cohort (Linked Servers) SQL\n\n" + script), Is.EquivalentTo(new[] { 1, 2, 3 }));
        Execute("USE master;");
        Assert.That(Query(script.Replace("USE " + Quote(_dbA) + ";", "")), Is.Empty);
    }

    [Test]
    public void WrongDefaultDatabaseIsHarmlessInTheUseFormAndCaughtByTheGuardedFallback()
    {
        // v56: a single far-side database no longer selects the guarded form, so a link whose login
        // lands in master (seen in production) still runs, because EXEC USE restores the context.
        var f = new CohortFixture(CatalogueRepository); var home = ImportSet(f, "Patients"); ImportSet(f, "Patients", server: _wrongLink);
        var rendered = f.Build();
        Assert.That(rendered.Warnings, Is.Empty);
        Assert.That(rendered.Sql, Does.Contain("EXEC(''USE " + Quote(_dbA) + ";"));
        var viaLink = Query(rendered.Sql);
        var direct = Query("SELECT DISTINCT id FROM " + Quote(_dbA) + ".dbo.Patients");
        Assert.That(viaLink, Is.EquivalentTo(direct));

        // Forced fallback: a count expression outside the type grammar => guarded form => the guard fires.
        var g = new CohortFixture(CatalogueRepository); var h = ImportSet(g, "Patients");
        var pit = ImportSet(g, "Patients", server: _wrongLink, addToRoot: false);
        pit.CountSQL = "CONVERT(varchar(10), MIN(id)) AS tag"; pit.SaveToDatabase();
        var join = g.Join(h, pit); g.Filter(h, join.GetJoinTableAlias() + ".tag IS NOT NULL");
        var fallback = g.Build();
        Assert.That(fallback.Warnings.Single(), Does.Contain("guarded form used because"));
        var error = Assert.Throws<SqlException>(() => Query(fallback.Sql));
        Assert.That(error!.Message, Does.Contain("expected").And.Contain(_dbA).And.Contain("master"));
    }

    [Test]
    public void MultiDatabaseApostropheAndContextArePreserved()
    {
        var f = new CohortFixture(CatalogueRepository); var home = ImportSet(f, "Patients");
        var first = ImportSet(f, "Patients", server: _wrongLink);
        var second = ImportSet(f, "Patients", _dbB, _wrongLink);
        f.Filter(home, "1=0"); f.Filter(first, $"id=1 AND DB_ID()=DB_ID(N'{_dbA}')");
        f.Parameter(second, "@label", "'O''Neil'", "varchar(20)");
        f.Filter(second, $"label=@label AND DB_ID()=DB_ID(N'{_dbB.Replace("'", "''")}')");
        Assert.That(Query(f.Build().Sql), Is.EquivalentTo(new[] { 1, 3 }));
        // A composite core query has only one USE context; compare each remote core block in its own
        // database, as the cached core compiler executes it, for DB_ID-sensitive filters.
        var reference = new List<object>();
        foreach (var (set, db) in new[] { (first, _dbA), (second, _dbB) })
        {
            var block = CohortQueryBuilderHelper.GetSQLForAggregate(set, new QueryBuilderArgs(null, f.Cic.GetAllParameters()));
            reference.AddRange(Query($"USE {Quote(db)};" + QueryBuilder.GetParameterDeclarationSQL(block.ParametersUsed.GetFinalResolvedParametersList()) + block.Sql));
        }
        Assert.That(reference, Is.EquivalentTo(new[] { 1, 3 }));
    }

    [Test]
    public void AverageResultTypeDoesNotRoundAwayMembership()
    {
        var f = new CohortFixture(CatalogueRepository); var home = ImportSet(f, "Patients");
        var pit = ImportSet(f, "Contacts", server: _wrongLink, addToRoot: false);
        pit.CountSQL = "AVG(amount) AS average"; pit.SaveToDatabase();
        var join = f.Join(home, pit); f.Filter(home, "cutoff > " + join.GetJoinTableAlias() + ".average");
        var empty = ImportSet(f, "Patients", _dbB, _wrongLink); f.Filter(empty, "1=0");
        Assert.That(f.Build().ToScript(), Does.Contain("decimal(38,6)"));
        AssertMatchesCore(f, 1);
    }

    [Test]
    public void CharacterCastCollationPreservesMembership()
    {
        var f = new CohortFixture(CatalogueRepository); var home = ImportSet(f, "Patients");
        var pit = ImportSet(f, "Contacts", server: _link, addToRoot: false);
        pit.CountSQL = "CAST(MIN(status) AS nvarchar(10)) AS status"; pit.SaveToDatabase();
        var join = f.Join(home, pit); f.Filter(home, join.GetJoinTableAlias() + ".status = N'a'");
        var empty = ImportSet(f, "Patients", _dbB, _link); f.Filter(empty, "1=0");
        AssertMatchesCore(f, 1);
        var withoutCollation = f.Build().Sql.Replace("[status] nvarchar(10) COLLATE Latin1_General_CS_AS", "[status] nvarchar(10)");
        Assert.That(Query(withoutCollation), Is.EquivalentTo(new[] { 1, 2 }), "Dropping the declaration's collation changes membership.");
    }

    [TestCase(false)] [TestCase(true)]
    public void CollationProjectionIsNecessaryInBothForms(bool exec)
    {
        var f = new CohortFixture(CatalogueRepository); ImportSet(f, "CaseHome"); ImportSet(f, "CaseRemote", server: _link);
        if (exec) { var extra = ImportSet(f, "CaseRemote", _dbB, _link); f.Filter(extra, "1=1"); }
        var root = f.Root; root.Operation = SetOperation.INTERSECT; root.SaveToDatabase();
        var script = f.Build().Sql;
        var rows = Query(script); Assert.That(rows.Count, Is.EqualTo(1));
        Assert.That(QueryStaged(f.BuildStaged().Sql), Is.EqualTo(rows), "staged keeps the composite's collation handling");
        Assert.That(rows[0].ToString(), Is.EqualTo("a").IgnoreCase);
        Assert.Throws<SqlException>(() => Query(script.Replace("[id] COLLATE Latin1_General_CI_AS AS [id]", "[id] AS [id]")));
    }

    [TestCase("AVG(amount)")] [TestCase("SUM(amount)")] [TestCase("MIN(eventDate)")]
    [TestCase("CAST(MIN(status) AS nvarchar(10))")] [TestCase("COUNT(*)")]
    [TestCase("SUM(smallValue)")] [TestCase("AVG(realValue)")] [TestCase("SUM(moneyValue)")]
    public void DeclaredTypesAndCollationsMatchServerMetadata(string expression)
    {
        var f = new CohortFixture(CatalogueRepository); var pit = ImportSet(f, "Contacts", server: _link, addToRoot: false);
        pit.CountSQL = expression + " AS result"; pit.SaveToDatabase();
        var columns = new SqlServerResultColumnResolver(pit).Resolve();
        var block = CohortQueryBuilderHelper.GetSQLForAggregate(pit, new QueryBuilderArgs(null, Array.Empty<ISqlParameter>()));
        using var cmd = Command("USE " + Quote(_dbA) + "; SELECT name,system_type_name,collation_name FROM sys.dm_exec_describe_first_result_set(@sql,NULL,0) ORDER BY column_ordinal;", ("@sql", block.Sql));
        using var reader = cmd.ExecuteReader();
        var index = 0;
        while (reader.Read())
        {
            Assert.That(columns[index].Name, Is.EqualTo(reader.GetString(0)));
            Assert.That(columns[index].SqlType, Is.EqualTo(reader.GetString(1)).IgnoreCase);
            Assert.That(columns[index].Collation, Is.EqualTo(reader.IsDBNull(2) ? null : reader.GetString(2)));
            index++;
        }
        Assert.That(index, Is.EqualTo(columns.Count));
        reader.Close();
        Assert.That(Query("SELECT id FROM " + SqlServerOpenQueryWrapper.Wrap(block, CohortFixture.Table(pit), RemoteBatchForm.UseDatabase, columns).Sql),
            Is.EquivalentTo(new[] { 1, 2 }));
    }

    [TestCase(null)] [TestCase("COUNT(*) AS n")]
    public void DeclarationOrderMatchesCoreInBothHelperBranches(string countSql)
    {
        // Regression: a PIT with extra dimensions whose Order disagrees with enumeration order. Without
        // CountSQL core keeps enumeration order; with it core sorts by Order (count last). WITH RESULT
        // SETS binds by position, so a wrong order would silently swap same-typed columns.
        var f = new CohortFixture(CatalogueRepository); var pit = ImportSet(f, "Contacts", server: _link, addToRoot: false);
        var identifier = pit.AggregateDimensions.Single(); identifier.Order = 9; identifier.SaveToDatabase();
        var extraction = pit.Catalogue.GetAllExtractionInformation(ExtractionCategory.Any);
        foreach (var (column, order) in new[] { ("eventDate", 1), ("smallValue", 0) })
        {
            var d = new AggregateDimension(CatalogueRepository, extraction.Single(e => e.ColumnInfo.GetRuntimeName() == column), pit);
            d.Order = order; d.SaveToDatabase();
        }
        pit.ClearAllInjections();
        pit.CountSQL = countSql; pit.SaveToDatabase();
        var columns = new SqlServerResultColumnResolver(pit).Resolve();
        var block = CohortQueryBuilderHelper.GetSQLForAggregate(pit, new QueryBuilderArgs(null, Array.Empty<ISqlParameter>()));
        var described = new List<string>();
        using (var cmd = Command("USE " + Quote(_dbA) + "; SELECT name FROM sys.dm_exec_describe_first_result_set(@sql,NULL,0) ORDER BY column_ordinal;", ("@sql", block.Sql)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) described.Add(reader.GetString(0));
        Assert.That(columns.Select(c => c.Name), Is.EqualTo(described), "declared order must equal the batch's select order");
        // the extra dimensions make the PIT one row per (id, eventDate, smallValue); identifiers are what matter here
        Assert.That(Query("SELECT DISTINCT id FROM " + SqlServerOpenQueryWrapper.Wrap(block, CohortFixture.Table(pit), RemoteBatchForm.UseDatabase, columns).Sql),
            Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void FreshRepositoryRendersWithUnreadableCredentialsAndMissingKeyFile()
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients"); var remote = ImportSet(f, "Patients", server: _link);
        var credentials = new DataAccessCredentials(CatalogueRepository, "Unused credentials") { Username = "unused" };
        credentials.SaveToDatabase();
        var tables = new[] { CohortFixture.Table(home), CohortFixture.Table(remote) };
        var previous = tables.Select(t => (Table: t, Credentials: CatalogueRepository.TableInfoCredentialsManager.GetCredentialsIfExistsFor(t, DataAccessContext.Any))).ToArray();
        foreach (var (table, original) in previous)
        {
            if (original != null) CatalogueRepository.TableInfoCredentialsManager.BreakAllLinksBetween(original, table);
            CatalogueRepository.TableInfoCredentialsManager.CreateLinkBetween(credentials, table, DataAccessContext.Any);
        }
        var catalogueDb = CatalogueTableRepository.DiscoveredServer.GetCurrentDatabase().GetRuntimeName();
        Execute($"UPDATE {Quote(catalogueDb)}.dbo.DataAccessCredentials SET Password=N'not-a-valid-encrypted-password' WHERE ID=@id", ("@id", credentials.ID));
        var oldPath = CatalogueRepository.GetEncryptionKeyPath();
        var oldEnvironment = Environment.GetEnvironmentVariable("RDMP_KEY_LOCATION");
        try
        {
            Environment.SetEnvironmentVariable("RDMP_KEY_LOCATION", null);
            CatalogueRepository.SetEncryptionKeyPath(Path.Combine(Path.GetTempPath(), "missing-linked-key-" + Guid.NewGuid().ToString("N")));
            var fresh = new CatalogueRepository(CatalogueTableRepository.DiscoveredServer.Builder);
            Assert.Catch(() => fresh.GetObjectByID<DataAccessCredentials>(credentials.ID), "The negative control must try to read the missing key.");
            var reloaded = fresh.GetObjectByID<CohortIdentificationConfiguration>(f.Cic.ID);
            Assert.That(new LinkedServerCohortSqlBuilder().Build(reloaded).Sql, Does.Contain("OPENQUERY"));
        }
        finally
        {
            if (oldPath == null) CatalogueRepository.DeleteEncryptionKeyPath(); else CatalogueRepository.SetEncryptionKeyPath(oldPath);
            Environment.SetEnvironmentVariable("RDMP_KEY_LOCATION", oldEnvironment);
            foreach (var (table, original) in previous)
            {
                CatalogueRepository.TableInfoCredentialsManager.BreakAllLinksBetween(credentials, table);
                if (original != null) CatalogueRepository.TableInfoCredentialsManager.CreateLinkBetween(original, table, DataAccessContext.Any);
            }
            credentials.DeleteInDatabase();
        }
    }

    [Test]
    public void RecruitmentShapeMatchesHandCalculatedIdentifiersAndCoreCache()
    {
        var fixtureDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");
        var definition = new DeserializerBuilder().Build().Deserialize<RecruitmentDefinition>(
            File.ReadAllText(Path.Combine(fixtureDirectory, "recruitment-shape.yaml")));
        var homes = new[] { _dbA, NewDatabase("HOME2"), NewDatabase("HOME3"), NewDatabase("HOME4") };
        var remotes = new[] { _dbB, NewDatabase("FAR2"), NewDatabase("FAR3") };
        foreach (var database in homes.Concat(remotes))
        {
            Execute($"""
                USE {Quote(database)};
                CREATE TABLE dbo.RecruitmentRoster(id int PRIMARY KEY, cutoff int, status varchar(5), registration varchar(20));
                WITH n AS (SELECT 1 AS id UNION ALL SELECT id+1 FROM n WHERE id<{definition.Rows})
                INSERT INTO dbo.RecruitmentRoster SELECT id,
                    CASE WHEN id IN ({string.Join(',', definition.PitExcluded)}) THEN 3 ELSE 1 END,
                    CASE WHEN id IN ({string.Join(',', definition.StatusExcluded)}) THEN 'no' ELSE 'ready' END,
                    CASE WHEN id IN ({string.Join(',', definition.RegistrationExcluded)}) THEN 'no' ELSE 'registered' END FROM n;
                CREATE TABLE dbo.RecruitmentContacts(id int);
                INSERT INTO dbo.RecruitmentContacts SELECT id FROM dbo.RecruitmentRoster;
                INSERT INTO dbo.RecruitmentContacts SELECT id FROM dbo.RecruitmentRoster;
                EXEC('CREATE FUNCTION dbo.UpperLimit() RETURNS int AS BEGIN RETURN {definition.UpperLimit}; END');
                USE master;
                """);
        }
        var f = new CohortFixture(CatalogueRepository);
        var pit = ImportSet(f, "RecruitmentContacts", remotes[0], _link, addToRoot: false);
        pit.CountSQL = "COUNT(*) AS n"; pit.SaveToDatabase();
        f.Parameter(f.Cic, "@minimum", definition.GlobalMinimum.ToString());
        f.Parameter(pit, "@pit", "0"); f.Filter(pit, "id>@pit");
        var root = f.Root; root.Operation = SetOperation.EXCEPT; root.SaveToDatabase();
        var includes = new CohortAggregateContainer(CatalogueRepository, SetOperation.INTERSECT) { Order = 1 }; includes.SaveToDatabase();
        root.AddChild(includes);
        var sets = new List<(AggregateConfiguration Set, string Database)>();
        foreach (var entry in definition.Sets)
        {
            var database = (entry.Remote ? remotes : homes)[entry.Database];
            var set = ImportSet(f, "RecruitmentRoster", database, entry.Remote ? _link : null, addToRoot: false);
            set.Name = $"cic_{f.Cic.ID}_{entry.Name}"; set.SaveToDatabase();
            (entry.Inclusion ? includes : root).AddChild(set, sets.Count);
            var predicate = entry.Where;
            if (entry.UsesPit) predicate = predicate.Replace("{pit}", f.Join(set, pit).GetJoinTableAlias());
            f.Filter(set, predicate);
            if (entry.Parameter != null) f.Parameter(set, entry.Parameter.Name, entry.Parameter.Value, entry.Parameter.Type);
            sets.Add((set, database));
        }

        var expected = File.ReadAllLines(Path.Combine(fixtureDirectory, "recruitment-shape.expected.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')).Select(int.Parse).ToArray();
        var script = f.Build().Sql;
        Assert.That(script, Does.Contain("@code_2").And.Contain("dbo.UpperLimit()").And.Not.Contain("RAISERROR"));
        Assert.That(Query(script), Is.EquivalentTo(expected));
        Assert.That(QueryStaged(f.BuildStaged().Sql), Is.EquivalentTo(expected), "staged");
        // Prevent a vacuous fixture: each of the eleven sets must affect final membership.
        foreach (var (set, _) in sets)
        {
            set.IsDisabled = true; set.SaveToDatabase();
            try { Assert.That(Query(f.Build().Sql), Is.Not.EquivalentTo(expected), set.Name); }
            finally { set.IsDisabled = false; set.SaveToDatabase(); }
        }

        var cacheDatabase = _server.ExpectDatabase("RDMP_LINKED_TEST_CACHE_" + _suffix);
        _createdDatabases.Add(cacheDatabase.GetRuntimeName());
        var patcher = new QueryCachingPatcher();
        new MasterDatabaseScriptExecutor(cacheDatabase).CreateAndPatchDatabase(patcher, new AcceptAllCheckNotifier());
        var pointer = new ExternalDatabaseServer(CatalogueRepository, "Plugin test cache", patcher);
        pointer.SetProperties(cacheDatabase); pointer.SaveToDatabase();
        f.Cic.QueryCachingServer_ID = pointer.ID; f.Cic.SaveToDatabase();
        var manager = new CachedAggregateConfigurationResultsManager(pointer);
        var globals = f.Cic.GetAllParameters();
        foreach (var (set, database) in sets)
        {
            var use = set.PatientIndexJoinablesUsed.SingleOrDefault();
            var index = use?.JoinableCohortAggregateConfiguration.AggregateConfiguration;
            var indexSql = index == null ? null : CohortQueryBuilderHelper.GetSQLForAggregate(index, new QueryBuilderArgs(new QueryBuilderCustomArgs(), globals));
            var block = CohortQueryBuilderHelper.GetSQLForAggregate(set, new QueryBuilderArgs(use, index, indexSql, null, globals));
            var sql = QueryBuilder.GetParameterDeclarationSQL(block.ParametersUsed.GetFinalResolvedParametersList()) + block.Sql;
            using var command = Command($"USE {Quote(database)};" + sql);
            using var reader = command.ExecuteReader();
            var data = new DataTable(); data.Load(reader); reader.Close();
            manager.CommitResults(new CacheCommitIdentifierList(set, sql, data, new DatabaseColumnRequest("id", "int"), 60));
        }
        var core = new CohortQueryBuilder(f.Cic, null);
        Assert.That(Query(core.SQL), Is.EquivalentTo(expected));
        Assert.That(core.Results.CountOfCachedSubQueries, Is.EqualTo(11));
        Assert.That(Query(f.Build().Sql), Is.EquivalentTo(expected));
        Assert.That(f.Build().ToScript(), Does.Contain("QueryCache ignored by this view"));
        // Plan 0.7.8 acceptance shape: the whole identifier set of #rdmp_cohort equals core's cached result, both ways.
        using var window = OpenWindow();
        QueryStaged(f.BuildStaged().Sql, window);
        var cached = Query(core.SQL);
        Assert.That(QueryOn(window, "SELECT [id] FROM #rdmp_cohort"), Is.EquivalentTo(cached));
    }

    // ---- Staged shape, executed (plan 0.7.7) ----

    // (q): an identical re-run in the same window works; a re-run after an edit that changes a leaf's columns fails at
    // compile with Msg 207, which header line 10 explains.
    [Test]
    public void StagedReRunInTheSameWindow()
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Contacts"); ImportSet(f, "Patients", server: _link);
        Operation(f, SetOperation.INTERSECT);
        using var window = OpenWindow();
        Assert.That(QueryStaged(f.BuildStaged().Sql, window), Is.EquivalentTo(new[] { 1, 2 }));
        Assert.That(QueryStaged(f.BuildStaged().Sql, window), Is.EquivalentTo(new[] { 1, 2 }), "identical re-run");
        home.CountSQL = "COUNT(*) AS n"; home.SaveToDatabase();
        var edited = f.BuildStaged();
        Assert.That(edited.ToScript(), Does.Contain("close this query window first"));
        var error = Assert.Throws<SqlException>(() => QueryStaged(edited.Sql, window));
        Assert.That(error!.Number, Is.EqualTo(207), error.Message);
    }

    // (s) and (n): a far-side stage that raises ends the script with that error; neither the grid nor #rdmp_cohort runs,
    // and the CATCH resets the window's settings.
    [Test]
    public void FailingFarSideStageStopsTheScriptAndResetsSettings()
    {
        var f = new CohortFixture(CatalogueRepository);
        ImportSet(f, "Patients"); var remote = ImportSet(f, "Patients", server: _link);
        f.Filter(remote, "id / (id - id) = 1");
        Operation(f, SetOperation.EXCEPT);
        using var window = OpenWindow();
        var error = Assert.Throws<SqlException>(() => QueryStaged(f.BuildStaged().Sql, window));
        Assert.That(error!.Message, Does.Contain("Divide by zero"));
        Assert.That(QueryOn(window, "SELECT OBJECT_ID('tempdb..#rdmp_cohort')").Single(), Is.EqualTo(DBNull.Value));
        Assert.That(QueryOn(window, $"SELECT OBJECT_ID('tempdb..#rdmp_s')").Single(), Is.EqualTo(DBNull.Value));
        Assert.That(QueryOn(window, "SELECT @@OPTIONS & (16384 | 512)").Single(), Is.EqualTo(0), "XACT_ABORT and NOCOUNT are off again");
    }

    // (n), documented limitation: deferred name resolution errors (Msg 208 here) are not caught at the same level, so the
    // window keeps XACT_ABORT and NOCOUNT on, as header line 10 says.
    [Test]
    public void DeferredNameResolutionErrorBypassesTheCatch()
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients");
        f.Filter(home, "id IN (SELECT id FROM dbo.NoSuchTable_" + _suffix + ")");
        using var window = OpenWindow();
        var error = Assert.Throws<SqlException>(() => QueryStaged(f.BuildStaged().Sql, window));
        Assert.That(error!.Number, Is.EqualTo(208), error.Message);
        Assert.That(QueryOn(window, "SELECT @@OPTIONS & (16384 | 512)").Single(), Is.EqualTo(16384 | 512));
        Assert.That(f.BuildStaged().ToScript(), Does.Contain("If a run stops with Msg 207 or 208, this window also keeps XACT_ABORT ON and NOCOUNT ON: close it."));
    }

    // (x): leaves whose tuples differ only in the count keep core's whole-row semantics.
    [TestCase(SetOperation.EXCEPT, new[] { 1, 2 })] [TestCase(SetOperation.INTERSECT, new int[0])]
    public void CountTuplesKeepWholeRowSemantics(SetOperation operation, int[] expected)
    {
        var f = new CohortFixture(CatalogueRepository);
        var all = ImportSet(f, "Contacts"); var early = ImportSet(f, "Contacts");
        foreach (var set in new[] { all, early }) { set.CountSQL = "COUNT(*) AS n"; set.SaveToDatabase(); }
        f.Filter(early, "eventDate < '2020-01-02'");
        Operation(f, operation);
        AssertMatchesCore(f, expected.Cast<object>().ToArray());
    }

    // (y): a home identifier-only leaf against a whole remote leaf with a count: the remote projection selects only the
    // identifier, so both shapes run (core's no-cache composite would fail with Msg 205 here).
    [TestCase(SetOperation.INTERSECT, new[] { 1, 2 })] [TestCase(SetOperation.EXCEPT, new[] { 3 })]
    public void RemoteCountLeafProjectsTheIdentifierOnly(SetOperation operation, int[] expected)
    {
        var f = new CohortFixture(CatalogueRepository);
        ImportSet(f, "Patients"); var remote = ImportSet(f, "Contacts", server: _link);
        remote.CountSQL = "COUNT(*) AS n"; remote.SaveToDatabase();
        Operation(f, operation);
        AssertShapes(f, expected.Cast<object>().ToArray());
    }

    // (z2): an extra dimension makes the leaf a (id, marker) tuple; (1,2) EXCEPT (1,1) keeps 1, as in core.
    [TestCase(SetOperation.EXCEPT, new[] { 1 })] [TestCase(SetOperation.INTERSECT, new int[0])]
    public void ExtraDimensionTuplesKeepWholeRowSemantics(SetOperation operation, int[] expected)
    {
        var f = new CohortFixture(CatalogueRepository);
        var a = ImportSet(f, "MarkA"); var b = ImportSet(f, "MarkB");
        AddDimension(a, "marker", 1); AddDimension(b, "marker", 1);
        Operation(f, operation);
        Assert.That(f.BuildStaged().Sql, Does.Contain($"SELECT [id] AS [id], [marker] AS [marker] FROM #rdmp_s{a.ID}"));
        AssertMatchesCore(f, expected.Cast<object>().ToArray());
    }

    // (z3), plan 0.7.9.1: a whole remote set with an extra dimension. 1.0.1 declared only the identifier (Msg 11537).
    [TestCase(SetOperation.EXCEPT, new int[0])] [TestCase(SetOperation.UNION, new[] { 1 })]
    public void WholeRemoteSetWithAnExtraDimensionExecutes(SetOperation operation, int[] expected)
    {
        var f = new CohortFixture(CatalogueRepository);
        ImportSet(f, "MarkB"); var remote = ImportSet(f, "MarkA", server: _link);
        AddDimension(remote, "marker", 1);
        Operation(f, operation);
        Assert.That(f.Build().ToScript(), Does.Contain("declared columns: [id] int, [marker] int"));
        // Not compared with core's no-cache composite, which selects both remote columns against one home column (Msg 205);
        // the whole remote projection selects the identifier only (plan 0.7.3).
        AssertShapes(f, expected.Cast<object>().ToArray());
    }

    // (z3), plan 0.7.9.2: a multi-line literal in a nested set. Core collapses filters onto one line, so the literal comes
    // through HAVING. Core's own no-cache composite indents it and loses the row; both plugin shapes keep it.
    [Test]
    public void MultiLineLiteralInANestedSetKeepsItsMembership()
    {
        var f = new CohortFixture(CatalogueRepository);
        var outer = ImportSet(f, "Patients"); f.Filter(outer, "id = 3");
        var nested = ImportSet(f, "Texts", addToRoot: false);
        nested.HavingSQL = "MIN(t) = 'a" + Environment.NewLine + "b'"; nested.SaveToDatabase();
        var sub = new CohortAggregateContainer(CatalogueRepository, SetOperation.UNION);
        f.Root.AddChild(sub); sub.AddChild(nested, 1);
        AssertShapes(f, 1, 3);
        var core = new CohortQueryBuilder(f.Cic, null).SQL;
        Assert.That(Query($"USE {Quote(_dbA)};" + core), Is.EquivalentTo(new[] { 3 }), "core's no-cache view indents the literal");
        var direct = CohortQueryBuilderHelper.GetSQLForAggregate(nested, new QueryBuilderArgs(null, f.Cic.GetAllParameters()));
        Assert.That(Query($"USE {Quote(_dbA)};" + direct.Sql), Is.EquivalentTo(new[] { 1 }), "the set run on its own, as the cached build does");
    }

    // (z3), v66: orders int.MinValue and -1 under HAVING fit the comparator, so staged accepts them and matches core.
    [Test]
    public void ExtremeButDeterminedOrdersMatchCore()
    {
        var f = new CohortFixture(CatalogueRepository);
        var a = ImportSet(f, "MarkA"); var b = ImportSet(f, "MarkB");
        foreach (var set in new[] { a, b })
        {
            var id = set.AggregateDimensions.Single(); id.Order = int.MinValue; id.SaveToDatabase();
            AddDimension(set, "marker", -1);
            set.HavingSQL = "COUNT(*) > 0"; set.SaveToDatabase();
        }
        Operation(f, SetOperation.EXCEPT);
        AssertMatchesCore(f, 1);
    }

    // (v): guarded blocks stage with SELECT * INTO and are not refused: a patient index whose count is outside the type
    // grammar, and a whole remote set whose column order is ambiguous (so it cannot use the positional EXEC form).
    [Test]
    public void GuardedBlocksStageWithSelectStar()
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients");
        var pit = ImportSet(f, "Patients", server: _link, addToRoot: false);
        pit.CountSQL = "CONVERT(varchar(10), MIN(id)) AS tag"; pit.SaveToDatabase();
        var join = f.Join(home, pit); f.Filter(home, join.GetJoinTableAlias() + ".tag IS NOT NULL");
        var remote = ImportSet(f, "MarkA", server: _link);
        var id = remote.AggregateDimensions.Single(); id.Order = 1; id.SaveToDatabase();
        AddDimension(remote, "marker", 1);
        remote.HavingSQL = "COUNT(*) > 0"; remote.SaveToDatabase();
        Operation(f, SetOperation.UNION);
        var staged = f.BuildStaged();
        Assert.That(staged.Warnings.Count(w => w.Contains("guarded form used because")), Is.EqualTo(2));
        Assert.That(staged.Sql, Does.Contain($"SELECT * INTO #rdmp_pit{join.JoinableCohortAggregateConfiguration_ID} FROM OPENQUERY(")
            .And.Contain($"SELECT * INTO #rdmp_r{remote.ID} FROM OPENQUERY("));
        AssertShapes(f, 1, 2, 3);
    }

    // (w) and (u): an aliased expression identifier, and an aliased identifier ending in COLLATE, stage and match core.
    [Test]
    public void AliasedExpressionIdentifierStages()
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients"); ImportSet(f, "Contacts", server: _link);
        var id = home.AggregateDimensions.Single(); id.SelectSQL = "CAST(" + id.SelectSQL + " AS bigint)"; id.SaveToDatabase();
        home.ClearAllInjections();
        Assert.That(Assert.Throws<LinkedServerRenderException>(() => f.BuildStaged())!.Reason, Is.EqualTo(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias));
        id.Alias = "id"; id.SaveToDatabase(); home.ClearAllInjections();
        Operation(f, SetOperation.INTERSECT);
        AssertMatchesCore(f, 1L, 2L);
    }

    [Test]
    public void IdentifierEndingInCollateKeepsItInTheProjection()
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "CaseHome"); ImportSet(f, "CaseRemote", server: _link);
        var id = home.AggregateDimensions.Single(); id.SelectSQL += " COLLATE Latin1_General_CS_AS"; id.Alias = "id"; id.SaveToDatabase();
        home.ClearAllInjections();
        Operation(f, SetOperation.INTERSECT);
        Assert.That(f.BuildStaged().Sql, Does.Contain($"SELECT [id] COLLATE Latin1_General_CS_AS AS [id] FROM #rdmp_s{home.ID}"));
        AssertShapes(f, "a", "A");
    }

    [Test]
    public void CountGridReportsEachLeaf()
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients"); var remote = ImportSet(f, "Contacts", server: _link);
        QueryStaged(f.BuildStaged().Sql, null, out var grid);
        Assert.That(grid, Is.EqualTo(new[] { (home.ToString(), 3), (remote.ToString(), 2), ("Final cohort", 3) }));
    }

    // Review of 1.0.2 (P1): a compound count is not an int. 1.0.2 before the fix declared int, so 0.5 became 0 and
    // identifier 2 was silently dropped. Now the block is guarded and keeps the real type.
    [TestCase("COUNT(*) * 1.0 / NULLIF(COUNT(*) + 1, 0) AS ratio")]
    [TestCase("COUNT(* --comment\r) * 1.0 / NULLIF(\nCOUNT(*) + 1, 0) AS ratio")] // review of v68: bare CR ends the comment
    public void CompoundCountExpressionKeepsItsValue(string countSql)
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients");
        var pit = ImportSet(f, "Contacts", server: _link, addToRoot: false);
        pit.CountSQL = countSql; pit.SaveToDatabase();
        var join = f.Join(home, pit); f.Filter(home, join.GetJoinTableAlias() + ".ratio > 0.4");
        Assert.That(f.Build().Warnings, Has.One.Contains("guarded form used because"));
        AssertMatchesCore(f, 1, 2);
    }

    // Review of 1.0.2 (P2): quoted aliases, home and whole remote, both shapes.
    [TestCase("[patient id]")] [TestCase("\"patient id\"")] [TestCase(" [patient id] ")]
    [TestCase("'patient_id'")] [TestCase("'it''s id'")]
    public void QuotedAliasesExecute(string alias)
    {
        var f = new CohortFixture(CatalogueRepository);
        var home = ImportSet(f, "Patients"); var remote = ImportSet(f, "Contacts", server: _link);
        foreach (var set in new[] { home, remote })
        {
            var id = set.AggregateDimensions.Single(); id.Alias = alias; id.SaveToDatabase(); set.ClearAllInjections();
        }
        Operation(f, SetOperation.INTERSECT);
        AssertMatchesCore(f, 1, 2);
    }

    // (t): one far-side patient index with two HOME consumers is staged once and both derived tables read it.
    [Test]
    public void FarSidePatientIndexWithTwoHomeConsumers()
    {
        var f = new CohortFixture(CatalogueRepository);
        var first = ImportSet(f, "Patients"); var second = ImportSet(f, "Contacts");
        var pit = ImportSet(f, "Contacts", server: _link, addToRoot: false);
        pit.CountSQL = "COUNT(*) AS n"; pit.SaveToDatabase();
        foreach (var (set, predicate) in new[] { (first, ".n > 1"), (second, ".n >= 1") })
        {
            var join = f.Join(set, pit); f.Filter(set, join.GetJoinTableAlias() + predicate);
        }
        Operation(f, SetOperation.UNION);
        var staged = f.BuildStaged();
        var jid = pit.JoinableCohortAggregateConfiguration.ID;
        Assert.That(staged.Sql.Split($"INTO #rdmp_pit{jid} FROM OPENQUERY(").Length, Is.EqualTo(2), "staged once");
        Assert.That(staged.Sql.Split($"FROM #rdmp_pit{jid}").Length, Is.EqualTo(3), "read by both consumers");
        Assert.That(staged.ToScript(), Does.Contain($"staged once as #rdmp_pit{jid}, used by 2 sets").And.Not.Contain("also rendered inside"));
        Assert.That(f.Build().ToScript(), Does.Contain("rendered 2 times"));
        AssertMatchesCore(f, 1, 2);
    }

    // (x), three leaves: a plain CS home leaf, an explicit CI home leaf and a remote character-to-character CI leaf. The
    // explicit CI decides the comparison in both shapes, and no COLLATE is added to the remote projection. Dropping the
    // mirrored COLLATE from the staged projection makes the run fail with Msg 468.
    [Test]
    public void ThreeLeafCollationCase()
    {
        var f = new CohortFixture(CatalogueRepository);
        var plain = ImportSet(f, "CaseRemote"); var explicitCi = ImportSet(f, "CaseHome");
        var remote = ImportSet(f, "CaseHome", server: _link);
        var ci = explicitCi.AggregateDimensions.Single(); ci.SelectSQL += " COLLATE Latin1_General_CI_AS"; ci.Alias = "id"; ci.SaveToDatabase();
        var cast = remote.AggregateDimensions.Single(); cast.SelectSQL = "CAST(" + cast.SelectSQL + " AS varchar(10))"; cast.Alias = "id"; cast.SaveToDatabase();
        foreach (var set in new[] { explicitCi, remote }) set.ClearAllInjections();
        Operation(f, SetOperation.EXCEPT);
        var staged = f.BuildStaged().Sql;
        Assert.That(staged, Does.Contain($"SELECT [id] AS [id] FROM #rdmp_r{remote.ID}"), "no COLLATE added to the remote projection");
        Assert.That(staged, Does.Contain($"SELECT [id] COLLATE Latin1_General_CI_AS AS [id] FROM #rdmp_s{explicitCi.ID}"));
        AssertMatchesCore(f);
        // The temp table keeps CI only as an implicit collation, so without the mirrored COLLATE the leaves conflict.
        Assert.That(Assert.Throws<SqlException>(() => QueryStaged(staged.Replace("[id] COLLATE Latin1_General_CI_AS AS [id]", "[id] AS [id]")))!.Number,
            Is.EqualTo(468));
    }

    // (x): a character CountSQL ending in COLLATE keeps it in the staged projection; tuples (1,'a') and (1,'A') are equal
    // under the explicit CI, so EXCEPT keeps only 3, as core does. Without the mirror the run fails with Msg 468.
    [Test]
    public void CharacterCountCollationIsMirrored()
    {
        var f = new CohortFixture(CatalogueRepository);
        var patients = ImportSet(f, "Patients"); var tuples = ImportSet(f, "CaseTuples");
        patients.CountSQL = "MIN(label) COLLATE Latin1_General_CI_AS AS n"; patients.SaveToDatabase();
        tuples.CountSQL = "MIN(label) AS n"; tuples.SaveToDatabase();
        Operation(f, SetOperation.EXCEPT);
        var staged = f.BuildStaged().Sql;
        Assert.That(staged, Does.Contain($"SELECT [id] AS [id], [n] COLLATE Latin1_General_CI_AS AS [n] FROM #rdmp_s{patients.ID}"));
        AssertMatchesCore(f, 3);
        Assert.That(Assert.Throws<SqlException>(() => QueryStaged(staged.Replace("[n] COLLATE Latin1_General_CI_AS AS [n]", "[n] AS [n]")))!.Number,
            Is.EqualTo(468), "without the mirrored COLLATE the implicit CI and CS columns conflict");
    }

    private string NewDatabase(string label)
    {
        var name = $"RDMP_LINKED_TEST_{label}_{_suffix}";
        Execute("CREATE DATABASE " + Quote(name)); _createdDatabases.Add(name);
        return name;
    }

    public sealed class RecruitmentDefinition
    {
        public int Rows { get; set; }
        public int GlobalMinimum { get; set; }
        public int UpperLimit { get; set; }
        public int[] StatusExcluded { get; set; }
        public int[] RegistrationExcluded { get; set; }
        public int[] PitExcluded { get; set; }
        public RecruitmentSet[] Sets { get; set; }
    }

    public sealed class RecruitmentSet
    {
        public string Name { get; set; }
        public int Database { get; set; }
        public bool Inclusion { get; set; }
        public bool Remote { get; set; }
        public bool UsesPit { get; set; }
        public string Where { get; set; }
        public RecruitmentParameter Parameter { get; set; }
    }

    public sealed class RecruitmentParameter
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
    }

    private static string Quote(string value) => "[" + value.Replace("]", "]]") + "]";
    private DbCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 60;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; command.Parameters.Add(p);
        }
        return command;
    }
    private void Execute(string sql, params (string Name, object Value)[] parameters) { using var command = Command(sql, parameters); command.ExecuteNonQuery(); }
    private DbConnection OpenWindow()
    {
        var connection = _server.GetConnection(); connection.Open(); return connection;
    }

    /// <summary>Runs a staged script as an analyst would, in its own query window unless one is given, and returns the
    /// first column of the last result set (the cohort); the count grid comes before it.</summary>
    private List<object> QueryStaged(string sql, DbConnection window = null) => QueryStaged(sql, window, out _);

    private List<object> QueryStaged(string sql, DbConnection window, out List<(string Set, int Rows)> grid)
    {
        var own = window == null;
        window ??= OpenWindow();
        try
        {
            using var command = window.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 120;
            using var reader = command.ExecuteReader();
            var sets = new List<List<object[]>>();
            do
            {
                var rows = new List<object[]>();
                while (reader.Read()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values); }
                sets.Add(rows);
            } while (reader.NextResult());
            Assert.That(sets, Has.Count.EqualTo(2), "the staged script returns the count grid, then the cohort");
            grid = sets[0].Select(r => ((string)r[1], (int)r[4])).ToList();
            Assert.That(sets[0].Select(r => (int)r[0]), Is.Ordered, "grid ordered by [n]");
            Assert.That(sets[0][^1][1], Is.EqualTo("Final cohort"));
            Assert.That(sets[0][^1][4], Is.EqualTo(sets[1].Count), "the final row counts the cohort");
            return sets[1].Select(r => r[0]).ToList();
        }
        finally { if (own) window.Dispose(); }
    }

    private static List<object> QueryOn(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<object>(); while (reader.Read()) result.Add(reader.GetValue(0)); return result;
    }

    private static AggregateDimension AddDimension(AggregateConfiguration set, string column, int order)
    {
        var extraction = set.Catalogue.GetAllExtractionInformation(ExtractionCategory.Any).Single(e => e.ColumnInfo.GetRuntimeName() == column);
        var dimension = new AggregateDimension(set.CatalogueRepository, extraction, set) { Order = order };
        dimension.SaveToDatabase();
        set.ClearAllInjections();
        return dimension;
    }

    private void Operation(CohortFixture f, SetOperation operation)
    {
        var root = f.Root; root.Operation = operation; root.SaveToDatabase();
    }

    private List<object> Query(string sql)
    {
        using var command = Command(sql); using var reader = command.ExecuteReader();
        var result = new List<object>(); while (reader.Read()) result.Add(reader.GetValue(0)); return result;
    }
}
