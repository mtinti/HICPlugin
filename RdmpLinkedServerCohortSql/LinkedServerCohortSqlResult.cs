// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using Rdmp.Core.Curation.Data;

namespace RdmpLinkedServerCohortSql;

public sealed record LinkedServerCohortSqlResult(string Sql, string HomeServerName, string HomeDatabaseName,
    IReadOnlyList<string> Warnings, IReadOnlyList<ITableInfo> TablesTouched)
{
    internal IReadOnlyList<string> HeaderLines { get; init; } = Array.Empty<string>();

    /// <summary>Which script this is: the staged script, which copies each set into a temp table and is the one to run, or the
    /// script for inspection only (the composite), which is one statement.</summary>
    public CohortSqlShape Shape { get; init; } = CohortSqlShape.Composite;

    /// <summary>The first line printed on the console. Its example keeps the script type the user asked for.</summary>
    internal static string ConsoleTip(CohortSqlShape shape) =>
        "-- Tip: RDMP also prints log lines to the console. To get only the SQL, save it to a file: " +
        "rdmp cmd ViewCohortLinkedServerSql CohortIdentificationConfiguration:<id> out.sql" + (shape == CohortSqlShape.Staged ? " true" : "");

    public string ToScript(bool includeConsoleTip = false) =>
        (includeConsoleTip ? ConsoleTip(Shape) + Environment.NewLine : "") +
        string.Concat(HeaderLines.Select(line => line + Environment.NewLine)) + Sql;

    // A name from the catalogue must stay inside its comment line, so line breaks become spaces.
    internal static string CommentText(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
}
