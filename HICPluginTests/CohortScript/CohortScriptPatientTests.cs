// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using FAnsi;
using FAnsi.Discovery;
using NUnit.Framework;
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.ReusableLibraryCode.Checks;
using RdmpCohortScript;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// Builds cohorts on fake patient data, exports each as a build script, rebuilds it, and runs RDMP's own query
/// for the original and the rebuilt cohort. The two must return the same patients. The plugin writes no SQL;
/// the SQL here comes from RDMP's <see cref="CohortQueryBuilder"/>.
/// </summary>
public class CohortScriptPatientTests : DatabaseTests
{
    [Test]
    public void RebuiltCohortReturnsTheSamePatientsAsTheOriginalForSetsUnderExcept()
    {
        var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);

        var admissions = Table("cs_admissions", new[] { "chi", "code" },
            Rows(1, 8, i => new object[] { P(i), "E11" }).Concat(new[] { new object[] { P(9), "I10" } }));
        var prescribing = Table("cs_prescribing", new[] { "chi", "drug" },
            new[] { 2, 4, 6, 8, 10 }.Select(i => new object[] { P(i), "Diazepam" }));
        var demography = Table("cs_demography", new[] { "chi", "date_of_death" },
            Rows(1, 12, i => new object[] { P(i), i is 2 or 4 or 9 ? "2021-01-01" : null }));

        var cic = new CohortIdentificationConfiguration(CatalogueRepository, "Patient check, except");
        cic.CreateRootContainerIfNotExists();
        var root = cic.RootCohortAggregateContainer;
        root.Operation = SetOperation.EXCEPT;
        root.SaveToDatabase();

        var inclusion = new CohortAggregateContainer(CatalogueRepository, SetOperation.INTERSECT) { Name = "Inclusion" };
        inclusion.SaveToDatabase();
        root.AddChild(inclusion);
        var exclusion = new CohortAggregateContainer(CatalogueRepository, SetOperation.UNION) { Name = "Exclusion", Order = 1 };
        exclusion.SaveToDatabase();
        root.AddChild(exclusion);

        var setA = AddSet(cic, inclusion, Import(db, admissions), 0);
        var codeFilter = AddFilter(setA, "Diabetes code", "code LIKE @code");
        var codeParam = codeFilter.GetFilterFactory().CreateNewParameter(codeFilter, "DECLARE @code AS varchar(10)");
        codeParam.Value = "'E1%'";
        codeParam.SaveToDatabase();

        var setB = AddSet(cic, inclusion, Import(db, prescribing), 1);
        AddFilter(setB, "Diazepam", "drug = 'Diazepam'");

        var setC = AddSet(cic, exclusion, Import(db, demography), 0);
        AddFilter(setC, "Has died", "date_of_death IS NOT NULL");

