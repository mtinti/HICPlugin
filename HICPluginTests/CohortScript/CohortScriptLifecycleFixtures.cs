// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.Repositories;

namespace HICPluginTests;

// The fixtures of the lifecycle tests. Each states its rows here, and the test states the expected answer,
// worked out by hand from these rows.
public abstract partial class CohortScriptLifecycleBase
{
    /// <summary>
    /// F1: patients P01 to P16. A holds 1 to 8, with P01 twice; B holds 3, 4, 6, 8, 9, 10; C holds 2, 4, 8, 10, 12;
    /// All holds 1 to 16; None is All with a filter that matches nobody.
    /// </summary>
    public static class SetOperations
    {
        public abstract record Node;

        public sealed record Leaf(string Table, string Filter = null) : Node;

        public sealed record Expression(SetOperation Operation, params Node[] Children) : Node;

        public sealed record LeafSet(Leaf A, Leaf B, Leaf C, Leaf All, Leaf None);

        public sealed record TableSet(ICatalogue A, ICatalogue B, ICatalogue C, ICatalogue All);

        public static readonly LeafSet Leaves = new(new("A"), new("B"), new("C"), new("All"), new("All", "chi = 'nobody'"));

        public static readonly Expression AExceptB = new(SetOperation.EXCEPT, Leaves.A, Leaves.B);

        public static TableSet Tables(CohortScriptLifecycleBase t) => new(
            t.Patients(Unique("a"), new[] { 1, 1, 2, 3, 4, 5, 6, 7, 8 }),
            t.Patients(Unique("b"), new[] { 3, 4, 6, 8, 9, 10 }),
            t.Patients(Unique("c"), new[] { 2, 4, 8, 10, 12 }),
            t.Patients(Unique("all"), Enumerable.Range(1, 16)));

        /// <summary>
        /// Builds the expression as a cohort. With <paramref name="reverseInsertion"/>, each container's children are
        /// added last first, with the same saved order, so a rebuild cannot depend on the order objects were made in.
        /// </summary>
        public static (CohortIdentificationConfiguration Cohort, TableSet Tables) Build(CohortScriptLifecycleBase t,
            string name, Expression expression, bool reverseInsertion = false)
        {
            var tables = Tables(t);
            var cic = new CohortIdentificationConfiguration(t.CatalogueRepository, name);
            cic.CreateRootContainerIfNotExists();
            Fill(cic.RootCohortAggregateContainer, expression);
            return (cic, tables);

            void Fill(CohortAggregateContainer container, Expression e)
            {
                container.Operation = e.Operation;
                container.SaveToDatabase();
                var children = new IOrderable[e.Children.Length];
                var positions = Enumerable.Range(0, e.Children.Length);
                foreach (var i in reverseInsertion ? positions.Reverse() : positions)
                    switch (e.Children[i])
                    {
                        case Leaf leaf:
                            var set = t.AddSet(cic, container, Catalogue(leaf.Table), i);
                            if (leaf.Filter != null)
                                t.AddFilter((AggregateFilterContainer)set.RootFilterContainer, "Nobody", leaf.Filter);
                            children[i] = set;
                            break;
                        case Expression sub:
                            var child = t.AddContainer(container, sub.Operation, $"Container {i}", i);
                            Fill(child, sub);
                            children[i] = child;
                            break;
                    }

                SetOrders(children);
            }

            ICatalogue Catalogue(string table) => table switch
            {
                "A" => tables.A,
                "B" => tables.B,
                "C" => tables.C,
                _ => tables.All
            };
        }
    }

    /// <summary>
    /// F3: an admission on 2020-01-01 for P01 to P05 and P07 to P09, and drug A prescribed from the admission to
    /// @window days after it, both days included. With 90 days the last day is 2020-03-31, as 2020 is a leap year.
    /// </summary>
    public static class AdmissionWindow
    {
        public sealed record Fixture(CohortIdentificationConfiguration Cohort, AnyTableSqlParameter Window);

