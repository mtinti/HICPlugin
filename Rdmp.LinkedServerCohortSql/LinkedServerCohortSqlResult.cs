// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using Rdmp.Core.Curation.Data;

namespace Rdmp.LinkedServerCohortSql;

public sealed record LinkedServerCohortSqlResult(string Sql, string HomeServerName, string HomeDatabaseName,
    IReadOnlyList<string> Warnings, IReadOnlyList<ITableInfo> TablesTouched)
{
    internal const string ConsoleTip = "-- Tip: pass a file path as the argument after the configuration for clean output; stdout also carries log lines";
    internal IReadOnlyList<string> HeaderLines { get; init; } = Array.Empty<string>();

    /// <summary>Composite: one statement, for reading. Staged: one temp table per set, the shape to execute (plan 0.7).</summary>
    public CohortSqlShape Shape { get; init; } = CohortSqlShape.Composite;

    public string ToScript(bool includeConsoleTip = false) =>
        (includeConsoleTip ? ConsoleTip + Environment.NewLine : "") +
        string.Concat(HeaderLines.Select(line => line + Environment.NewLine)) + Sql;

    // Catalogue names and exception details must never escape their SQL comment line.
    internal static string CommentText(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
}
