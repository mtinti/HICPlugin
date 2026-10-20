// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Text.RegularExpressions;
using Rdmp.Core.Curation.Data.Aggregation;

namespace RdmpLinkedServerCohortSql;

/// <summary>One column that RDMP's query builder selects for a cohort set. <see cref="Dimension"/> is null for the count column.
/// <see cref="Name"/> is null when SQL Server would give the column no name.</summary>
internal sealed record CoreColumn(string Name, string Expression, AggregateDimension Dimension);

/// <summary>
/// The columns that RDMP's query builder (<c>CohortQueryBuilderHelper.GetSQLForAggregate</c>) selects for a set, in its order.
/// <para>RDMP selects every dimension of a cohort set, because <c>IsCohortIdentificationAggregate</c> is true for every
/// cohort set. Without a count or HAVING, RDMP keeps the dimensions in the order they are listed. With either, RDMP sorts the
/// columns by Order, and gives the count column Order 2147483647 (<c>int.MaxValue</c>) so it comes last. RDMP compares two
/// Orders by subtracting them without an overflow check, and its sort does not keep equal values in a fixed order.</para>
/// </summary>
internal static class CoreSelectList
{
    internal const string IdentifierPattern = "(?:\\[(?:[^\\]]|\\]\\])+\\]|\"(?:[^\"]|\"\")+\"|[\\p{L}_#@][\\p{L}\\p{N}_@$#]*)";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
    private static readonly Regex QualifiedIdentifier =
        new($"^(?:{IdentifierPattern}?\\s*\\.\\s*)*(?<last>{IdentifierPattern})$", Options);
    private static readonly Regex TrailingCollate = new(@"^(?<expression>.*)\s+COLLATE\s+(?<collation>\w+)\s*$", Options);

    /// <param name="aggregate">The cohort set or patient index table.</param>
    /// <param name="orderProblem">Null when RDMP's column order is certain. Otherwise, in full sentences, why it is not and what to do.</param>
    internal static IReadOnlyList<CoreColumn> For(AggregateConfiguration aggregate, out string orderProblem)
    {
        orderProblem = null;
        var dimensions = aggregate.IsCohortIdentificationAggregate
            ? aggregate.AggregateDimensions.ToArray()
            : aggregate.AggregateDimensions.Where(d => d.IsExtractionIdentifier).ToArray();
        var hasCount = !string.IsNullOrWhiteSpace(aggregate.CountSQL);
        var sorted = hasCount || !string.IsNullOrWhiteSpace(aggregate.HavingSQL);
        if (sorted)
        {
            var orders = dimensions.Select(d => (long)d.Order).ToList();
            if (hasCount) orders.Add(int.MaxValue);
            var tie = orders.GroupBy(o => o).FirstOrDefault(g => g.Count() > 1);
            if (tie != null)
                orderProblem = tie.Key == int.MaxValue && hasCount
                    ? $"A column has Order {int.MaxValue}, the Order that RDMP gives the count column, so RDMP may write the count first. Lower that column's Order."
                    : $"Two columns have Order {tie.Key}, so RDMP may write them in either order. Give each column a different Order.";
            else if (orders.Count > 1 && orders.Max() - orders.Min() > int.MaxValue)
                orderProblem = $"The column Orders range from {orders.Min()} to {orders.Max()}" +
                               (hasCount ? $" (RDMP gives the count column Order {int.MaxValue})" : "") +
                               ". RDMP compares Orders by subtraction, and a gap this large gives a wrong result. " +
                               $"Use Orders that differ by at most {int.MaxValue}.";
            dimensions = dimensions.OrderBy(d => d.Order).ToArray();
        }
        var columns = dimensions.Select(d => new CoreColumn(NameOf(d), d.SelectSQL.Trim(), d)).ToList();
        if (hasCount)
        {
            aggregate.Catalogue.GetQuerySyntaxHelper().SplitLineIntoSelectSQLAndAlias(aggregate.CountSQL, out var expression, out var alias);
            columns.Add(new CoreColumn(string.IsNullOrWhiteSpace(alias) ? null : Unquote(alias), expression.Trim(), null));
        }
        return columns.AsReadOnly();
    }

    /// <summary>The name SQL Server gives the column: its alias, else the last part of a (qualified) column name, else null.</summary>
    internal static string NameOf(AggregateDimension dimension)
    {
        // SQL Server names the column patient id whether the alias is written [patient id], "patient id" or 'patient id'.
        // AggregateDimension.GetRuntimeName returns the alias as typed, with its quotes, so it cannot be used here.
        if (!string.IsNullOrWhiteSpace(dimension.Alias)) return Unquote(dimension.Alias);
        var match = QualifiedIdentifier.Match(dimension.SelectSQL?.Trim() ?? "");
        return match.Success ? Unquote(match.Groups["last"].Value) : null;
    }

    internal static bool IsQualifiedIdentifier(string expression, out string lastPart)
    {
        var match = QualifiedIdentifier.Match(expression.Trim());
        lastPart = match.Success ? Unquote(match.Groups["last"].Value) : null;
        return match.Success;
    }

    /// <summary>The collation of an expression ending in <c>COLLATE x</c>, else null.</summary>
    internal static string TrailingCollation(string expression, out string inner)
    {
        var match = TrailingCollate.Match(expression.Trim());
        inner = match.Success ? match.Groups["expression"].Value.Trim() : expression.Trim();
        return match.Success ? match.Groups["collation"].Value : null;
    }

    internal static string Unquote(string name)
    {
        name = name.Trim();
        if (name.Length < 2) return name;
        if (name.StartsWith('[') && name.EndsWith(']')) return name[1..^1].Replace("]]", "]");
        if (name.StartsWith('"') && name.EndsWith('"')) return name[1..^1].Replace("\"\"", "\"");
        // SQL Server also accepts a string literal as a column alias: SELECT id AS 'patient id'.
        if (name.StartsWith('\'') && name.EndsWith('\'')) return name[1..^1].Replace("''", "'");
        return name;
    }
}