        public static (ICatalogue Admissions, ICatalogue Prescribing) Tables(CohortScriptLifecycleBase t)
        {
            var admitted = new DateTime(2020, 1, 1);
            var admissions = t.Import(Unique("adm"), new[] { Chi, ("admission_date", typeof(DateTime)) },
                new[] { 1, 2, 3, 4, 5, 7, 8, 9 }.Select(p => new object[] { P(p), admitted }));
            var prescribing = t.Import(Unique("presc"), new[] { Chi, ("drug", typeof(string)), ("prescribed_date", typeof(DateTime)) },
                new[]
                {
                    new object[] { P(1), "A", new DateTime(2019, 12, 31) }, // the day before the admission
                    new object[] { P(2), "A", new DateTime(2020, 1, 1) },   // the day of the admission
                    new object[] { P(3), "A", new DateTime(2020, 3, 31) },  // day 90
                    new object[] { P(4), "A", new DateTime(2020, 4, 1) },   // day 91
                    new object[] { P(5), "A", new DateTime(2020, 2, 1) },   // twice, the same row
                    new object[] { P(5), "A", new DateTime(2020, 2, 1) },
                    new object[] { P(6), "A", new DateTime(2020, 2, 1) },   // no admission
                    new object[] { P(7), "A", null },                       // no date
                    new object[] { P(8), "B", new DateTime(2020, 2, 1) },   // another drug
                    new object[] { P(9), null, new DateTime(2020, 2, 1) }   // no drug
                });
            return (admissions, prescribing);
        }

        public static Fixture Build(CohortScriptLifecycleBase t, string name)
        {
            var (admissions, prescribing) = Tables(t);
            var cic = new CohortIdentificationConfiguration(t.CatalogueRepository, name);
            cic.CreateRootContainerIfNotExists();
            var root = cic.RootCohortAggregateContainer;
            root.Operation = SetOperation.UNION;
            root.SaveToDatabase();

            var joinable = t.AddPatientIndexTable(cic, admissions, "admission_date");
            var set = t.AddSet(cic, root, prescribing, 0);
            Join(joinable, set);
            var window = new AnyTableSqlParameter(t.CatalogueRepository, set, "DECLARE @window AS int") { Value = "90" };
            window.SaveToDatabase();
            var filters = (AggregateFilterContainer)set.RootFilterContainer;
            t.AddFilter(filters, "Within the window",
                $"prescribed_date BETWEEN ix{joinable.ID}.admission_date AND DATEADD(day, @window, ix{joinable.ID}.admission_date)");
            t.AddFilter(filters, "Drug A", "drug = 'A'");
            return new Fixture(cic, window);
        }
    }

    /// <summary>
    /// F4: one cohort with the saved features that F1 to F3 do not have. Root EXCEPT of Inclusion (INTERSECT) and
    /// Exclusion (UNION).
    /// Inclusion:
    /// - drug A since the cohort's @since (2020-01-01), through a filter parameter @drug, a two-line filter, an OR
    ///   container with a disabled filter, and a forced join: P01, P04, P05, P06 (P02 only if the disabled filter
    ///   is enabled);
    /// - at least @visits (2) visits, by HAVING: P01, P02, P04, P06, P08;
    /// - mothers, through the catalogue's second identifier column: P01, P02, P04, P06, P09.
    /// Exclusion:
    /// - died after an admission since @pitfrom (2019-01-01), joined to a patient index table: P04;
    /// - everyone, but the set is disabled; everyone again, in a disabled container.
    /// The cohort returns P01 and P06. It also has a description, a ticket and two project links.
    /// </summary>
    public sealed class SavedFeatures
    {
        public CohortIdentificationConfiguration Cohort { get; private init; }
        public AggregateFilter DisabledFilter { get; private init; }
        public AggregateFilter DeathFilter { get; private init; }

        private int[] _projects;
        private int _forcedTable;
        private string[] _inclusion;
        private string[] _exclusion;
        private string _motherColumn;

