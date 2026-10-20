// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Text;

namespace RdmpLinkedServerCohortSql;

/// <summary>
/// Writes the staged script, the one to run. First it copies each set and patient index table that lives on a linked server
/// into a temp table, so each linked server is busy for seconds only. Next it runs each set on the home server as its own
/// statement into a temp table. Last it combines the temp tables with UNION, INTERSECT and EXCEPT, as the cohort tree says.
/// RDMP's cached build also runs each set on its own.
/// </summary>
internal static class StagedScriptWriter
{
    internal const string Result = "#rdmp_cohort";

    internal static LinkedServerCohortSqlResult Write(RenderedCohort rendered)
    {
        if (!rendered.StagedChecked) throw new InvalidOperationException("Call Render with staged: true before writing the staged script.");
        var nl = Environment.NewLine;
        var syntax = rendered.Syntax;

        // First, copy the data that lives on linked servers: each remote set, and each remote patient index table once.
        var far = new List<(string Table, string Sql)>();
        foreach (var placement in rendered.Placements)
        {
            if (placement.Kind == SetPlacementKind.RemoteWhole)
                far.Add((placement.TempTable, FarStage(placement, placement.TempTable)));
            else if (placement.Kind == SetPlacementKind.HomeWithRemotePit && far.All(f => f.Table != placement.PitTempTable))
                far.Add((placement.PitTempTable, FarStage(placement, placement.PitTempTable)));
        }
        // Next, run each set on the home server into its own temp table.
        var home = rendered.Placements.Where(p => p.Kind != SetPlacementKind.RemoteWhole).Select(p => (Table: p.TempTable, Sql: HomeStage(p))).ToList();
        var tables = far.Select(f => f.Table).Concat(home.Select(h => h.Table)).Append(Result).ToArray();

        var sql = new StringBuilder();
        sql.Append(SqlServerOpenQueryWrapper.UseDatabase(rendered.HomeDatabase, syntax)).Append(nl);
        foreach (var table in tables) sql.Append($"IF OBJECT_ID('tempdb..{table}') IS NOT NULL DROP TABLE {table};").Append(nl);
        sql.Append("SET NOCOUNT ON;").Append(nl).Append("SET XACT_ABORT ON;").Append(nl);
        sql.Append(rendered.Declarations);
        if (rendered.Declarations.Length > 0 && !rendered.Declarations.EndsWith('\n')) sql.Append(nl);
        sql.Append("BEGIN TRY").Append(nl);
        foreach (var (_, stage) in far) sql.Append(stage);
        foreach (var (_, stage) in home) sql.Append(stage);

        // Then combine the temp tables with the same set operations as the script for inspection.
        var arithmetic = LinkedServerCohortSqlBuilder.WriteContainer(rendered.Root, set => Projection(rendered.PlacementOf(set), rendered));
        sql.Append($"SELECT * INTO {Result} FROM").Append(arithmetic).Append("AS c").Append(nl).Append(';').Append(nl);

        // Then the count table: one row per set, in the order the cohort tree combines them, then the final cohort. Each row
        // shows the set's ID and temp table, so a user can tell apart two sets with the same name.
        var rows = LinkedServerCohortSqlBuilder.TreeOrder(rendered.Root).Select(set => rendered.PlacementOf(set))
            .Select((p, i) => $"SELECT {i + 1} AS [n], N'{Literal(p.Set.ToString())}' AS [set], {p.Set.ID} AS [id], N'{p.TempTable}' AS [table], COUNT(*) AS [rows] FROM {p.TempTable}")
            .ToList();
        rows.Add($"SELECT {rows.Count + 1} AS [n], N'Final cohort' AS [set], NULL AS [id], N'{Result}' AS [table], COUNT(*) AS [rows] FROM {Result}");
        sql.Append(string.Join(nl + "UNION ALL" + nl, rows)).Append(nl).Append("ORDER BY [n]").Append(nl).Append(';').Append(nl);

        // The cohort itself, last, so it is the script's final result set.
        sql.Append($"SELECT * FROM {Result}").Append(nl).Append(';').Append(nl);

        // Last, switch the session settings back, both after success and after an error, and list the DROP lines.
        sql.Append("SET XACT_ABORT OFF;").Append(nl).Append("SET NOCOUNT OFF;").Append(nl).Append("END TRY").Append(nl)
            .Append("BEGIN CATCH").Append(nl).Append("SET XACT_ABORT OFF;").Append(nl).Append("SET NOCOUNT OFF;").Append(nl)
            .Append("THROW;").Append(nl).Append("END CATCH;").Append(nl)
            .Append("-- To clear the temp tables in this window, uncomment and run:").Append(nl);
        foreach (var table in tables) sql.Append($"-- DROP TABLE {table};").Append(nl);
        return rendered.Result(sql.ToString(), CohortSqlShape.Staged);
    }

