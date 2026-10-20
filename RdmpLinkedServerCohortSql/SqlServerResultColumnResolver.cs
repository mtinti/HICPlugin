// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Text.RegularExpressions;
using FAnsi.Discovery.QuerySyntax;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;

namespace RdmpLinkedServerCohortSql;

// Works out the name, SQL type and collation of each column a set returns, from RDMP's catalogue only, so the script can
// declare the columns in WITH RESULT SETS. It knows a small set of expression patterns: a plain column, COUNT, MIN, MAX, SUM,
// AVG, CAST, and a trailing COLLATE. For anything else it throws, and the caller uses the checked form instead.
internal sealed class SqlServerResultColumnResolver(AggregateConfiguration aggregate)
{
    private const string Identifier = CoreSelectList.IdentifierPattern;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
    private IQuerySyntaxHelper Syntax => aggregate.Catalogue.GetQuerySyntaxHelper();

    /// <summary>
    /// Describes every column RDMP's query builder selects for this set, in RDMP's order (<see cref="CoreSelectList"/>),
    /// because WITH RESULT SETS matches columns by position. RDMP selects every dimension of a cohort set, not only the
    /// identifier; declaring fewer columns fails with error 11537 (the query returned more columns than declared).
    /// </summary>
    internal IReadOnlyList<ResultColumn> Resolve()
    {
        var columns = CoreSelectList.For(aggregate, out var orderProblem);
        // Without RDMP's exact order the USE form cannot declare the columns by position. The caller then uses the checked
        // form, which declares nothing and keeps the columns by name.
        if (orderProblem != null)
            throw new LinkedServerRenderException(LinkedServerRenderReason.RemoteResultTypeUnknown,
                "The plugin cannot tell in which order RDMP writes this set's columns. " + orderProblem, subject: Subject);
        var result = new List<ResultColumn>();
        foreach (var column in columns)
        {
            if (column.Dimension != null) result.Add(Describe(DimensionName(column.Dimension), column.Expression));
            else if (column.Name == null)
                throw new LinkedServerRenderException(LinkedServerRenderReason.RemoteResultTypeUnknown,
                    $"The count column {aggregate.CountSQL} has no alias.", "Give it one, for example COUNT(*) AS n.", Subject);
            else result.Add(Describe(column.Name, column.Expression));
        }
        return result.AsReadOnly();
    }

    internal static string DimensionName(AggregateDimension dimension) =>
        CoreSelectList.NameOf(dimension) ?? throw new LinkedServerRenderException(LinkedServerRenderReason.ExpressionIdentifierNeedsAlias,
            $"Column {dimension.SelectSQL} has no name.", $"Give it an alias, for example {dimension.SelectSQL} AS name.",
            LinkedServerCohortSqlBuilder.Describe(dimension.AggregateConfiguration));

    private string Subject => LinkedServerCohortSqlBuilder.Describe(aggregate);

    /// <summary>
    /// Staged script only. Returns null when a column of a set on the home server keeps, in a temp table, the collation it
    /// has in RDMP's query. Otherwise returns what is wrong, in full sentences. Accepted: an expression whose only COLLATE is
    /// at the end (the staged script repeats it), a plain column or MIN/MAX of one whatever the catalogue records, and any
    /// other expression this class can describe whose result is not text or has a known collation.
    /// </summary>
    internal string HomeCollationProblem(CoreColumn column)
    {
        var trailing = CoreSelectList.TrailingCollation(column.Expression, out _);
        if (HasInnerCollate(column.Expression)) return "The expression uses COLLATE inside it.";
        if (trailing != null) return null;
        // SELECT INTO gives a temp-table column the source column's own collation, and the column then compares exactly as
        // it does in RDMP's query. So a plain column needs no collation in the catalogue.
        if (IsColumnReference(column.Expression)) return null;
        try
        {
            var described = Describe(column.Name ?? "column", column.Expression);
            return IsCharacter(described.SqlType) && string.IsNullOrWhiteSpace(described.Collation)
                ? "The plugin cannot work out the collation of this expression." : null;
        }
        catch (LinkedServerRenderException e)
        {
            // The problem only: the resolver's own fix is for the USE form, not for the staged script.
            return e.Problem;
        }
    }

    /// <summary>True when the expression has a COLLATE anywhere except at its very end.</summary>
    internal static bool HasInnerCollate(string expression)
    {
        CoreSelectList.TrailingCollation(expression, out var inner);
        return Regex.IsMatch(inner, @"\bCOLLATE\b", Options);
    }

    // A plain column, or MIN/MAX of one. Both keep the column's own collation.
    private static bool IsColumnReference(string expression)
    {
        expression = expression.Trim();
        var minMax = Regex.Match(expression, @"^(?:MIN|MAX)\s*\((?<input>.*)\)$", Options);
        if (minMax.Success && SqlText.IsSingleCall(expression)) expression = minMax.Groups["input"].Value.Trim();
        // A variable, or a function without brackets such as SYSTEM_USER, looks like a column name but takes the database's
        // default collation. A temp table would turn that into the column's collation, which compares differently.
        return Regex.IsMatch(expression, $"^(?:{Identifier}?\\s*\\.\\s*)*{Identifier}$", Options) &&
               !expression.StartsWith('@') && !NotColumns.Contains(expression);
    }

