// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>Lexical helpers over T-SQL text that skip string literals, quoted identifiers and comments.</summary>
internal static class SqlText
{
    /// <summary>The index of the parenthesis closing the one at <paramref name="open"/>, or -1 if it is never closed.</summary>
    internal static int ClosingParenthesis(string sql, int open)
    {
        var depth = 0;
        for (var i = open; i < sql.Length;)
        {
            var next = Skip(sql, i);
            if (next != i) { i = next; continue; }
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')' && --depth == 0) return i;
            i++;
        }
        return -1;
    }

    /// <summary>True when the first '(' of <paramref name="expression"/> is closed by its last character, so a leading
    /// <c>NAME(...)</c> is the whole expression and not, say, <c>COUNT(*) * 1.0 / NULLIF(...)</c>.</summary>
    internal static bool IsSingleCall(string expression)
    {
        expression = expression.Trim();
        var open = expression.IndexOf('(');
        return open > 0 && ClosingParenthesis(expression, open) == expression.Length - 1;
    }

    /// <summary>If a comment, string literal or quoted identifier starts at <paramref name="i"/>, the index after it; else <paramref name="i"/>.</summary>
    internal static int Skip(string sql, int i)
    {
        var c = sql[i];
        var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
        if (c == '-' && next == '-')
        {
            // SQL Server ends a line comment at a carriage return as well as at a line feed.
            var end = sql.IndexOfAny(['\r', '\n'], i);
            return end < 0 ? sql.Length : end;
        }
        if (c == '/' && next == '*')
        {
            var nesting = 1;
            i += 2;
            while (i < sql.Length && nesting > 0)
            {
                var n = i + 1 < sql.Length ? sql[i + 1] : '\0';
                if (sql[i] == '/' && n == '*') { nesting++; i += 2; }
                else if (sql[i] == '*' && n == '/') { nesting--; i += 2; }
                else i++;
            }
            return i;
        }
        return c switch
        {
            '\'' => SkipQuoted(sql, i, '\''),
            '[' => SkipQuoted(sql, i, ']'),
            '"' => SkipQuoted(sql, i, '"'),
            _ => i
        };
    }

    private static int SkipQuoted(string sql, int start, char close)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }
}
