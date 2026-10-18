// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Text.RegularExpressions;
using FAnsi.Discovery.QuerySyntax;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;

namespace RdmpLinkedServerCohortSql;

// Metadata-only description of the deliberately small expression grammar supported by WITH RESULT SETS.
internal sealed class SqlServerResultColumnResolver(AggregateConfiguration aggregate)
{
    private const string Identifier = CoreSelectList.IdentifierPattern;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
    private IQuerySyntaxHelper Syntax => aggregate.Catalogue.GetQuerySyntaxHelper();

    /// <summary>
    /// Declares the columns of core's select list for this aggregate, in core's order (<see cref="CoreSelectList"/>), because
    /// WITH RESULT SETS binds by position. 1.0.1 declared only the identifier for a whole remote set, but core selects every
    /// dimension of a cohort set, so a set with an extra dimension failed with Msg 11537 (plan 0.7.9.1).
    /// </summary>
    internal IReadOnlyList<ResultColumn> Resolve()
    {
        var columns = CoreSelectList.For(aggregate, out var orderProblem);
        // Not knowing core's order only rules out the positional EXEC form; the caller falls back to the guarded form,
        // which binds by name.
        if (orderProblem != null) throw Unknown(string.Join(", ", columns.Select(c => c.Expression)), orderProblem);
        var result = new List<ResultColumn>();
        foreach (var column in columns)
        {
            if (column.Dimension != null) result.Add(Describe(DimensionName(column.Dimension), column.Expression));
            else if (column.Name == null) throw Unknown(aggregate.CountSQL, "give the count expression an explicit alias");
            else result.Add(Describe(column.Name, column.Expression));
        }
        return result.AsReadOnly();
    }

    internal static string DimensionName(AggregateDimension dimension) =>
        CoreSelectList.NameOf(dimension) ?? throw new LinkedServerRenderException(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias,
            $"Set {dimension.AggregateConfiguration}: expression {dimension.SelectSQL} needs an explicit alias.");

    /// <summary>
    /// Staged shape only (plan 0.7.3): null when the column of a home leaf keeps the same collation in a temp table as in
    /// the composite's outer select list, otherwise the reason it may not. Accepted: an expression whose only COLLATE is
    /// trailing (mirrored in the projection), and any expression the resolver can describe whose result is not character
    /// or has a known collation (plain column with metadata, MIN/MAX of one, character-to-character CAST of either).
    /// </summary>
    internal string HomeCollationProblem(CoreColumn column)
    {
        var trailing = CoreSelectList.TrailingCollation(column.Expression, out var inner);
        if (Regex.IsMatch(inner, @"\bCOLLATE\b", Options))
            return "it has a COLLATE that is not trailing, whose explicit precedence a temp table would lose";
        if (trailing != null) return null;
        try
        {
            var described = Describe(column.Name ?? "column", column.Expression);
            return IsCharacter(described.SqlType) && string.IsNullOrWhiteSpace(described.Collation)
                ? "its collation is not known" : null;
        }
        catch (LinkedServerRenderException)
        {
            return "its type and collation cannot be derived from the catalogue";
        }
    }

    internal ResultColumn Describe(string name, string expression, string explicitCollation = null)
    {
        expression = expression.Trim();
        var collate = Regex.Match(expression, @"^(?<expression>.*)\s+COLLATE\s+(?<collation>\w+)\s*$", Options);
        if (collate.Success)
        {
            var described = Describe(name, collate.Groups["expression"].Value, collate.Groups["collation"].Value);
            if (IsCharacter(described.SqlType)) return described with { Collation = collate.Groups["collation"].Value };
            throw Unknown(expression, "COLLATE requires a character result");
        }

        var cast = Regex.Match(expression,
            @"^CAST\s*\((?<input>.*)\s+AS\s+(?<type>[a-z][a-z0-9_]*(?:\s*\(\s*(?:max|\d+)(?:\s*,\s*\d+)?\s*\))?)\s*\)$", Options);
        // Only when the CAST is the whole expression: CAST(a AS varchar(5)) + CAST(b AS varchar(5)) is varchar(10).
        if (cast.Success && SqlText.IsSingleCall(expression))
        {
            var type = ValidType(cast.Groups["type"].Value, cast: true);
            if (!IsCharacter(type)) return new ResultColumn(name, type);
            if (explicitCollation != null) return new ResultColumn(name, type, explicitCollation);
            var input = cast.Groups["input"].Value.Trim();
            var inputCollation = Regex.Match(input, @"\s+COLLATE\s+(?<collation>\w+)\s*$", Options);
            if (inputCollation.Success) return new ResultColumn(name, type, inputCollation.Groups["collation"].Value);
            // Character-to-character CAST inherits the source collation. Numeric-to-character CAST
            // uses a database default which the catalogue does not describe, so refuse that case.
            var source = DescribeSource(input);
            if (IsCharacter(source.Data_type) && !string.IsNullOrWhiteSpace(source.Collation))
                return new ResultColumn(name, type, source.Collation);
            throw Unknown(expression, "add an explicit COLLATE to the character CAST");
        }

        var function = Regex.Match(expression, @"^(?<function>COUNT_BIG|COUNT|MIN|MAX|SUM|AVG)\s*\((?<input>.*)\)$", Options);
        // Only when the call is the whole expression: COUNT(*) * 1.0 / NULLIF(COUNT(*) + 1, 0) is not an int. Anything
        // else falls through to ResolveColumn, fails, and the caller uses the guarded form, which declares nothing.
        if (function.Success && SqlText.IsSingleCall(expression))
        {
            var operation = function.Groups["function"].Value.ToUpperInvariant();
            if (operation is "COUNT" or "COUNT_BIG") return new ResultColumn(name, operation == "COUNT" ? "int" : "bigint");
            var column = ResolveColumn(function.Groups["input"].Value);
            if (operation is "MIN" or "MAX") return FromColumn(name, column);
            var sourceType = ValidType(column.Data_type);
            var baseType = sourceType.Split('(')[0].Trim().ToLowerInvariant();
            var type = baseType switch
            {
                "tinyint" or "smallint" or "int" => "int",
                "bigint" => "bigint",
                "money" or "smallmoney" => "money",
                "float" or "real" => "float",
                "decimal" or "numeric" => DecimalAggregateType(sourceType, operation),
                _ => throw Unknown(expression, "use an explicit CAST with the SQL Server aggregate result type")
            };
            return new ResultColumn(name, type);
        }
        return FromColumn(name, ResolveColumn(expression));
    }

