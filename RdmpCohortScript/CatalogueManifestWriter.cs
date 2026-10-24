// Writes the catalogue manifest, format 1 (COHORT-SCRIPT-FORMAT.md, section 10): the catalogues, tables and
// projects a build script may refer to, each with id and name, so an author who cannot see the GUI can write a
// script. Every value is a YAML double-quoted scalar, as in the build script.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.Repositories;

namespace RdmpCohortScript;

/// <summary>Writes the catalogue manifest for a cohort, or for chosen catalogues.</summary>
internal sealed class CatalogueManifestWriter
{
    public const string FormatVersion = "1";

    private readonly IRDMPPlatformRepositoryServiceLocator _locator;

    public CatalogueManifestWriter(IRDMPPlatformRepositoryServiceLocator locator)
    {
        _locator = locator;
    }

    /// <summary>The catalogues, forced-join tables and projects that one cohort uses.</summary>
    public string ForCohort(CohortIdentificationConfiguration cic)
    {
        var aggregates = (cic.RootCohortAggregateContainer?.GetAllAggregateConfigurationsRecursively() ??
                          new List<Rdmp.Core.Curation.Data.Aggregation.AggregateConfiguration>())
            .Concat(cic.GetAllJoinables().Select(j => j.AggregateConfiguration)).ToArray();

        // a filter's published source may be on another catalogue; the script names it, so the manifest lists it
        var sourceCatalogues = aggregates.SelectMany(a => FiltersOf(a.RootFilterContainer))
            .Select(f => f.ClonedFromExtractionFilter_ID).Where(id => id != null).Select(id => id.Value).Distinct()
            .SelectMany(id => _locator.CatalogueRepository.GetAllObjectsInIDList<ExtractionFilter>(new[] { id }))
            .Select(f => (Catalogue)f.ExtractionInformation.CatalogueItem.Catalogue);
        var catalogues = aggregates.Select(a => (Catalogue)a.Catalogue).Where(c => c != null)
            .Concat(sourceCatalogues).DistinctBy(c => c.ID).ToArray();
        var tables = aggregates.SelectMany(SavedForcedJoins.Of).DistinctBy(t => t.ID).ToArray();
        var projects = _locator.DataExportRepository?
            .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>("CohortIdentificationConfiguration_ID", cic.ID)
            .Select(a => (IProject)a.Project).ToArray() ?? Array.Empty<IProject>();

        return Write(catalogues, tables, projects);
    }

    /// <summary>The given catalogues, every table they read from, and the projects they are specific to.</summary>
    public string ForCatalogues(IEnumerable<Catalogue> catalogues)
    {
        var list = catalogues.DistinctBy(c => c.ID).ToArray();
        var tables = list.SelectMany(c => c.GetTableInfoList(true)).DistinctBy(t => t.ID).ToArray();
        return Write(list, tables, Array.Empty<IProject>());
    }

    private string Write(IEnumerable<Catalogue> catalogues, IEnumerable<ITableInfo> tables, IEnumerable<IProject> cohortProjects)
    {
        var projects = new Dictionary<int, IProject>();
        foreach (var p in cohortProjects)
            projects[p.ID] = p;

        var sb = new StringBuilder();
        sb.Append("catalogue-manifest-format: ").AppendLine(Q(FormatVersion));
        var catalogueList = catalogues.ToArray();
        sb.AppendLine(catalogueList.Length == 0 ? "catalogues: []" : "catalogues:");
        foreach (var catalogue in catalogueList.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.ID))
        {
            var catalogueProjects = ProjectsOf(catalogue);
            foreach (var p in catalogueProjects)
                projects[p.ID] = p;

            var columns = catalogue.GetAllExtractionInformation(ExtractionCategory.Any)
                .OrderBy(e => e.Order).ThenBy(e => e.ID).ToArray();

            sb.Append("  - id: ").AppendLine(Q(catalogue.ID.ToString()));
            sb.Append("    name: ").AppendLine(Q(catalogue.Name));
            sb.Append("    project-ids: ").AppendLine(List(catalogueProjects.Select(p => p.ID.ToString())));
            sb.Append("    identifier-columns: ")
                .AppendLine(List(columns.Where(e => e.IsExtractionIdentifier).Select(e => e.GetRuntimeName())));
            sb.Append("    columns: ").AppendLine(List(columns.Select(e => e.GetRuntimeName())));

            var filters = catalogue.GetAllFilters().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.ID).ToArray();
            if (filters.Length == 0)
            {
                sb.AppendLine("    filters: []");
                continue;
            }

            sb.AppendLine("    filters:");
            foreach (var filter in filters)
            {
                sb.Append("      - id: ").AppendLine(Q(filter.ID.ToString()));
                sb.Append("        name: ").AppendLine(Q(filter.Name));
                sb.Append("        mandatory: ").AppendLine(Q(filter.IsMandatory ? "true" : "false"));
                sb.Append("        where: ").AppendLine(Q(filter.WhereSQL));
                var parameters = filter.GetAllParameters();
                if (parameters.Length == 0)
                {
                    sb.AppendLine("        parameters: []");
                    continue;
                }

                sb.AppendLine("        parameters:");
                foreach (var parameter in parameters.OrderBy(p => p.ParameterName, StringComparer.Ordinal))
                {
                    sb.Append("          - name: ").AppendLine(Q(parameter.ParameterName));
                    sb.Append("            declaration: ").AppendLine(Q(parameter.ParameterSQL));
                    sb.Append("            value: ").AppendLine(Q(parameter.Value));
                    sb.Append("            comment: ").AppendLine(Q(parameter.Comment));
                }
            }
        }

        WriteIdNameList(sb, "tables", tables.OrderBy(t => t.ID).Select(t => (t.ID, t.Name)));
        WriteIdNameList(sb, "projects", projects.Values.OrderBy(p => p.ID).Select(p => (p.ID, p.Name)));
        return sb.ToString();
    }

    private static IEnumerable<Rdmp.Core.Curation.Data.Aggregation.AggregateFilter> FiltersOf(IContainer container) =>
        container == null
            ? Enumerable.Empty<Rdmp.Core.Curation.Data.Aggregation.AggregateFilter>()
            : container.GetFilters().OfType<Rdmp.Core.Curation.Data.Aggregation.AggregateFilter>()
                .Concat(container.GetSubContainers().SelectMany(FiltersOf));

    private IProject[] ProjectsOf(Catalogue catalogue) =>
        _locator.DataExportRepository?
            .GetAllObjectsWhere<ExtractableDataSet>("Catalogue_ID", catalogue.ID)
            .SelectMany(eds => eds.Projects).DistinctBy(p => p.ID).OrderBy(p => p.ID).ToArray() ?? Array.Empty<IProject>();

    private static void WriteIdNameList(StringBuilder sb, string key, IEnumerable<(int Id, string Name)> items)
    {
        var list = items.ToArray();
        if (list.Length == 0)
        {
            sb.Append(key).AppendLine(": []");
            return;
        }

        sb.Append(key).AppendLine(":");
        foreach (var (id, name) in list)
        {
            sb.Append("  - id: ").AppendLine(Q(id.ToString()));
            sb.Append("    name: ").AppendLine(Q(name));
        }
    }

    private static string List(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(Q))}]";

    private static string Q(string value) => YamlText.DoubleQuoted(value ?? "");
}
