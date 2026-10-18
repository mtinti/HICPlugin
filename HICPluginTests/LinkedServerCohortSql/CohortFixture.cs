// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FAnsi;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.Repositories;

namespace Rdmp.LinkedServerCohortSql.Tests;

internal sealed class CohortFixture
{
    public ICatalogueRepository Repository { get; }
    public CohortIdentificationConfiguration Cic { get; }
    public CohortAggregateContainer Root => Cic.RootCohortAggregateContainer;

    public CohortFixture(ICatalogueRepository repository)
    {
        Repository = repository;
        Cic = new CohortIdentificationConfiguration(repository, "Linked cohort");
        Cic.CreateRootContainerIfNotExists();
    }

    public AggregateConfiguration Set(string name, string server = "home", string database = "HomeDb",
        string idType = "int", string collation = null, bool addToRoot = true)
    {
        var table = new TableInfo(Repository, $"[{database.Replace("]", "]]")}].[dbo].[{name}]")
        { Server = server, Database = "[" + database + "]", Schema = "dbo", DatabaseType = DatabaseType.MicrosoftSQLServer };
        table.SaveToDatabase();
        var catalogue = new Catalogue(Repository, name);
        var set = new AggregateConfiguration(Repository, catalogue, $"cic_{Cic.ID}_{name}") { CountSQL = null };
        set.SaveToDatabase();
        Dimension(set, table, "id", idType, collation, identifier: true);
        if (addToRoot) Root.AddChild(set, Root.GetOrderedContents().Count() + 1);
        return set;
    }

    public AggregateDimension Dimension(AggregateConfiguration set, TableInfo table, string name,
        string type, string collation = null, bool identifier = false)
    {
        var column = new ColumnInfo(Repository, table.Name + ".[" + name + "]", type, table) { Collation = collation };
        column.SaveToDatabase();
        var item = new CatalogueItem(Repository, set.Catalogue, name);
        var extraction = new ExtractionInformation(Repository, item, column, column.Name) { IsExtractionIdentifier = identifier };
        extraction.SaveToDatabase();
        var dimension = new AggregateDimension(Repository, extraction, set);
        set.ClearAllInjections();
        set.Catalogue.ClearAllInjections();
        return dimension;
    }

    /// <summary>Adds a non-identifier dimension on the set's own table with an explicit Order.</summary>
    public AggregateDimension AddDimension(AggregateConfiguration set, string name, string type, int order, string collation = null)
    {
        var table = (TableInfo)set.Catalogue.GetTableInfoList(false).Single();
        var dimension = Dimension(set, table, name, type, collation);
        dimension.Order = order;
        dimension.SaveToDatabase();
        set.ClearAllInjections();
        return dimension;
    }

    public AggregateFilter Filter(AggregateConfiguration set, string sql)
    {
        var container = new AggregateFilterContainer(Repository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = container.ID;
        set.SaveToDatabase();
        var filter = new AggregateFilter(Repository, "Filter", container) { WhereSQL = sql };
        filter.SaveToDatabase();
        return filter;
    }

    public AnyTableSqlParameter Parameter(IMapsDirectlyToDatabaseTable parent, string name, string value, string type = "int")
    {
        var parameter = new AnyTableSqlParameter(Repository, parent, $"DECLARE {name} AS {type}") { Value = value };
        parameter.SaveToDatabase();
        return parameter;
    }

    public JoinableCohortAggregateConfigurationUse Join(AggregateConfiguration user, AggregateConfiguration pit)
    {
        var joinable = pit.JoinableCohortAggregateConfiguration ?? new JoinableCohortAggregateConfiguration(Repository, Cic, pit);
        pit.ClearAllInjections();
        return joinable.AddUser(user);
    }

    public static TableInfo Table(AggregateConfiguration set) => set.AggregateDimensions[0].ColumnInfo.TableInfo;
    public LinkedServerCohortSqlResult Build()
    {
        foreach (var set in Repository.GetAllObjects<AggregateConfiguration>()) set.ReFetchOrder();
        return new LinkedServerCohortSqlBuilder().Build(Cic);
    }

    public LinkedServerCohortSqlResult BuildStaged()
    {
        foreach (var set in Repository.GetAllObjects<AggregateConfiguration>()) set.ReFetchOrder();
        return new LinkedServerCohortSqlBuilder().Build(Cic, staged: true);
    }
}
