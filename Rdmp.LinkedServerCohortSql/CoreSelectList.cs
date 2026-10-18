// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Text.RegularExpressions;
using Rdmp.Core.Curation.Data.Aggregation;

namespace Rdmp.LinkedServerCohortSql;

/// <summary>One column of the select list core writes for a cohort set. <see cref="Dimension"/> is null for the CountSQL column;
/// <see cref="Name"/> is null when the column has no name that SQL Server would give it.</summary>
internal sealed record CoreColumn(string Name, string Expression, AggregateDimension Dimension);

/// <summary>
/// The select list of <c>CohortQueryBuilderHelper.GetSQLForAggregate</c>, in core's order.
/// <para>Core sets <c>isJoinAggregate = aggregate.IsCohortIdentificationAggregate</c>, a name-prefix test that is true for every
/// cohort set, so every <see cref="AggregateDimension"/> is selected. Without CountSQL and HAVING the list is in enumeration
/// order; with either, <c>AggregateBuilder</c> sorts the columns with <c>QueryTimeColumn.CompareTo</c> (<c>a.Order - b.Order</c>,
/// unchecked) and the count column has <c>Order = int.MaxValue</c>.</para>
/// </summary>
internal static class CoreSelectList
{
    internal const string IdentifierPattern = "(?:\\[(?:[^\\]]|\\]\\])+\\]|\"(?:[^\"]|\"\")+\"|[\\p{L}_#@][\\p{L}\\p{N}_@$#]*)";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
    private static readonly Regex QualifiedIdentifier =
        new($"^(?:{IdentifierPattern}?\\s*\\.\\s*)*(?<last>{IdentifierPattern})$", Options);
    private static readonly Regex TrailingCollate = new(@"^(?<expression>.*)\s+COLLATE\s+(?<collation>\w+)\s*$", Options);

    /// <param name="aggregate">The cohort set or patient index table.</param>
    /// <param name="orderProblem">Null when core's order is fully determined; otherwise why it is not (plan 0.7.3, v66).</param>
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
                    ? $"a dimension has Order {int.MaxValue}, the same as the count column, so core may write the count first; lower that Order"
                    : $"two columns share Order {tie.Key}, so core's unstable sort can write them in either order; give each dimension a distinct Order";
            else if (orders.Count > 1 && orders.Max() - orders.Min() > int.MaxValue)
                orderProblem = $"the Orders span {orders.Min()} to {orders.Max()}{(hasCount ? " (the count column is " + int.MaxValue + ")" : "")}, which overflows core's comparator; use Orders that differ by at most {int.MaxValue}";
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
        // The alias as SQL Server names the column: [patient id], "patient id" and  patient id  are all patient id.
        // AggregateDimension.GetRuntimeName returns the alias as typed, quotes included.
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
