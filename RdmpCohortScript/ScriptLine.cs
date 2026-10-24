// One line of a build script, as text: a command, its arguments, and, after "=>", the handles of the objects it
// creates. Arguments are separated by spaces. An argument with a space, a double quote, or nothing in it is
// written in double quotes, and a double quote inside is written twice. Every other character, new lines
// included, stands for itself. The format document (COHORT-SCRIPT-FORMAT.md, section 4) is the reference.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RdmpCohortScript;

/// <summary>One argument of a script line, and whether it was written in quotes.</summary>
public sealed record ScriptArgument(string Text, bool Quoted)
{
    /// <summary>True for the bare word null, which means "not given".</summary>
    public bool IsNull => !Quoted && Text == "null";
}

/// <summary>A parsed build script line.</summary>
public sealed class ScriptLine
{
    public const string HandleArrow = "=>";

    public string Command { get; }
    public IReadOnlyList<ScriptArgument> Arguments { get; }
    public IReadOnlyList<string> Handles { get; }

    private ScriptLine(string command, IReadOnlyList<ScriptArgument> arguments, IReadOnlyList<string> handles)
    {
        Command = command;
        Arguments = arguments;
        Handles = handles;
    }

    /// <summary>Writes a value as one argument: bare when that is safe, otherwise in double quotes.</summary>
    public static string Quote(string value)
    {
        if (value == null)
            return "null";

        var bare = value.Length > 0 && value != "null" && value != HandleArrow &&
                   value.All(c => !char.IsWhiteSpace(c) && c != '"');
        return bare ? value : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>Writes a value in double quotes even when it would be safe bare, as names and SQL always are.</summary>
    public static string QuoteText(string value) =>
        value == null ? "null" : $"\"{value.Replace("\"", "\"\"")}\"";

    /// <summary>Builds a line from a command, its arguments (already quoted) and its handles.</summary>
    public static string Write(string command, IEnumerable<string> arguments, IEnumerable<string> handles = null)
    {
        var parts = new List<string> { command };
        parts.AddRange(arguments);
        var h = handles?.ToArray() ?? Array.Empty<string>();
        if (h.Length > 0)
        {
            parts.Add(HandleArrow);
            parts.AddRange(h);
        }

        return string.Join(" ", parts);
    }

    /// <summary>Reads a line. Throws <see cref="FormatException"/> with a plain message when it cannot.</summary>
    public static ScriptLine Parse(string text)
    {
        var tokens = Tokenize(text ?? "");
        if (tokens.Count == 0 || tokens[0].Quoted)
            throw new FormatException("The line is empty or does not start with a command name.");

        var arrow = tokens.FindIndex(t => !t.Quoted && t.Text == HandleArrow);
        var arguments = (arrow < 0 ? tokens.Skip(1) : tokens.Skip(1).Take(arrow - 1)).ToArray();
        var handles = arrow < 0 ? Array.Empty<string>() : tokens.Skip(arrow + 1).Select(t => t.Text).ToArray();

        if (arrow >= 0 && handles.Length == 0)
            throw new FormatException($"The line has \"{HandleArrow}\" but no handle after it.");

        if (arrow >= 0 && tokens.Skip(arrow + 1).Any(t => t.Quoted || !t.Text.StartsWith('$')))
            throw new FormatException($"Everything after \"{HandleArrow}\" must be a handle starting with $.");

        return new ScriptLine(tokens[0].Text, arguments, handles);
    }

    private static List<ScriptArgument> Tokenize(string text)
    {
        var tokens = new List<ScriptArgument>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                var value = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= text.Length)
                        throw new FormatException("A quoted argument has no closing double quote. Write a double quote inside a value twice.");

                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            value.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    value.Append(text[i++]);
                }

                if (i < text.Length && text[i] != ' ')
                    throw new FormatException("A quoted argument must be followed by a space or the end of the line.");

                tokens.Add(new ScriptArgument(value.ToString(), true));
            }
            else
            {
                var start = i;
                while (i < text.Length && text[i] != ' ')
                {
                    if (text[i] == '"')
                        throw new FormatException("A double quote may only start an argument. Quote the whole argument.");
                    i++;
                }

                tokens.Add(new ScriptArgument(text[start..i], false));
            }
        }

        return tokens;
    }
}