    private static readonly HashSet<string> NotColumns = new(StringComparer.OrdinalIgnoreCase)
        { "NULL", "USER", "CURRENT_USER", "SESSION_USER", "SYSTEM_USER", "CURRENT_TIMESTAMP" };

    internal ResultColumn Describe(string name, string expression, string explicitCollation = null)
    {
        expression = expression.Trim();
        var collate = Regex.Match(expression, @"^(?<expression>.*)\s+COLLATE\s+(?<collation>\w+)\s*$", Options);
        if (collate.Success)
        {
            var described = Describe(name, collate.Groups["expression"].Value, collate.Groups["collation"].Value);
            if (IsCharacter(described.SqlType)) return described with { Collation = collate.Groups["collation"].Value };
            throw Unknown(expression, "COLLATE works only on text, and this expression does not return text.");
        }

        var cast = Regex.Match(expression,
            @"^CAST\s*\((?<input>.*)\s+AS\s+(?<type>[a-z][a-z0-9_]*(?:\s*\(\s*(?:max|\d+)(?:\s*,\s*\d+)?\s*\))?)\s*\)$", Options);
        // Only when the CAST is the whole expression. CAST(a AS varchar(5)) + CAST(b AS varchar(5)) is varchar(10), not varchar(5).
        if (cast.Success && SqlText.IsSingleCall(expression))
        {
            var type = ValidType(cast.Groups["type"].Value, cast: true);
            if (!IsCharacter(type)) return new ResultColumn(name, type);
            if (explicitCollation != null) return new ResultColumn(name, type, explicitCollation);
            var input = cast.Groups["input"].Value.Trim();
            var inputCollation = Regex.Match(input, @"\s+COLLATE\s+(?<collation>\w+)\s*$", Options);
            if (inputCollation.Success) return new ResultColumn(name, type, inputCollation.Groups["collation"].Value);
            // A CAST from text to text keeps the source collation. A CAST from a number to text takes the database's
            // default collation, which the catalogue does not record.
            var source = DescribeSource(input);
            if (IsCharacter(source.Data_type) && !string.IsNullOrWhiteSpace(source.Collation))
                return new ResultColumn(name, type, source.Collation);
            throw Unknown(expression, IsCharacter(source.Data_type)
                    ? $"The catalogue records no collation for column {source.Name}."
                    : "A number cast to text takes the database's default collation, which the catalogue does not record.",
                $"Add COLLATE <collation name> after the CAST, for example {expression} COLLATE Latin1_General_CI_AS.");
        }

        var function = Regex.Match(expression, @"^(?<function>COUNT_BIG|COUNT|MIN|MAX|SUM|AVG)\s*\((?<input>.*)\)$", Options);
        // Only when the call is the whole expression. COUNT(*) * 1.0 / NULLIF(COUNT(*) + 1, 0) is not an int. Anything else
        // reaches ResolveColumn, which throws, and the caller then uses the checked form, which declares nothing.
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
                _ => throw Unknown(expression, $"The plugin does not know what SQL Server returns for {operation} of a {baseType} column.",
                    "Wrap the expression in CAST(... AS type) with that type.")
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
        if (string.IsNullOrWhiteSpace(column.Collation))
            throw Unknown(null, $"The catalogue records no collation for column {column.Name}.", "Synchronise the table in RDMP.");
        return new ResultColumn(name, type, column.Collation);
    }

    private string ValidType(string type, bool cast = false)
    {
        if (string.IsNullOrWhiteSpace(type) || !Regex.IsMatch(type.Trim(),
                @"^[a-z][a-z0-9_]*(?:\s*\(\s*(?:max|\d+)(?:\s*,\s*\d+)?\s*\))?$", Options) ||
            Syntax.TypeTranslater.TryGetCSharpTypeForSQLDBType(type) == null)
            throw Unknown(null, string.IsNullOrWhiteSpace(type)
                    ? "The catalogue records no type for this column."
                    : $"The catalogue records the type '{type}', which the plugin does not recognise.",
                "Synchronise the table in RDMP, or wrap the expression in CAST(... AS type).");
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
        if (!match.Success)
            throw Unknown(expression, "The plugin knows the type of plain columns, COUNT, MIN, MAX, SUM, AVG and CAST only, and this expression is none of them.",
                "Wrap it in CAST(... AS type) and give it an alias.");
        var name = Unquote(match.Groups["last"].Value);
        var columns = aggregate.Catalogue.GetTableInfoList(false).SelectMany(t => t.ColumnInfos)
            .Where(c => string.Equals(c.GetRuntimeName(), name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => string.Join("\0", c.TableInfo.GetRuntimeName(), c.GetRuntimeName(), c.Data_type, c.Collation),
                StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        if (columns.Length == 0)
            throw Unknown(expression, $"The catalogue has no column named {name} in this set's tables.", "Synchronise the table in RDMP.");
        if (columns.Length > 1)
            throw Unknown(expression, $"More than one table in this set's catalogue has a column named {name} " +
                                      $"({string.Join(", ", columns.Select(c => c.TableInfo.Name))}), with different types or collations.",
                "Wrap the expression in CAST(... AS type).");
        return columns[0];
    }

    private LinkedServerRenderException Unknown(string expression, string problem, string fix = null) =>
        new(LinkedServerRenderReason.RemoteResultTypeUnknown, expression == null ? problem : $"The plugin cannot describe {expression}. {problem}",
            fix, Subject);

    private static string Unquote(string name) => CoreSelectList.Unquote(name);
}