        public static SavedFeatures Build(CohortScriptLifecycleBase t)
        {
            var repository = t.CatalogueRepository;
            var day = (Func<int, int, int, DateTime>)((y, m, d) => new DateTime(y, m, d));

            var prescribing = t.Import(Unique("f4presc"), new[] { Chi, ("drug", typeof(string)), ("prescribed_date", typeof(DateTime)) },
                new[]
                {
                    new object[] { P(1), "A", day(2021, 1, 1) },
                    new object[] { P(2), "A", day(2019, 6, 1) }, // before @since
                    new object[] { P(3), "B", day(2021, 1, 1) }, // another drug
                    new object[] { P(4), "A", day(2021, 2, 1) },
                    new object[] { P(5), "A", day(2021, 3, 1) },
                    new object[] { P(6), "A", day(2021, 3, 1) }
                });
            var visits = t.Patients(Unique("f4visits"), new[] { 1, 1, 2, 2, 4, 4, 4, 5, 6, 6, 8, 8 });
            var births = t.Import(Unique("f4births"), new[] { Chi, ("mother_chi", typeof(string)) }, new[]
            {
                new object[] { "C1", P(1) }, new object[] { "C2", P(4) }, new object[] { "C3", P(6) },
                new object[] { "C4", P(9) }, new object[] { "C5", P(2) }
            }, "mother_chi");
            var admissions = t.Import(Unique("f4adm"), new[] { Chi, ("admission_date", typeof(DateTime)) }, new[]
            {
                new object[] { P(4), day(2020, 1, 1) }, new object[] { P(6), day(2020, 1, 1) },
                new object[] { P(10), day(2018, 6, 1) } // before @pitfrom, so not in the patient index table
            });
            var deaths = t.Import(Unique("f4deaths"), new[] { Chi, ("date_of_death", typeof(DateTime)) }, new[]
            {
                new object[] { P(4), day(2021, 5, 1) },  // after the admission
                new object[] { P(6), day(2018, 1, 1) },  // before the admission
                new object[] { P(10), day(2021, 1, 1) }  // no admission in the table
            });
            var everyone = t.Patients(Unique("f4all"), Enumerable.Range(1, 10));
            var registry = t.Import(Unique("f4reg"), new[] { Chi, ("practice", typeof(string)) },
                Enumerable.Range(1, 6).Select(p => new object[] { P(p), "X" }));

            var cic = new CohortIdentificationConfiguration(repository, $"F4 {Guid.NewGuid():N}"[..12])
            {
                Description = "Every saved feature.\nTwo lines.",
                Ticket = "LC-4"
            };
            cic.SaveToDatabase();
            cic.CreateRootContainerIfNotExists();
            new AnyTableSqlParameter(repository, cic, "DECLARE @since AS date") { Value = "'2020-01-01'" }.SaveToDatabase();

            var projects = new[] { "one", "two" }.Select(n =>
            {
                var project = new Project(t.DataExportRepository, $"F4 project {n} {Guid.NewGuid():N}") { ProjectNumber = t.NextProjectNumber() };
                project.SaveToDatabase();
                _ = new ProjectCohortIdentificationConfigurationAssociation(t.DataExportRepository, project, cic);
                return project.ID;
            }).ToArray();

            var root = cic.RootCohortAggregateContainer;
            root.Operation = SetOperation.EXCEPT;
            root.SaveToDatabase();
            var inclusion = t.AddContainer(root, SetOperation.INTERSECT, "Inclusion", 0);
            var exclusion = t.AddContainer(root, SetOperation.UNION, "Exclusion", 1);

            // drug A since @since
            var drug = t.AddSet(cic, inclusion, prescribing, 0);
            drug.Description = "Drug A since the study start";
            drug.SaveToDatabase();
            var drugFilters = (AggregateFilterContainer)drug.RootFilterContainer;
            var drugFilter = t.AddFilter(drugFilters, "Drug", "drug = @drug");
            var drugParameter = drugFilter.GetFilterFactory().CreateNewParameter(drugFilter, "DECLARE @drug AS varchar(5)");
            drugParameter.Value = "'A'";
            drugParameter.SaveToDatabase();
            var notBlank = t.AddFilter(drugFilters, "Not blank", "prescribed_date IS NOT NULL\nAND drug IS NOT NULL");
            notBlank.Description = "Two lines of SQL";
            notBlank.SaveToDatabase();
            var when = t.AddFilterContainer(drugFilters, FilterContainerOperation.OR);
            t.AddFilter(when, "Since the study start", "prescribed_date >= @since");
            var anyDate = t.AddFilter(when, "Any date", "prescribed_date >= '1900-01-01'");
            anyDate.IsDisabled = true;
            anyDate.SaveToDatabase();

            // a forced join to the registry, through a join from its identifier to the prescriptions' identifier
            var registryTable = registry.GetTableInfoList(false).Single();
            _ = new JoinInfo(repository, (ColumnInfo)IdentifierOf(registry).ColumnInfo, (ColumnInfo)IdentifierOf(prescribing).ColumnInfo,
                ExtractionJoinType.Left, null);
            repository.AggregateForcedJoinManager.CreateLinkBetween(drug, (TableInfo)registryTable);

            // at least @visits visits
            var frequent = t.AddSet(cic, inclusion, visits, 1);
            frequent.HavingSQL = "count(*) >= @visits";
            frequent.SaveToDatabase();
            new AnyTableSqlParameter(repository, frequent, "DECLARE @visits AS int") { Value = "2" }.SaveToDatabase();

            // mothers
            var mothers = t.AddSet(cic, inclusion, births, 2, "mother_chi");

            // died after an admission since @pitfrom
            var joinable = t.AddPatientIndexTable(cic, admissions, "admission_date");
            var table = joinable.AggregateConfiguration;
            new AnyTableSqlParameter(repository, table, "DECLARE @pitfrom AS date") { Value = "'2019-01-01'" }.SaveToDatabase();
            var tableFilters = new AggregateFilterContainer(repository, FilterContainerOperation.AND);
            table.RootFilterContainer_ID = tableFilters.ID;
            table.SaveToDatabase();
            t.AddFilter(tableFilters, "Admitted since", "admission_date >= @pitfrom");

            var died = t.AddSet(cic, exclusion, deaths, 0);
            Join(joinable, died);
            var deathFilter = t.AddFilter((AggregateFilterContainer)died.RootFilterContainer, "Died after an admission",
                $"date_of_death >= ix{joinable.ID}.admission_date");

            var disabledSet = t.AddSet(cic, exclusion, everyone, 1);
            disabledSet.IsDisabled = true;
            disabledSet.SaveToDatabase();
            var notUsed = t.AddContainer(exclusion, SetOperation.UNION, "Not used", 2);
            notUsed.IsDisabled = true;
            notUsed.SaveToDatabase();
            t.AddSet(cic, notUsed, everyone, 0);

            SetOrders(inclusion, exclusion);
            SetOrders(drug, frequent, mothers);
            SetOrders(died, disabledSet, notUsed);

            return new SavedFeatures
            {
                Cohort = cic,
                DisabledFilter = anyDate,
                DeathFilter = deathFilter,
                _projects = projects,
                _forcedTable = registryTable.ID,
                _inclusion = new[] { drug.Name, frequent.Name, mothers.Name },
                _exclusion = new[] { died.Name, disabledSet.Name, "Not used" },
                _motherColumn = "mother_chi"
            };
        }

