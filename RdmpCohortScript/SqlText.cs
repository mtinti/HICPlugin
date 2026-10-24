// Finds the patient index table alias, ix<joinable id>, in SQL text. A regular expression over the whole text
// would also match inside string literals and comments, and changing those changes data. This scanner skips
// '...' literals (with '' inside), -- comments (to the end of the line, CR or LF) and /* ... */ comments (which
// may nest). In user SQL the alias is always the qualifier of a column, ix7.admission_date, so the scanner marks
// a name only when a dot follows it and no dot comes before it: source.ix7 is a column, not the alias. The name
// may be bare, [ix7] or "ix7", in any case (SQL Server usually ignores case); its delimiters and letters are kept.

using System;
using System.Collections.Generic;
using System.Text;

namespace RdmpCohortScript;

/// <summary>Marks the ix&lt;joinable id&gt; alias in SQL for a build script.</summary>
internal static class SqlText
{
    /// <summary>
    /// Replaces each whole-word ix&lt;id&gt; outside literals, names and comments with ix{$j&lt;id&gt;}, when
    /// &lt;id&gt; is one of <paramref name="joinableIds"/>. The id is compared as text, so a long number cannot
    /// overflow.
    /// </summary>
    public static string MarkJoinAliases(string sql, ISet<string> joinableIds)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '\'')
                i = CopyQuoted(sql, i, '\'', '\'', sb);
            else if (c is '[' or '"')
            {
                var start = i;
                i = CopyQuoted(sql, i, c, c == '[' ? ']' : '"', new StringBuilder());
                var quoted = sql[start..i];
                var inner = quoted.Length >= 2 ? quoted[1..^1] : "";
                sb.Append(IsAliasQualifier(sql, start, i, inner, joinableIds)
                    ? $"{quoted[0]}{Marked(inner)}{quoted[^1]}"
                    : quoted);
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                i = CopyLineComment(sql, i, sb);
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                i = CopyBlockComment(sql, i, sb);
            else if (IsWordChar(c))
            {
                var start = i;
                while (i < sql.Length && IsWordChar(sql[i]))
                    i++;

                var word = sql[start..i];
                sb.Append(IsAliasQualifier(sql, start, i, word, joinableIds) ? Marked(word) : word);
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    // name is ix<one of the ids>, in any case, with a dot after it (spaces allowed) and no dot before it
    private static bool IsAliasQualifier(string sql, int start, int end, string name, ISet<string> joinableIds)
    {
        if (name.Length <= 2 || !name.StartsWith("ix", StringComparison.OrdinalIgnoreCase) || !IsDigits(name, 2) ||
            !joinableIds.Contains(name[2..]))
            return false;

        var before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(sql[before]))
            before--;
        if (before >= 0 && sql[before] == '.')
            return false;

        var after = end;
        while (after < sql.Length && char.IsWhiteSpace(sql[after]))
            after++;
        return after < sql.Length && sql[after] == '.';
    }

    // keeps the letters as written (ix, IX, Ix) and marks the id
    private static string Marked(string name) => $"{name[..2]}{{$j{name[2..]}}}";

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    private static bool IsDigits(string s, int from)
    {
        for (var k = from; k < s.Length; k++)
            if (!char.IsAsciiDigit(s[k]))
                return false;
        return true;
    }

    // copies an opening char, the text, and the closing char; a doubled closing char inside stands for itself
    private static int CopyQuoted(string sql, int i, char open, char close, StringBuilder sb)
    {
        sb.Append(sql[i++]);
        while (i < sql.Length)
        {
            var c = sql[i++];
            sb.Append(c);
            if (c != close)
                continue;
            if (i < sql.Length && sql[i] == close)
            {
                sb.Append(sql[i++]);
                continue;
            }

            break;
        }

        return i;
    }

    private static int CopyLineComment(string sql, int i, StringBuilder sb)
    {
        while (i < sql.Length && sql[i] is not ('\n' or '\r'))
            sb.Append(sql[i++]);
        return i;
    }

    private static int CopyBlockComment(string sql, int i, StringBuilder sb)
    {
        var depth = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                depth++;
                sb.Append("/*");
                i += 2;
            }
            else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                depth--;
                sb.Append("*/");
                i += 2;
                if (depth == 0)
                    break;
            }
            else
            {
                sb.Append(sql[i++]);
            }
        }

        return i;
    }
}
