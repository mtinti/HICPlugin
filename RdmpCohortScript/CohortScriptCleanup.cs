// Removes a partly built cohort after a rebuild fails or is cancelled. RDMP's own deletes do not do this in one
// step: deleting a cohort deletes its container tree but leaves its sets, and a patient index table cannot be
// deleted while a set joins it. The clean-up therefore collects what belongs to the new cohort, and only that,
// because other people may be working on the same platform: its tree, its patient index tables, every set with
// its cic_<id>_ prefix, and everything hanging off those. It deletes them children first and reports what it
// removed, and anything it could not remove.

using System;
using System.Collections.Generic;
using System.Linq;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.Repositories;

namespace RdmpCohortScript;

/// <summary>Deletes a partly built cohort and everything that belongs to it.</summary>
internal sealed class CohortScriptCleanup
{
    private readonly IRDMPPlatformRepositoryServiceLocator _locator;
    private readonly List<string> _failed = new();
    private int _removed;

    public CohortScriptCleanup(IRDMPPlatformRepositoryServiceLocator locator)
    {
        _locator = locator;
    }

    /// <summary>
    /// Deletes the cohort, if there is one, and then anything else the run saved that still exists. Returns a
    /// sentence that says what happened, for the end of an error message.
    /// </summary>
    public string Remove(CohortIdentificationConfiguration cic, IReadOnlyList<IMapsDirectlyToDatabaseTable> madeByTheRun)
    {
        if (cic != null)
            RemoveCohort(cic);

        // what the run saved and the cohort's clean-up did not reach, newest first
        foreach (var leftover in madeByTheRun.Reverse())
            if (leftover is IDeleteable deleteable and Rdmp.Core.MapsDirectlyToDatabaseTable.Revertable.IRevertable revertable &&
                StillThere(revertable))
                Delete(deleteable, $"{leftover.GetType().Name} {leftover.ID}");

        var what = cic == null ? "The objects this rebuild saved were removed" : $"The partly built cohort {cic.ID} \"{cic.Name}\" was removed";
        return _failed.Count == 0
            ? $"{what}, with {_removed} thing(s) in all (objects and links)."
            : $"{what} only in part. Still there: {string.Join("; ", _failed)}. Delete them by hand.";
    }

    private static bool StillThere(Rdmp.Core.MapsDirectlyToDatabaseTable.Revertable.IRevertable o)
    {
        try
        {
            return o.Exists();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RemoveCohort(CohortIdentificationConfiguration cic)
    {
        var repository = _locator.CatalogueRepository;
        var root = cic.RootCohortAggregateContainer;
        var containers = root == null ? new List<CohortAggregateContainer>() : AllContainers(root);
        var joinables = cic.GetAllJoinables();
        var prefix = $"cic_{cic.ID}_";
        var sets = containers.SelectMany(c => c.GetAggregateConfigurations())
            .Concat(joinables.Select(j => j.AggregateConfiguration))
            .Concat(repository.GetAllObjects<AggregateConfiguration>().Where(a => a.Name?.StartsWith(prefix, StringComparison.Ordinal) == true))
            .Where(a => a != null).DistinctBy(a => a.ID).ToArray();

        // the links between objects go first, so nothing below is held by another object
        foreach (var use in joinables.SelectMany(j => j.Users))
            Delete(use, $"join use {use.ID}");
        foreach (var set in sets)
        {
            foreach (var table in set.ForcedJoins)
                Try($"forced join of set {set.ID} to table {table.ID}",
                    () => repository.AggregateForcedJoinManager.BreakLinkBetween(set, table));
            if (set.GetTopXIfAny() is { } topX)
                Delete(topX, $"Top X of set {set.ID}");
        }

        // filter trees, children first
        foreach (var set in sets)
            if (set.RootFilterContainer_ID is { } fcId && set.OverrideFiltersByUsingParentAggregateConfigurationInstead_ID == null &&
                repository.GetAllObjectsInIDList<AggregateFilterContainer>(new[] { fcId }).SingleOrDefault() is { } fc)
                DeleteFilterTree(fc);

        foreach (var parameter in repository.GetAllObjects<AnyTableSqlParameter>()
                     .Where(p => (p.ReferencedObjectType == nameof(AggregateConfiguration) && sets.Any(s => s.ID == p.ReferencedObjectID)) ||
                                 (p.ReferencedObjectType == nameof(CohortIdentificationConfiguration) && p.ReferencedObjectID == cic.ID)))
            Delete(parameter, $"parameter {parameter.ParameterName}");

        foreach (var joinable in joinables)
            Delete(joinable, $"patient index table {joinable.ID}");
        foreach (var set in sets)
        {
            foreach (var dimension in repository.GetAllObjectsWhere<AggregateDimension>("AggregateConfiguration_ID", set.ID))
                Delete(dimension, $"column {dimension.ID} of set {set.ID}");
            Delete(set, $"set {set.ID}");
        }

        if (_locator.DataExportRepository is { } dataExport)
            foreach (var link in dataExport.GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>(
                         "CohortIdentificationConfiguration_ID", cic.ID))
                Delete(link, $"link to project {link.Project_ID}");

        // the cohort deletes its root container, which deletes the containers below it
        Delete(cic, $"cohort {cic.ID}");
    }

    private void DeleteFilterTree(AggregateFilterContainer container)
    {
        foreach (var child in container.GetSubContainers().OfType<AggregateFilterContainer>())
            DeleteFilterTree(child);
        foreach (var filter in container.GetFilters().OfType<AggregateFilter>())
        {
            foreach (var parameter in filter.GetAllParameters().OfType<AggregateFilterParameter>())
                Delete(parameter, $"parameter {parameter.ParameterName} of filter {filter.ID}");
            Delete(filter, $"filter {filter.ID}");
        }

        Delete(container, $"filter container {container.ID}");
    }

    private static List<CohortAggregateContainer> AllContainers(CohortAggregateContainer root)
    {
        var all = new List<CohortAggregateContainer> { root };
        foreach (var child in root.GetSubContainers())
            all.AddRange(AllContainers(child));
        return all;
    }

    private void Delete(IDeleteable deleteable, string what) => Try(what, deleteable.DeleteInDatabase);

    private void Try(string what, Action action)
    {
        try
        {
            action();
            _removed++;
        }
        catch (Exception e)
        {
            _failed.Add($"{what} ({e.Message})");
        }
    }
}