        // diabetic (1-8) and on diazepam (2,4,6,8,10), minus the dead (2,4,9)
        AssertRebuildReturnsSamePatients(db, cic, new[] { P(6), P(8) });
    }

    [Test]
    public void RebuiltCohortReturnsTheSamePatientsAsTheOriginalForAPatientIndexTableAndADisabledFilter()
    {
        var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);

        var admissions = Table("cs_pit_admissions", new[] { "chi", "admission_date" },
            Rows(1, 4, i => new object[] { P(i), "2020-01-01" }));
        var prescribing = Table("cs_pit_prescribing", new[] { "chi", "drug", "prescribed_date" }, new[]
        {
            new object[] { P(1), "A", "2019-06-01" }, // before the admission
            new object[] { P(2), "A", "2020-06-01" },
            new object[] { P(3), "B", "2020-06-01" }, // drug B only counts if the disabled filter comes back
            new object[] { P(5), "A", "2020-06-01" }  // no admission
        });

        var cic = new CohortIdentificationConfiguration(CatalogueRepository, "Patient check, index table");
        cic.CreateRootContainerIfNotExists();
        var root = cic.RootCohortAggregateContainer;
        root.Operation = SetOperation.UNION;
        root.SaveToDatabase();

        var admissionsCata = Import(db, admissions);
        // no count column: RDMP's default count(*) has no alias, so the joined table would have an unnamed column
        var pit = new AggregateConfiguration(CatalogueRepository, admissionsCata, "Admissions") { CountSQL = null };
        pit.SaveToDatabase();
        foreach (var ei in admissionsCata.GetAllExtractionInformation(ExtractionCategory.Any)
                     .Where(e => e.GetRuntimeName() is "chi" or "admission_date"))
            _ = new AggregateDimension(CatalogueRepository, ei, pit);
        cic.EnsureNamingConvention(pit);
        var joinable = new JoinableCohortAggregateConfiguration(CatalogueRepository, cic, pit);

        var set = AddSet(cic, root, Import(db, prescribing), 0);
        var use = joinable.AddUser(set);
        use.JoinType = ExtractionJoinType.Inner;
        use.SaveToDatabase();

        var rootFc = (AggregateFilterContainer)set.RootFilterContainer;
        _ = AddFilter(set, "After an admission", $"prescribed_date >= ix{joinable.ID}.admission_date");
        var or = new AggregateFilterContainer(CatalogueRepository, FilterContainerOperation.OR);
        rootFc.AddChild(or);
        new AggregateFilter(CatalogueRepository, "Drug A", or) { WhereSQL = "drug = 'A'" }.SaveToDatabase();
        new AggregateFilter(CatalogueRepository, "Drug B", or) { WhereSQL = "drug = 'B'", IsDisabled = true }
            .SaveToDatabase();

        // prescribed after an admission (2, 3), and drug A only, because the drug B filter is disabled
        AssertRebuildReturnsSamePatients(db, cic, new[] { P(2) });
    }

    [Test]
    public void RebuiltCohortReturnsTheSamePatientsAsTheOriginalForAPrescriptionWindowAfterAnAdmission()
    {
        var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);

        var admissions = Table("cs_cx_admissions", new[] { "chi", "admission_date" },
            Rows(1, 6, i => new object[] { P(i), "2020-01-01" }));
        var prescribing = Table("cs_cx_prescribing", new[] { "chi", "drug", "prescribed_date" }, new[]
        {
            new object[] { P(1), "A", "2019-12-01" }, // before the admission
            new object[] { P(2), "A", "2020-02-01" }, // inside the 90-day window
            new object[] { P(3), "A", "2020-06-01" }, // beyond the window; inside it if @window grew
            new object[] { P(4), "A", "2020-03-01" }, // inside, but the diagnosis set rejects them
            new object[] { P(5), "B", "2020-02-01" }, // inside, but drug B's filter is disabled
            new object[] { P(6), "A", "2020-01-15" }, // inside
            new object[] { P(7), "A", "2020-02-01" }  // no admission
        });
        var diagnoses = Table("cs_cx_diagnoses", new[] { "chi", "code", "diagnosis_date" }, new[]
        {
            new object[] { P(2), "E11.2", "2018-05-01" },
            new object[] { P(3), "E11.2", "2018-05-01" },
            new object[] { P(4), "I10", "2018-05-01" },   // not diabetes
            new object[] { P(5), "E11.2", "2018-05-01" },
            new object[] { P(6), "E11.9", "2010-05-01" }  // too early, but kept by the code branch of the OR
        });

        var cic = new CohortIdentificationConfiguration(CatalogueRepository, "Patient check, prescription window");
        cic.CreateRootContainerIfNotExists();
        var root = cic.RootCohortAggregateContainer;
        root.Operation = SetOperation.INTERSECT;
        root.SaveToDatabase();

        var admissionsCata = Import(db, admissions);
        var pit = new AggregateConfiguration(CatalogueRepository, admissionsCata, "Admissions") { CountSQL = null };
        pit.SaveToDatabase();
        foreach (var ei in admissionsCata.GetAllExtractionInformation(ExtractionCategory.Any)
                     .Where(e => e.GetRuntimeName() is "chi" or "admission_date"))
            _ = new AggregateDimension(CatalogueRepository, ei, pit);
        cic.EnsureNamingConvention(pit);
        var joinable = new JoinableCohortAggregateConfiguration(CatalogueRepository, cic, pit);

        // prescribed drug A within @window days of an admission
        var prescribed = AddSet(cic, root, Import(db, prescribing), 0);
        var use = joinable.AddUser(prescribed);
        use.JoinType = ExtractionJoinType.Inner;
        use.SaveToDatabase();
        new AnyTableSqlParameter(CatalogueRepository, prescribed, "DECLARE @window AS int") { Value = "90" }
            .SaveToDatabase();
        AddFilter(prescribed, "Within the window",
            $"prescribed_date BETWEEN ix{joinable.ID}.admission_date AND DATEADD(day, @window, ix{joinable.ID}.admission_date)");
        var drugs = new AggregateFilterContainer(CatalogueRepository, FilterContainerOperation.OR);
        ((AggregateFilterContainer)prescribed.RootFilterContainer).AddChild(drugs);
        new AggregateFilter(CatalogueRepository, "Drug A", drugs) { WhereSQL = "drug = 'A'" }.SaveToDatabase();
        new AggregateFilter(CatalogueRepository, "Drug B", drugs) { WhereSQL = "drug = 'B'", IsDisabled = true }
            .SaveToDatabase();

        // diabetes, diagnosed since 2015 or with code E11.9
        var diagnosed = AddSet(cic, root, Import(db, diagnoses), 1);
        AddFilter(diagnosed, "Diabetes", "code LIKE 'E11%'");
        var since = new AggregateFilterContainer(CatalogueRepository, FilterContainerOperation.OR);
        ((AggregateFilterContainer)diagnosed.RootFilterContainer).AddChild(since);
        new AggregateFilter(CatalogueRepository, "Since 2015", since) { WhereSQL = "diagnosis_date >= '2015-01-01'" }
            .SaveToDatabase();
        new AggregateFilter(CatalogueRepository, "Code E11.9", since) { WhereSQL = "code = 'E11.9'" }.SaveToDatabase();

        // prescribed in the window (2, 4, 6) and diagnosed (2, 3, 5, 6)
        AssertRebuildReturnsSamePatients(db, cic, new[] { P(2), P(6) });
    }

    private void AssertRebuildReturnsSamePatients(DiscoveredDatabase db, CohortIdentificationConfiguration original,
        string[] expected)
    {
        var activator = new ConsoleInputManager(RepositoryLocator, ThrowImmediatelyCheckNotifier.Quiet)
        {
            DisallowInput = true
        };

        var outDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"rdmp-cohortscript-{Guid.NewGuid()}"));
        try
        {
            new ExecuteCommandExportCohortAsScript(activator, original, outDir).Execute();
            var script = new FileInfo(Path.Join(outDir.FullName, original.Name, "build.script.yaml"));
            var rebuiltName = $"{original.Name} (rebuilt)";
            new ExecuteCommandBuildCohortFromScript(activator, script, rebuiltName).Execute();

            var rebuilt = CatalogueRepository
                .GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", rebuiltName).Single();

            var originalPatients = Patients(db, original);
            Assert.Multiple(() =>
            {
                Assert.That(originalPatients, Is.EquivalentTo(expected),
                    "The original cohort should return the patients the fake data was built for.");
                Assert.That(Patients(db, rebuilt), Is.EquivalentTo(originalPatients),
                    $"The rebuilt cohort should return the same patients as the original. Script:{Environment.NewLine}{File.ReadAllText(script.FullName)}");
            });
        }
        finally
        {
            if (outDir.Exists) outDir.Delete(true);
        }
    }

    private static string[] Patients(DiscoveredDatabase db, CohortIdentificationConfiguration cic)
    {
        var sql = new CohortQueryBuilder(cic, null).SQL;
        var patients = new List<string>();
        using var con = db.Server.GetConnection();
        con.Open();
        using var cmd = db.Server.GetCommand(sql, con);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            patients.Add(Convert.ToString(reader[0]));
        return patients.OrderBy(p => p).ToArray();
    }

    private AggregateConfiguration AddSet(CohortIdentificationConfiguration cic, CohortAggregateContainer container,
        ICatalogue cata, int order)
    {
        var set = new AggregateConfiguration(CatalogueRepository, cata, cata.Name);
        var chi = cata.GetAllExtractionInformation(ExtractionCategory.Any)
            .Single(e => e.GetRuntimeName().Equals("chi", StringComparison.OrdinalIgnoreCase));
        _ = new AggregateDimension(CatalogueRepository, chi, set);
        cic.EnsureNamingConvention(set);
        container.AddChild(set, order);

        var fc = new AggregateFilterContainer(CatalogueRepository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        return set;
    }

    private AggregateFilter AddFilter(AggregateConfiguration set, string name, string whereSql)
    {
        var filter = new AggregateFilter(CatalogueRepository, name, (AggregateFilterContainer)set.RootFilterContainer)
        {
            WhereSQL = whereSql
        };
        filter.SaveToDatabase();
        return filter;
    }

    private ICatalogue Import(DiscoveredDatabase db, DataTable data)
    {
        var cata = Import(db.CreateTable(data.TableName, data));
        var chi = cata.GetAllExtractionInformation(ExtractionCategory.Any)
            .Single(e => e.GetRuntimeName().Equals("chi", StringComparison.OrdinalIgnoreCase));
        chi.IsExtractionIdentifier = true;
        chi.SaveToDatabase();
        return cata;
    }

    private static DataTable Table(string name, string[] columns, IEnumerable<object[]> rows)
    {
        var dt = new DataTable(name);
        foreach (var c in columns) dt.Columns.Add(c);
        foreach (var r in rows) dt.Rows.Add(r.Select(v => v ?? DBNull.Value).ToArray());
        return dt;
    }

    private static IEnumerable<object[]> Rows(int from, int to, Func<int, object[]> row) =>
        Enumerable.Range(from, to - from + 1).Select(row);

    private static string P(int i) => $"P{i:00}";
}
