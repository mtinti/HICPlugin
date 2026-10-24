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
using NUnit.Framework;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using RdmpCohortScript;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// Which catalogues a cohort may use, checked on a real platform database. In memory the catalogue and the data
/// export metadata share one repository, which hides the case these tests cover: a catalogue loaded from the
/// catalogue database does not know its project, so RDMP's own check passes it silently.
/// </summary>
public class CohortScriptProjectCatalogueTests : DatabaseTests
{
    private Catalogue _catalogue;
    private Project _ownProject;
    private Project _otherProject;
    private CohortIdentificationConfiguration _cic;
    private PromptRefusingActivator _activator;

    [SetUp]
    public void MakeProjectSpecificCatalogue()
    {
        var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);
        var data = new DataTable("cs_project_data");
        data.Columns.Add("chi");
        data.Rows.Add("P01");
        var imported = Import(db.CreateTable(data.TableName, data));
        var chi = imported.GetAllExtractionInformation(ExtractionCategory.Any).Single();
        chi.IsExtractionIdentifier = true;
        chi.SaveToDatabase();

        _ownProject = new Project(DataExportRepository, "Own project");
        _otherProject = new Project(DataExportRepository, "Other project");
        var dataset = new ExtractableDataSet(DataExportRepository, imported);
        _ = new ExtractableDataSetProject(DataExportRepository, dataset, _ownProject);

        // load the catalogue as a script line would get it: from the catalogue database, fresh
        _catalogue = CatalogueRepository.GetObjectByID<Catalogue>(imported.ID);

        _cic = new CohortIdentificationConfiguration(CatalogueRepository, $"Project check {Guid.NewGuid():N}");
        _cic.CreateRootContainerIfNotExists();
        _activator = new PromptRefusingActivator(RepositoryLocator);
    }

    [TestCase("AddCatalogueToCohortSet")]
    [TestCase("AddPatientIndexTable")]
    public void AProjectSpecificCatalogueIsRefusedWhenTheCohortIsNotLinkedToAnyProject(string command)
    {
        var refusal = Refusal(command);

        Assert.That(refusal, Is.EqualTo(CohortScriptRefusal.ProjectNotLinked),
            "A cohort with no project link should not use a project-specific catalogue.");
    }

    [TestCase("AddCatalogueToCohortSet")]
    [TestCase("AddPatientIndexTable")]
    public void AProjectSpecificCatalogueIsRefusedWhenTheCohortIsLinkedToAnotherProject(string command)
    {
        _ = new ProjectCohortIdentificationConfigurationAssociation(DataExportRepository, _otherProject, _cic);

        var refusal = Refusal(command);

        Assert.That(refusal, Is.EqualTo(CohortScriptRefusal.ProjectNotLinked),
            "A cohort linked to another project should not use the catalogue.");
    }

    [TestCase("AddCatalogueToCohortSet")]
    [TestCase("AddPatientIndexTable")]
    public void AProjectSpecificCatalogueIsAcceptedWhenTheCohortIsLinkedToItsProject(string command)
    {
        _ = new ProjectCohortIdentificationConfigurationAssociation(DataExportRepository, _ownProject, _cic);

        var result = CommandLine.RunCommand(_activator, Line(command));

        Assert.That(result.Completed, Is.True,
            $"A cohort linked to the catalogue's project should use it. RDMP said: {result.ImpossibleReason}");
    }

    [TestCase("AddCatalogueToCohortSet")]
    [TestCase("AddPatientIndexTable")]
    public void AnInternalDatasetIsRefused(string command)
    {
        _ = new ProjectCohortIdentificationConfigurationAssociation(DataExportRepository, _ownProject, _cic);
        _catalogue.IsInternalDataset = true;
        _catalogue.SaveToDatabase();

        var refusal = Refusal(command);

        Assert.That(refusal, Is.EqualTo(CohortScriptRefusal.InternalDataset), "A cohort should not use an internal dataset.");
    }

    private CohortScriptRefusal? Refusal(string command)
    {
        var result = CommandLine.RunCommand(_activator, Line(command));
        Assert.That(result.Completed, Is.False, "The line should not run.");
        return ((CohortScriptCommand)result.Command).Refusal;
    }

    private string[] Line(string command) => command == "AddCatalogueToCohortSet"
        ? new[] { command, $"CohortAggregateContainer:{_cic.RootCohortAggregateContainer_ID}", $"Catalogue:{_catalogue.ID}", "null", "null", "false" }
        : new[] { command, $"CohortIdentificationConfiguration:{_cic.ID}", $"Catalogue:{_catalogue.ID}", "null", "null", "null", "false" };
}
