// The forced joins saved for a set. RDMP's own list (AggregateConfiguration.ForcedJoins) adds every main table of
// the set's catalogue when the user setting "always join everything" is on. Those tables are joined at query time,
// but nothing is saved for them. The export, the manifest, the add-join command, the check after its line and the
// clean-up all need what is saved: otherwise the export writes joins the cohort does not have, the add-join command
// refuses a join that is not there, and the clean-up reports a join it cannot remove.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Settings;

namespace RdmpCohortScript;

/// <summary>Reads the forced joins saved for a set, without the tables RDMP adds for the "always join everything" setting.</summary>
internal static class SavedForcedJoins
{
    // the memory and YAML repositories keep their joins here; RDMP has no public way to read them without the setting
    private static readonly PropertyInfo MemoryJoins =
        typeof(MemoryCatalogueRepository).GetProperty("ForcedJoins", BindingFlags.Instance | BindingFlags.NonPublic);

    public static ITableInfo[] Of(AggregateConfiguration set)
    {
        var repository = set.CatalogueRepository;
        if (!UserSettings.AlwaysJoinEverything)
            return repository.AggregateForcedJoinManager.GetAllForcedJoinsFor(set);

        // the same query RDMP runs, without the tables the setting adds
        if (repository is TableRepository database)
            return database.SelectAllWhere<TableInfo>(
                "SELECT TableInfo_ID FROM AggregateForcedJoin WHERE AggregateConfiguration_ID = @set", "TableInfo_ID",
                new Dictionary<string, object> { ["@set"] = set.ID }).Cast<ITableInfo>().ToArray();

        if (repository is MemoryCatalogueRepository && MemoryJoins?.GetValue(repository) is Dictionary<AggregateConfiguration, HashSet<ITableInfo>> joins)
            return joins.TryGetValue(set, out var saved) ? saved.ToArray() : System.Array.Empty<ITableInfo>();

        // a repository of another kind: without the main tables, which loses a saved join to one of them
        var main = set.Catalogue.GetTableInfosIdeallyJustFromMainTables().Select(t => t.ID).ToHashSet();
        return repository.AggregateForcedJoinManager.GetAllForcedJoinsFor(set).Where(t => !main.Contains(t.ID)).ToArray();
    }
}