    private static string Literal(string value) => value.Replace("'", "''");

    // The copy lists the columns without COLLATE: the temp table keeps each column's collation from the OPENQUERY result,
    // as the script for inspection does. A query in the checked form declares no columns, so the copy takes them all.
    private static string FarStage(SetPlacement placement, string table)
    {
        var columns = placement.RemoteBatchForm == RemoteBatchForm.Guarded ? "*" :
            string.Join(", ", placement.ResultColumns.Select(c => SqlServerOpenQueryWrapper.QuoteIdentifier(c.Name, placement.RemoteTables[0].GetQuerySyntaxHelper())));
        return $"SELECT {columns} INTO {table} FROM {placement.RemoteSql}{Environment.NewLine};{Environment.NewLine}";
    }

    private static string HomeStage(SetPlacement placement)
    {
        var sql = placement.HomeSql;
        if (placement.Kind == SetPlacementKind.HomeWithRemotePit)
            sql = sql.Replace(placement.Placeholder, placement.PitTempTable, StringComparison.Ordinal);
        var from = FindTopLevelFrom(sql);
        var space = from > 0 && !char.IsWhiteSpace(sql[from - 1]) ? " " : "";
        return sql[..from] + space + "INTO " + placement.TempTable + Environment.NewLine + sql[from..] +
               Environment.NewLine + ";" + Environment.NewLine;
    }

    // Reads a set back from its temp table with the same columns, order and names as the script for inspection. A column
    // gets COLLATE only where the set's own SQL ends with one, because a temp-table column keeps its collation but not the
    // stronger effect of an explicit COLLATE.
    private static string Projection(SetPlacement placement, RenderedCohort rendered)
    {
        if (placement.Kind == SetPlacementKind.RemoteWhole)
            return placement.HomeSql.Replace(placement.Placeholder, placement.TempTable, StringComparison.Ordinal);
        return "SELECT " + string.Join(", ", placement.HomeColumns.Select(c =>
        {
            var name = SqlServerOpenQueryWrapper.QuoteIdentifier(c.Name, rendered.Syntax);
            var collation = CoreSelectList.TrailingCollation(c.Expression, out _);
            return name + (collation == null ? "" : " COLLATE " + collation) + " AS " + name;
        })) + " FROM " + placement.TempTable;
    }

    /// <summary>
    /// Finds the main FROM of a set's SQL: the first whole word FROM outside all brackets. It skips comments (including nested
    /// /* */ comments), string literals and quoted names. A word ends where SQL Server's rules for names say it does,
    /// including for non-English letters.
    /// </summary>
    internal static int FindTopLevelFrom(string sql)
    {
        var depth = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var skipped = SqlText.Skip(sql, i);
            if (skipped != i) { i = skipped; continue; }
            var c = sql[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && (c == 'F' || c == 'f') && i + 4 <= sql.Length &&
                     string.Compare(sql, i, "FROM", 0, 4, StringComparison.OrdinalIgnoreCase) == 0 &&
                     (i == 0 || !IsWordCharacter(sql[i - 1])) && (i + 4 == sql.Length || !IsWordCharacter(sql[i + 4])))
                return i;
            i++;
        }
        throw new InvalidOperationException("The staged script writer could not find the main FROM of the set's SQL (a FROM outside " +
                                            "all brackets). This is a bug in the plugin; please report it with this SQL:" + Environment.NewLine + sql);
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || char.IsSurrogate(c) || c is '_' or '@' or '#' or '$';
}
