// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace Rdmp.LinkedServerCohortSql;

/// <summary>The composite shape: one statement, for reading (plan 0.7.1).</summary>
internal static class CompositeScriptWriter
{
    internal static LinkedServerCohortSqlResult Write(RenderedCohort rendered)
    {
        var sql = SqlServerOpenQueryWrapper.UseDatabase(rendered.HomeDatabase, rendered.Syntax) + Environment.NewLine +
            rendered.Declarations + LinkedServerCohortSqlBuilder.WriteContainer(rendered.Root, set => rendered.PlacementOf(set).LeafToken);
        // Leaf text is spliced in after the walk so indentation never reaches a multi-line string literal (plan 0.7.9.2),
        // and remote SQL after that, so it stays opaque to core parameter renaming and indentation.
        foreach (var placement in rendered.Placements)
            sql = sql.Replace(placement.LeafToken, placement.HomeSql, StringComparison.Ordinal);
        foreach (var placement in rendered.Placements.Where(p => p.Kind != SetPlacementKind.Home))
            sql = sql.Replace(placement.Placeholder, placement.RemoteSql, StringComparison.Ordinal);
        return rendered.Result(sql, CohortSqlShape.Composite);
    }
}
