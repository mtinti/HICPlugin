// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using Rdmp.Core.Curation.Data.Aggregation;

namespace Rdmp.LinkedServerCohortSql;

internal static class CollationClause
{
    internal static bool IsPlain(AggregateDimension dimension) => dimension.ColumnInfo != null &&
        string.Equals(dimension.SelectSQL?.Trim(), dimension.ColumnInfo.Name, StringComparison.Ordinal);

    public static string Decide(AggregateDimension home, AggregateDimension remote)
    {
        if (!IsPlain(home) || !IsPlain(remote)) return null;
        var a = home.ColumnInfo;
        var b = remote.ColumnInfo;
        if (string.IsNullOrWhiteSpace(a.Collation) || string.IsNullOrWhiteSpace(b.Collation) ||
            string.Equals(a.Collation, b.Collation, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(a.Data_type) || string.IsNullOrWhiteSpace(b.Data_type)) return null;
        var translator = a.TableInfo.GetQuerySyntaxHelper().TypeTranslater;
        return translator.TryGetCSharpTypeForSQLDBType(a.Data_type) == typeof(string) &&
               translator.TryGetCSharpTypeForSQLDBType(b.Data_type) == typeof(string)
            ? " COLLATE " + a.Collation : null;
    }
}
