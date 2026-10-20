// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>Writes the script for inspection only: RDMP's own one-statement SQL, with the parts on linked servers read
/// through OPENQUERY.</summary>
internal static class CompositeScriptWriter
{
    internal static LinkedServerCohortSqlResult Write(RenderedCohort rendered)
    {
        var sql = SqlServerOpenQueryWrapper.UseDatabase(rendered.HomeDatabase, rendered.Syntax) + Environment.NewLine +
            rendered.Declarations + LinkedServerCohortSqlBuilder.WriteContainer(rendered.Root, set => rendered.PlacementOf(set).LeafToken);
        // The SQL of each set goes in after the tree is indented, so indenting never changes a string that spans several
        // lines. The SQL for linked servers goes in last, so RDMP's parameter renaming and indenting never touch it.
        foreach (var placement in rendered.Placements)
            sql = sql.Replace(placement.LeafToken, placement.HomeSql, StringComparison.Ordinal);
        foreach (var placement in rendered.Placements.Where(p => p.Kind != SetPlacementKind.Home))
            sql = sql.Replace(placement.Placeholder, placement.RemoteSql, StringComparison.Ordinal);
        return rendered.Result(sql, CohortSqlShape.Composite);
    }
}