        private static ExtractionInformation IdentifierOf(ICatalogue cata) => cata.GetAllExtractionInformation(ExtractionCategory.Any)
            .Single(e => e.GetRuntimeName() == "chi");

        /// <summary>
        /// What B0 must hold before it becomes the reference for later generations, read through the given
        /// repositories. Each problem names the feature that is missing or wrong.
        /// </summary>
        public List<string> ExpectedStructure(IRDMPPlatformRepositoryServiceLocator locator)
        {
            var problems = new List<string>();
            void Expect(bool ok, string what)
            {
                if (!ok) problems.Add($"structure: B0 should have {what}.");
            }

            var repository = locator.CatalogueRepository;
            var cic = repository.GetObjectByID<CohortIdentificationConfiguration>(Cohort.ID);
            Expect(cic.Description == "Every saved feature.\nTwo lines." && cic.Ticket == "LC-4", "its description and ticket");
            Expect(cic.GetAllParameters().SingleOrDefault() is { ParameterName: "@since", Value: "'2020-01-01'" }, "the cohort parameter @since");
            Expect(locator.DataExportRepository
                    .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", cic.ID)
                    .Select(l => l.Project_ID).OrderBy(i => i).SequenceEqual(_projects.OrderBy(i => i)),
                "links to its two projects");

            var root = cic.RootCohortAggregateContainer;
            var top = root.GetOrderedContents().OfType<CohortAggregateContainer>().ToArray();
            Expect(root.Operation == SetOperation.EXCEPT && top.Length == 2 &&
                   top[0] is { Name: "Inclusion", Operation: SetOperation.INTERSECT } &&
                   top[1] is { Name: "Exclusion", Operation: SetOperation.UNION },
                "a root EXCEPT of Inclusion (INTERSECT) and Exclusion (UNION)");
            if (top.Length != 2)
                return problems;

            var inclusion = top[0].GetOrderedContents().OfType<AggregateConfiguration>().ToArray();
            Expect(inclusion.Select(a => a.Name).SequenceEqual(_inclusion), "the drug, visits and mothers sets in Inclusion, in that order");
            var exclusion = top[1].GetOrderedContents().ToArray();
            Expect(exclusion.Select(o => o is AggregateConfiguration a ? a.Name : ((CohortAggregateContainer)o).Name).SequenceEqual(_exclusion),
                "the deaths set, the everyone set and the Not used container in Exclusion, in that order");
            if (inclusion.Length != 3 || exclusion.Length != 3)
                return problems;

            var drug = inclusion[0];
            var drugFilters = (AggregateFilterContainer)drug.RootFilterContainer;
            Expect(drug.Description == "Drug A since the study start", "the drug set's description");
            Expect(drug.ForcedJoins.Select(t => t.ID).SequenceEqual(new[] { _forcedTable }), "a forced join to the registry on the drug set");
            Expect(drugFilters.Operation == FilterContainerOperation.AND &&
                   drugFilters.GetFilters().Select(f => f.Name).OrderBy(n => n).SequenceEqual(new[] { "Drug", "Not blank" }),
                "the Drug and Not blank filters in an AND container");
            Expect(drugFilters.GetFilters().SingleOrDefault(f => f.Name == "Drug")?.GetAllParameters().SingleOrDefault()
                    is { ParameterName: "@drug", Value: "'A'" },
                "the filter parameter @drug with value 'A'");
            Expect(drugFilters.GetFilters().SingleOrDefault(f => f.Name == "Not blank")?.WhereSQL ==
                   "prescribed_date IS NOT NULL\nAND drug IS NOT NULL", "the two-line SQL of the Not blank filter");
            var when = drugFilters.GetSubContainers().Cast<AggregateFilterContainer>().SingleOrDefault();
            Expect(when?.Operation == FilterContainerOperation.OR &&
                   when.GetFilters().Cast<AggregateFilter>().OrderBy(f => f.Name)
                       .Select(f => (f.Name, f.IsDisabled))
                       .SequenceEqual(new[] { ("Any date", true), ("Since the study start", false) }),
                "an OR container with the Since the study start filter and the disabled Any date filter");

            var frequent = inclusion[1];
            Expect(frequent.HavingSQL == "count(*) >= @visits", "HAVING on the visits set");
            Expect(repository.GetAllObjects<AnyTableSqlParameter>()
                    .SingleOrDefault(p => p.ReferencedObjectType == nameof(AggregateConfiguration) && p.ReferencedObjectID == frequent.ID)
                    is { ParameterName: "@visits", Value: "2" },
                "the set parameter @visits with value 2");
            Expect(inclusion[2].AggregateDimensions.SingleOrDefault()?.ExtractionInformation.GetRuntimeName() == _motherColumn,
                "the mothers set on the mother_chi column");

            var died = (AggregateConfiguration)exclusion[0];
            var joinable = cic.GetAllJoinables().SingleOrDefault();
            Expect(joinable != null && died.PatientIndexJoinablesUsed.SingleOrDefault() is { } use &&
                   use.JoinableCohortAggregateConfiguration_ID == joinable.ID && use.JoinType == ExtractionJoinType.Inner,
                "the deaths set joined to the patient index table with an inner join");
            if (joinable != null)
            {
                var table = joinable.AggregateConfiguration;
                Expect(repository.GetAllObjects<AnyTableSqlParameter>()
                        .SingleOrDefault(p => p.ReferencedObjectType == nameof(AggregateConfiguration) && p.ReferencedObjectID == table.ID)
                        is { ParameterName: "@pitfrom" },
                    "the parameter @pitfrom on the patient index table");
                Expect(table.RootFilterContainer?.GetFilters().SingleOrDefault()?.WhereSQL == "admission_date >= @pitfrom",
                    "the filter of the patient index table");
            }

            Expect(exclusion[1] is AggregateConfiguration { IsDisabled: true }, "the everyone set disabled");
            Expect(exclusion[2] is CohortAggregateContainer { IsDisabled: true } c &&
                   c.GetAggregateConfigurations().Length == 1, "the Not used container disabled, with one set");
            return problems;
        }

