// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Text;

namespace RdmpLinkedServerCohortSql;

/// <summary>
/// The staged shape, the one to execute (plan 0.7.2): far-side sets and patient index tables are copied into temp tables
/// first, so each link is used for seconds; each home set then runs as its own statement into a temp table; the set
/// arithmetic runs last over the temp tables. This mirrors RDMP's cached build, which runs each set on its own.
/// </summary>
internal static class StagedScriptWriter
{
    internal const string Result = "#rdmp_cohort";

    internal static LinkedServerCohortSqlResult Write(RenderedCohort rendered)
    {
        if (!rendered.StagedChecked) throw new InvalidOperationException("Render the cohort with staged: true before writing the staged shape.");
        var nl = Environment.NewLine;
        var syntax = rendered.Syntax;

        // Step 5: far-side stages in order of first appearance in render order, one per far-side patient index table.
        var far = new List<(string Table, string Sql)>();
        foreach (var placement in rendered.Placements)
        {
            if (placement.Kind == SetPlacementKind.RemoteWhole)
                far.Add((placement.TempTable, FarStage(placement, placement.TempTable)));
            else if (placement.Kind == SetPlacementKind.HomeWithRemotePit && far.All(f => f.Table != placement.PitTempTable))
                far.Add((placement.PitTempTable, FarStage(placement, placement.PitTempTable)));
        }
        // Step 6: home stages in render order.
        var home = rendered.Placements.Where(p => p.Kind != SetPlacementKind.RemoteWhole).Select(p => (Table: p.TempTable, Sql: HomeStage(p))).ToList();
        var tables = far.Select(f => f.Table).Concat(home.Select(h => h.Table)).Append(Result).ToArray();
        var leaves = rendered.Placements;

        var sql = new StringBuilder();
        sql.Append(SqlServerOpenQueryWrapper.UseDatabase(rendered.HomeDatabase, syntax)).Append(nl);
        foreach (var table in tables) sql.Append($"IF OBJECT_ID('tempdb..{table}') IS NOT NULL DROP TABLE {table};").Append(nl);
        sql.Append("SET NOCOUNT ON;").Append(nl).Append("SET XACT_ABORT ON;").Append(nl);
        sql.Append(rendered.Declarations);
        if (rendered.Declarations.Length > 0 && !rendered.Declarations.EndsWith('\n')) sql.Append(nl);
        sql.Append("BEGIN TRY").Append(nl);
        foreach (var (_, stage) in far) sql.Append(stage);
        foreach (var (_, stage) in home) sql.Append(stage);

        // Step 7: the composite's arithmetic over the temp tables.
        var arithmetic = LinkedServerCohortSqlBuilder.WriteContainer(rendered.Root, set => Projection(rendered.PlacementOf(set), rendered));
        sql.Append($"SELECT * INTO {Result} FROM").Append(arithmetic).Append("AS c").Append(nl).Append(';').Append(nl);

        // Step 8: the count grid, one row per set in cohort tree order (the order of the arithmetic), with the set's ID
        // and temp table so sets with the same name can be told apart, then the final cohort's total.
        var rows = LinkedServerCohortSqlBuilder.TreeOrder(rendered.Root).Select(set => rendered.PlacementOf(set))
            .Select((p, i) => $"SELECT {i + 1} AS [n], N'{Literal(p.Set.ToString())}' AS [set], {p.Set.ID} AS [id], N'{p.TempTable}' AS [table], COUNT(*) AS [rows] FROM {p.TempTable}")
            .ToList();
        rows.Add($"SELECT {rows.Count + 1} AS [n], N'Final cohort' AS [set], NULL AS [id], N'{Result}' AS [table], COUNT(*) AS [rows] FROM {Result}");
        sql.Append(string.Join(nl + "UNION ALL" + nl, rows)).Append(nl).Append("ORDER BY [n]").Append(nl).Append(';').Append(nl);

        // The cohort itself, last, so it is the script's final result set.
        sql.Append($"SELECT * FROM {Result}").Append(nl).Append(';').Append(nl);

        // Steps 9 to 11.
        sql.Append("SET XACT_ABORT OFF;").Append(nl).Append("SET NOCOUNT OFF;").Append(nl).Append("END TRY").Append(nl)
            .Append("BEGIN CATCH").Append(nl).Append("SET XACT_ABORT OFF;").Append(nl).Append("SET NOCOUNT OFF;").Append(nl)
            .Append("THROW;").Append(nl).Append("END CATCH;").Append(nl)
            .Append("-- To clear the temp tables in this window, uncomment and run:").Append(nl);
        foreach (var table in tables) sql.Append($"-- DROP TABLE {table};").Append(nl);
        return rendered.Result(sql.ToString(), CohortSqlShape.Staged);
    }

    private static string Literal(string value) => value.Replace("'", "''");

    // Declared columns without COLLATE: the temp table keeps each column's collation from the OPENQUERY result, exactly as
    // the composite's OPENQUERY column carries it. A guarded block has no declared columns and stages everything.
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

    // Plan 0.7.3: exactly the composite's outer leaf select list, in the same order and with the same names. A column
    // carries COLLATE only where the composite's expression does, because a temp table column has implicit collation.
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
    /// Plan 0.7.4: the index of the first whole-word FROM at parenthesis depth 0, skipping line comments, nested block
    /// comments, string literals and quoted identifiers. Word boundaries follow SQL Server's Unicode-aware identifier rules.
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
        throw new InvalidOperationException("No FROM at parenthesis depth 0 in the set's SQL; this is a bug in the staged writer:" +
                                            Environment.NewLine + sql);
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || char.IsSurrogate(c) || c is '_' or '@' or '#' or '$';
}
