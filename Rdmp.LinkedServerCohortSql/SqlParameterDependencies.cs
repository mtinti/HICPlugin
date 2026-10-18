// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Text;
using FAnsi.Discovery;
using Rdmp.Core.Curation.Data;

namespace Rdmp.LinkedServerCohortSql;

internal static class SqlParameterDependencies
{
    internal static IReadOnlyList<ISqlParameter> Resolve(string sql, IEnumerable<ISqlParameter> parameters,
        ICollection<string> warnings)
    {
        var all = parameters.ToArray();
        var clash = all.GroupBy(p => p.ParameterName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (clash != null)
            throw new LinkedServerRenderException(LinkedServerRenderReason.ReservedParameterName,
                $"parameters {string.Join(", ", clash.Select(p => p.ParameterName))} differ only by case; rename one");
        var byName = all.ToDictionary(p => p.ParameterName, StringComparer.OrdinalIgnoreCase);
        var edges = all.ToDictionary(p => p.ParameterName,
            p => Names(p.Value).Where(n => byName.ContainsKey(n) &&
                !string.Equals(n, p.ParameterName, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(Names(sql).Where(byName.ContainsKey));
        while (pending.TryPop(out var name))
            if (needed.Add(name))
                foreach (var dependency in edges[name]) pending.Push(dependency);

        var remaining = all.Where(p => needed.Contains(p.ParameterName)).ToList();
        var result = new List<ISqlParameter>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            var next = remaining.FindIndex(p => edges[p.ParameterName].All(emitted.Contains));
            if (next < 0)
            {
                warnings.Add("Cyclic parameter dependencies: " + string.Join(", ", remaining.Select(p => p.ParameterName)) +
                    ". Declarations retain core order; resolve the cycle before executing the SQL.");
                return all.Where(p => needed.Contains(p.ParameterName)).ToArray();
            }
            var parameter = remaining[next];
            remaining.RemoveAt(next);
            result.Add(parameter);
            emitted.Add(parameter.ParameterName);
        }
        return result;
    }

    private static IEnumerable<string> Names(string sql) =>
        QuerySyntaxHelper.GetAllParameterNamesFromQuery(WithoutCommentsAndLiterals(sql ?? ""));

    // Preserve token boundaries, including newlines, while ignoring literals, quoted identifiers,
    // nested block comments and line comments. FAnsi remains the parameter-name parser.
    internal static string WithoutCommentsAndLiterals(string sql)
    {
        var result = new StringBuilder(sql.Length);
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                result.Append(' ');
                while (i < sql.Length && sql[i] != '\r' && sql[i] != '\n') i++;
                if (i < sql.Length) result.Append(sql[i]);
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                result.Append(' ');
                i += 2;
                var depth = 1;
                while (i < sql.Length && depth > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                i--;
            }
            else if (c is '\'' or '"' or '[')
            {
                result.Append(' ');
                var end = c == '[' ? ']' : c;
                for (i++; i < sql.Length; i++)
                    if (sql[i] == end)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == end) i++;
                        else break;
                    }
            }
            // The released FAnsi parser misses names after some SQL Server operators (e.g. >@p,
            // %@p). Normalise separators only in this analysis copy; never rewrite emitted SQL.
            else result.Append(c is '>' or '<' or '%' or '!' or '&' or '|' or '^' or '~' ? ' ' : c);
        }
        return result.ToString();
    }
}