    private string DecimalAggregateType(string source, string operation)
    {
        var request = Syntax.TypeTranslater.GetDataTypeRequestForSQLDBType(source);
        var scale = request.Size?.Scale ?? 0;
        return $"decimal(38,{(operation == "AVG" ? Math.Max(scale, 6) : scale)})";
    }

    private ColumnInfo DescribeSource(string expression)
    {
        var minMax = Regex.Match(expression, @"^(?:MIN|MAX)\s*\((?<input>.*)\)$", Options);
        return ResolveColumn(minMax.Success && SqlText.IsSingleCall(expression) ? minMax.Groups["input"].Value : expression);
    }

    private ResultColumn FromColumn(string name, ColumnInfo column)
    {
        var type = ValidType(column.Data_type);
        if (!IsCharacter(type)) return new ResultColumn(name, type);
        if (string.IsNullOrWhiteSpace(column.Collation)) throw Unknown(column.Name, "synchronise the column's collation metadata");
        return new ResultColumn(name, type, column.Collation);
    }

    private string ValidType(string type, bool cast = false)
    {
        if (string.IsNullOrWhiteSpace(type) || !Regex.IsMatch(type.Trim(),
                @"^[a-z][a-z0-9_]*(?:\s*\(\s*(?:max|\d+)(?:\s*,\s*\d+)?\s*\))?$", Options) ||
            Syntax.TypeTranslater.TryGetCSharpTypeForSQLDBType(type) == null)
            throw Unknown(type, "synchronise the column type metadata or use an explicit CAST");
        type = type.Trim();
        return type.ToLowerInvariant() switch
        {
            "datetime2" or "datetimeoffset" or "time" => type + "(7)",
            "decimal" or "numeric" => type + "(18,0)",
            "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary" when cast => type + "(30)",
            _ => type
        };
    }

    internal bool IsCharacter(string type) => !string.IsNullOrWhiteSpace(type) &&
        Syntax.TypeTranslater.TryGetCSharpTypeForSQLDBType(type) == typeof(string);

    private ColumnInfo ResolveColumn(string expression)
    {
        var match = Regex.Match(expression.Trim(), $"^(?:{Identifier}?\\s*\\.\\s*)*(?<last>{Identifier})$", Options);
        if (!match.Success) throw Unknown(expression, "alias the expression with an explicit CAST");
        var name = Unquote(match.Groups["last"].Value);
        var columns = aggregate.Catalogue.GetTableInfoList(false).SelectMany(t => t.ColumnInfos)
            .Where(c => string.Equals(c.GetRuntimeName(), name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => string.Join("\0", c.TableInfo.GetRuntimeName(), c.GetRuntimeName(), c.Data_type, c.Collation),
                StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        if (columns.Length != 1) throw Unknown(expression, columns.Length == 0
            ? "no matching column metadata; synchronise the catalogue"
            : "ambiguous column metadata in " + string.Join(", ", columns.Select(c => c.TableInfo.Name)) + "; use an explicit CAST");
        return columns[0];
    }

    private LinkedServerRenderException Unknown(string expression, string fix) =>
        new(LinkedServerRenderReason.RemoteResultTypeUnknown, $"Set {aggregate}: cannot describe {expression}; {fix}.");

    private static string Unquote(string name) => CoreSelectList.Unquote(name);
}
