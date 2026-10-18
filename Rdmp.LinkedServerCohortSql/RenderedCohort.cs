// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Reflection;
using FAnsi.Discovery.QuerySyntax;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;

namespace Rdmp.LinkedServerCohortSql;

/// <summary>Everything both writers need: placements with their home and remote SQL, the parameter declarations, the
/// header inputs and the warnings (plan 0.7.6).</summary>
internal sealed class RenderedCohort(CohortIdentificationConfiguration cic, CohortAggregateContainer root, SetPlacement[] placements,
    string homeServer, string homeDatabase, string declarations, List<string> warnings, ITableInfo[] touched,
    IQuerySyntaxHelper syntax, bool stagedChecked)
{
    internal const string ReadOnlyLine = "READ, DO NOT RUN ON PRODUCTION: a single statement is planned as one query; cohorts with full-text or " +
        "other heavy sets can take hours where the cached build takes minutes (observed 2026-10-18). To execute, use \"Query Builder SQL (Linked Servers, staged)\".";
    internal const string TempTablesLine = "Temp tables (#rdmp_s..., #rdmp_r..., #rdmp_pit..., #rdmp_cohort) are kept after the run so you can inspect " +
        "each set. To run again after changing the cohort, close this query window first, or run the DROP block at the end of this script. " +
        "If a run stops with Msg 207 or 208, this window also keeps XACT_ABORT ON and NOCOUNT ON: close it.";

    public CohortIdentificationConfiguration Cic { get; } = cic;
    public CohortAggregateContainer Root { get; } = root;
    public SetPlacement[] Placements { get; } = placements;
    public string HomeServer { get; } = homeServer;
    public string HomeDatabase { get; } = homeDatabase;
    public string Declarations { get; } = declarations;
    public IReadOnlyList<string> Warnings { get; } = warnings.AsReadOnly();
    public ITableInfo[] Touched { get; } = touched;
    public IQuerySyntaxHelper Syntax { get; } = syntax;
    /// <summary>True when the staged refusals ran, which the staged writer requires.</summary>
    public bool StagedChecked { get; } = stagedChecked;

    public SetPlacement PlacementOf(Rdmp.Core.Curation.Data.Aggregation.AggregateConfiguration set) => Placements.Single(p => Equals(p.Set, set));

    public LinkedServerCohortSqlResult Result(string sql, CohortSqlShape shape) =>
        new(sql, HomeServer, HomeDatabase, Warnings, Array.AsReadOnly(Touched)) { HeaderLines = Header(shape).AsReadOnly(), Shape = shape };

    /// <summary>Header lines 2 to 10 of plan 0.5 (line 1, the console tip, is added by <see cref="LinkedServerCohortSqlResult.ToScript"/>).</summary>
    internal List<string> Header(CohortSqlShape shape)
    {
        var comparer = LinkedServerCohortSqlBuilder.ServerComparer;
        var staged = shape == CohortSqlShape.Staged;
        var remotes = Placements.Where(p => p.Kind != SetPlacementKind.Home).ToArray();
        var links = remotes.Select(p => p.RemoteTables[0].Server.Trim()).Distinct(comparer).ToArray();
        var version = typeof(LinkedServerCohortSqlBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var header = new List<string>
        {
            $"Linked-server cohort SQL for {Cic.Name} (Rdmp.LinkedServerCohortSql {version}). Shape: {(staged ? "staged" : "composite")}. " +
            $"Home server: {HomeServer}. Links used: {(links.Length == 0 ? "none" : string.Join(", ", links))}"
        };
        if (!staged) header.Add(ReadOnlyLine);
        var databases = new[] { HomeDatabase }.Concat(Touched.Where(t => comparer.Equals(t.Server.Trim(), HomeServer))
            .Select(t => t.GetDatabaseRuntimeName())).Distinct(comparer).ToArray();
        header.Add(databases.Length == 1 ? "Home database: " + HomeDatabase :
            $"Home databases: {string.Join(", ", databases)}; USE names {HomeDatabase}; objects in the others must be qualified");
        foreach (var link in links) header.Add($"The OPENQUERY blocks run on {link} as the login that {link} maps on {HomeServer}, not as you.");
        if (links.Length > 0)
        {
            header.Add("If a run fails with Msg 7202 (server not found), compare these names with: SELECT name FROM sys.servers WHERE is_linked = 1");
            header.AddRange(links.Select(l => "  " + l));
        }
        if (Cic.QueryCachingServer_ID != null)
            header.Add("QueryCache ignored by this view: runs live against the source tables; counts can differ from the cached run if data changed since the cache was built.");
        header.AddRange(remotes.Select(p => p.RemoteBatchForm == RemoteBatchForm.Guarded
            ? $"{p.RemoteTables[0].Server.Trim()}: guarded form; the link login's default database must be {p.RemoteTables[0].GetDatabaseRuntimeName()}"
            : $"{p.RemoteTables[0].Server.Trim()}: EXEC {SqlServerOpenQueryWrapper.UseDatabase(p.RemoteTables[0].GetDatabaseRuntimeName(), Syntax)} form; declared columns: {string.Join(", ", p.ResultColumns.Select(c => SqlServerOpenQueryWrapper.DescribeColumn(c, Syntax)))}").Distinct());
        header.AddRange(Placements.Where(p => p.Pit != null).GroupBy(p => p.Pit).Where(g => g.Count() > 1).Select(g => PitLine(g.ToArray(), staged)));
        header.AddRange(Warnings.Select(w => "WARNING: " + w));
        if (staged) header.Add(TempTablesLine);
        return header.Select(h => "-- " + LinkedServerCohortSqlResult.CommentText(h)).ToList();
    }

    // Header line 8. Staged: a far-side patient index used by home sets is staged once; any other consumer (a whole remote
    // set on the same server, or a home set with a home patient index) still renders it inside its own text.
    private static string PitLine(SetPlacement[] users, bool staged)
    {
        var name = users[0].Pit.Name;
        var stagedUsers = staged ? users.Count(u => u.Kind == SetPlacementKind.HomeWithRemotePit) : 0;
        if (stagedUsers == 0) return $"Patient index table {name} rendered {users.Length} times";
        var inline = users.Length - stagedUsers;
        return $"Patient index table {name} staged once as {users.First(u => u.Kind == SetPlacementKind.HomeWithRemotePit).PitTempTable}, used by {stagedUsers} sets" +
               (inline > 0 ? $"; also rendered inside {inline} remote set(s)" : "");
    }
}
