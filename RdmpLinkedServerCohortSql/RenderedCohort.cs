// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Reflection;
using FAnsi.Discovery.QuerySyntax;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;

namespace RdmpLinkedServerCohortSql;

/// <summary>Everything both script writers need: where each set runs and its SQL, the parameter declarations, what the
/// header says, and the warnings.</summary>
internal sealed class RenderedCohort(CohortIdentificationConfiguration cic, CohortAggregateContainer root, SetPlacement[] placements,
    string homeServer, string homeDatabase, string declarations, List<string> warnings, ITableInfo[] touched,
    IQuerySyntaxHelper syntax, bool stagedChecked)
{
    internal const string InspectionLine = "This script is for inspection only. Do not run it on a production server. SQL Server plans the " +
        "whole script as one query, so a cohort with full-text or other heavy sets can run for hours, while RDMP's cached build takes " +
        "minutes. To run the cohort, use \"" + ExecuteCommandViewCohortLinkedServerSql.Staged + "\".";
    internal const string TempTablesLine = "The script keeps its temp tables so you can inspect each set: #rdmp_s<id> holds a set from the " +
        "home server, #rdmp_r<id> a set copied from a linked server, #rdmp_pit<id> a patient index table copied from a linked server, " +
        "and #rdmp_cohort the final cohort. Before you run the script again, close this window, or run the DROP TABLE lines at the end " +
        "(remove the -- first). If the run stops with error 207 (invalid column name) or 208 (invalid object name), close this window: " +
        "the script could not switch its session settings (XACT_ABORT and NOCOUNT) back.";

    public CohortIdentificationConfiguration Cic { get; } = cic;
    public CohortAggregateContainer Root { get; } = root;
    public SetPlacement[] Placements { get; } = placements;
    public string HomeServer { get; } = homeServer;
    public string HomeDatabase { get; } = homeDatabase;
    public string Declarations { get; } = declarations;
    public IReadOnlyList<string> Warnings { get; } = warnings.AsReadOnly();
    public ITableInfo[] Touched { get; } = touched;
    public IQuerySyntaxHelper Syntax { get; } = syntax;
    /// <summary>True when the checks that the staged script needs have run. The staged writer requires them.</summary>
    public bool StagedChecked { get; } = stagedChecked;

    public SetPlacement PlacementOf(Rdmp.Core.Curation.Data.Aggregation.AggregateConfiguration set) => Placements.Single(p => Equals(p.Set, set));

    public LinkedServerCohortSqlResult Result(string sql, CohortSqlShape shape) =>
        new(sql, HomeServer, HomeDatabase, Warnings, Array.AsReadOnly(Touched)) { HeaderLines = Header(shape).AsReadOnly(), Shape = shape };

    /// <summary>The comment lines at the top of the script. The console tip, when there is one, comes before them
    /// (<see cref="LinkedServerCohortSqlResult.ToScript"/>).</summary>
    internal List<string> Header(CohortSqlShape shape)
    {
        var comparer = LinkedServerCohortSqlBuilder.ServerComparer;
        var staged = shape == CohortSqlShape.Staged;
        var remotes = Placements.Where(p => p.Kind != SetPlacementKind.Home).ToArray();
        var links = remotes.Select(p => p.RemoteTables[0].Server.Trim()).Distinct(comparer).ToArray();
        var version = typeof(LinkedServerCohortSqlBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var header = new List<string>
        {
            $"Linked-server cohort SQL for {Cic.Name}, written by RdmpLinkedServerCohortSql {version}.",
            staged
                ? "Script type: staged, to run. Each set goes into its own temp table, and the script combines the tables at the end."
                : "Script type: for inspection only.",
            $"The script runs on server {HomeServer}, the home server. " +
            (links.Length == 0 ? "It reads from no linked server." : $"It reads from these linked servers: {string.Join(", ", links)}.")
        };
        if (!staged) header.Add(InspectionLine);
        var databases = new[] { HomeDatabase }.Concat(Touched.Where(t => comparer.Equals(t.Server.Trim(), HomeServer))
            .Select(t => t.GetDatabaseRuntimeName())).Distinct(comparer).ToArray();
        header.Add(databases.Length == 1 ? $"Sets on the home server use database {HomeDatabase}." :
            $"Sets on the home server use databases {string.Join(", ", databases)}. The script starts with USE {HomeDatabase}, " +
            "so tables in the other databases need their database name (database.schema.table).");
        foreach (var link in links)
            header.Add($"Each OPENQUERY block runs on {link} under the login that the linked server {link} on {HomeServer} maps your login to " +
                       "(see sys.linked_logins). That can be a shared login, not you.");
        if (links.Length > 0)
        {
            header.Add($"If the run fails with error 7202 (linked server not found), check that these names match a linked server on {HomeServer}: " +
                       "SELECT name FROM sys.servers WHERE is_linked = 1");
            header.AddRange(links.Select(l => "  " + l));
        }
        if (Cic.QueryCachingServer_ID != null)
            header.Add("This script ignores the cohort's cache. It reads the source tables directly, so its counts can differ from the " +
                       "cached build if the data changed after the cache was built.");
        header.AddRange(remotes.Select(p => p.RemoteBatchForm == RemoteBatchForm.Guarded
            ? $"{p.RemoteTables[0].Server.Trim()}: the query for {LinkedServerCohortSqlBuilder.Describe(p.RemoteAggregate)} runs in the login's default " +
              $"database, which must be {p.RemoteTables[0].GetDatabaseRuntimeName()}. The warning below says why."
            : $"{p.RemoteTables[0].Server.Trim()}: the query switches to database {p.RemoteTables[0].GetDatabaseRuntimeName()} with USE and returns " +
              $"these columns: {string.Join(", ", p.ResultColumns.Select(c => SqlServerOpenQueryWrapper.DescribeColumn(c, Syntax)))}.").Distinct());
        header.AddRange(Placements.Where(p => p.Pit != null).GroupBy(p => p.Pit).Where(g => g.Count() > 1).Select(g => PitLine(g.ToArray(), staged)));
        header.AddRange(Warnings.Select(w => "WARNING: " + w));
        if (staged) header.Add(TempTablesLine);
        return header.Select(h => "-- " + LinkedServerCohortSqlResult.CommentText(h)).ToList();
    }

    // A patient index table that several sets use. The staged script copies a patient index table on a linked server once,
    // for the sets on the home server that use it. Any other set that uses it repeats its query inside its own SQL.
    private static string PitLine(SetPlacement[] users, bool staged)
    {
        var name = users[0].Pit.ToString();
        var copiedFor = staged ? users.Count(u => u.Kind == SetPlacementKind.HomeWithRemotePit) : 0;
        if (copiedFor == 0) return $"The script repeats the query for patient index table {name} {users.Length} times, once for each set that uses it.";
        var repeated = users.Length - copiedFor;
        return $"The script copies patient index table {name} once into {users.First(u => u.Kind == SetPlacementKind.HomeWithRemotePit).PitTempTable}, " +
               $"and {copiedFor} sets on the home server read it from there." +
               (repeated > 0 ? $" {repeated} set(s) on linked servers repeat its query inside their own query." : "");
    }
}
