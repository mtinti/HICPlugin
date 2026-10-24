// Removes a partly built cohort after a rebuild fails or is cancelled. RDMP's own deletes do not do this in one
// step: deleting a cohort deletes its container tree but leaves its sets, and a patient index table cannot be
// deleted while a set joins it. The clean-up therefore collects what belongs to the new cohort, and only that,
// because other people may be working on the same platform: its tree, its patient index tables, every set with
// its cic_<id>_ prefix, and everything hanging off those. It deletes them children first and reports what it
// removed, anything it could not remove, and anything it could not check.

using System;
using System.Collections.Generic;
using System.Linq;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.MapsDirectlyToDatabaseTable.Revertable;
using Rdmp.Core.Repositories;

namespace RdmpCohortScript;

/// <summary>What a clean-up removed, what is still there, and what it could not check.</summary>
public sealed class CohortScriptCleanupResult
{
    /// <summary>How many objects and links the clean-up removed.</summary>
    public int Removed { get; init; }

    /// <summary>The objects and links still there after the clean-up, each with the reason when there is one.</summary>
    public IReadOnlyList<string> Leftovers { get; init; } = Array.Empty<string>();

    /// <summary>What the clean-up could not check, because reading the platform failed.</summary>
    public IReadOnlyList<string> Unverified { get; init; } = Array.Empty<string>();

    /// <summary>The cohort the rebuild made, kept even after it is deleted, or null when there was none.</summary>
    public CohortIdentificationConfiguration Cohort { get; init; }

    /// <summary>What happened, for the end of an error message.</summary>
    public string Sentence { get; init; }

    /// <summary>True when something may still be there, so RDMP's list of objects should be refreshed.</summary>
    public bool Incomplete => Leftovers.Count > 0 || Unverified.Count > 0;
}

/// <summary>Deletes a partly built cohort and everything that belongs to it.</summary>
internal sealed class CohortScriptCleanup
{
    private readonly IRDMPPlatformRepositoryServiceLocator _locator;

    // every object the clean-up tried to delete, by type and id. One whose delete failed is checked again at the end,
    // because a later pass may remove it.
    private readonly Dictionary<string, (IRevertable Object, string What, string Error)> _tried = new();

    // forced joins whose removal failed, checked again at the end: deleting the set also deletes its forced joins
    private readonly List<(AggregateConfiguration Set, ITableInfo Table, string What, string Error)> _failedJoins = new();

    // deletes that failed for something that is not a saved object, so nothing can look for it again
    private readonly List<string> _failedOther = new();
    private readonly Dictionary<string, string> _unverified = new();
    private int _removed;

    public CohortScriptCleanup(IRDMPPlatformRepositoryServiceLocator locator)
    {
        _locator = locator;
    }

    /// <summary>
    /// Deletes the cohort, if there is one, and then anything else the run saved that still exists. It never throws:
    /// a failure while reading the platform is reported as something it could not check.
    /// </summary>
    public CohortScriptCleanupResult Remove(CohortIdentificationConfiguration cic, IReadOnlyList<IMapsDirectlyToDatabaseTable> madeByTheRun)
    {
        if (cic != null)
            try
            {
                RemoveCohort(cic);
            }
            catch (Exception e)
            {
                _unverified[$"cohort {cic.ID}"] = $"the parts of cohort {cic.ID} ({e.Message})";
            }

        // what the run saved and the cohort's clean-up did not reach, newest first
        foreach (var leftover in madeByTheRun.Reverse())
            if (leftover is IDeleteable deleteable and IRevertable revertable && StillThere(revertable, Describe(leftover)) != false)
                Delete(deleteable, Describe(leftover));

        // the final check decides what is left. An object whose delete worked first time is not read again.
        var leftovers = new List<string>(_failedOther);
        foreach (var (o, what, error) in _tried.Values)
            if ((error != null || _unverified.ContainsKey(Key(o))) && StillThere(o, what) == true)
                leftovers.Add(error == null ? what : $"{what} ({error})");
        foreach (var (set, table, what, error) in _failedJoins)
            if (StillJoined(set, table, what) == true)
                leftovers.Add($"{what} ({error})");

        var subject = cic == null ? "The objects this rebuild saved were removed" : $"The partly built cohort {cic.ID} \"{cic.Name}\" was removed";
        var sentence = leftovers.Count == 0
            ? $"{subject}, with {_removed} thing(s) in all (objects and links)."
            : $"{subject} only in part. Still there: {string.Join("; ", leftovers)}. Delete them by hand.";
        if (_unverified.Count > 0)
            sentence += $" The clean-up could not check {string.Join("; ", _unverified.Values)}. Look for them in RDMP and delete any that are left.";

        return new CohortScriptCleanupResult
        {
            Removed = _removed,
            Leftovers = leftovers,
            Unverified = _unverified.Values.ToList(),
            Cohort = cic,
            Sentence = sentence
        };
    }

    private static string Describe(IMapsDirectlyToDatabaseTable o) => $"{o.GetType().Name} {o.ID}";

    // true when it exists, false when it is gone, null when the check failed. A failed check is recorded, and a later
    // check that works replaces it.
    private bool? StillThere(IRevertable o, string what)
    {
        try
        {
            var exists = o.Exists();
            _unverified.Remove(Key(o));
            return exists;
        }
        catch (Exception e)
        {
            _unverified[Key(o)] = $"{what} ({e.Message})";
            return null;
        }
    }

    private static string Key(IMapsDirectlyToDatabaseTable o) => $"{o.GetType().Name}:{o.ID}";

    // true when the forced join is still there, false when it is gone, null when the check failed
    private bool? StillJoined(AggregateConfiguration set, ITableInfo table, string what)
    {
        try
        {
            return _locator.CatalogueRepository.AggregateForcedJoinManager.GetAllForcedJoinsFor(set).Any(t => t.ID == table.ID);
        }
        catch (Exception e)
        {
            _unverified[what] = $"{what} ({e.Message})";
            return null;
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
                BreakForcedJoin(repository, set, table);
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

    private void Delete(IDeleteable deleteable, string what)
    {
        var revertable = deleteable as IRevertable;
        try
        {
            deleteable.DeleteInDatabase();
            _removed++;
            if (revertable != null)
                _tried.TryAdd(Key(revertable), (revertable, what, null));
        }
        catch (Exception e)
        {
            if (revertable != null)
                _tried[Key(revertable)] = (revertable, what, e.Message);
            else
                _failedOther.Add($"{what} ({e.Message})");
        }
    }

    private void BreakForcedJoin(ICatalogueRepository repository, AggregateConfiguration set, ITableInfo table)
    {
        var what = $"forced join of set {set.ID} to table {table.ID}";
        try
        {
            repository.AggregateForcedJoinManager.BreakLinkBetween(set, table);
            _removed++;
        }
        catch (Exception e)
        {
            _failedJoins.Add((set, table, what, e.Message));
        }
    }
}