        /// <summary>Saves the cohort and every object in it, unchanged, as a save of the whole cohort would.</summary>
        public static void SaveEverything(CohortIdentificationConfiguration cic)
        {
            cic.SaveToDatabase();
            foreach (var p in cic.GetAllParameters())
                p.SaveToDatabase();
            var aggregates = cic.RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively()
                .Concat(cic.GetAllJoinables().Select(j => j.AggregateConfiguration));
            foreach (var container in cic.RootCohortAggregateContainer.GetAllSubContainersRecursively()
                         .Append(cic.RootCohortAggregateContainer))
                container.SaveToDatabase();
            foreach (var a in aggregates)
            {
                a.SaveToDatabase();
                foreach (var d in a.AggregateDimensions)
                    d.SaveToDatabase();
                if (a.RootFilterContainer is not AggregateFilterContainer root)
                    continue;
                foreach (var fc in root.GetAllSubContainersRecursively().Cast<AggregateFilterContainer>().Append(root))
                {
                    fc.SaveToDatabase();
                    foreach (var f in fc.GetFilters().Cast<AggregateFilter>())
                    {
                        f.SaveToDatabase();
                        foreach (var p in f.GetAllParameters())
                            p.SaveToDatabase();
                    }
                }
            }
        }
    }
}
