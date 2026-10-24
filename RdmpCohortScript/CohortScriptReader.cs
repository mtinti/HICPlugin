// Reads a build script file: checks that it is format 1 and strict (every value a double-quoted scalar), and
// returns its command lines with the line number of each in the file, so every message can name the line.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace RdmpCohortScript;

/// <summary>One problem found in a build script, with the file line it is on (0 when it concerns the whole file).</summary>
public sealed record CohortScriptProblem(int FileLine, CohortScriptRefusal Reason, string Message)
{
    public override string ToString() => FileLine > 0 ? $"Line {FileLine}: {Message}" : Message;
}

/// <summary>Raised when a build script has problems; lists all of them.</summary>
public sealed class CohortScriptProblemsException : Exception
{
    public IReadOnlyList<CohortScriptProblem> Problems { get; }

    public CohortScriptProblemsException(string file, IReadOnlyList<CohortScriptProblem> problems)
        : base($"The build script {file} cannot be run. Nothing was built.{Environment.NewLine}" +
               string.Join(Environment.NewLine, problems))
    {
        Problems = problems;
    }
}

/// <summary>A command line of a build script, with its place in the file.</summary>
internal sealed record ScriptFileLine(int FileLine, string Text, ScriptLine Parsed);

/// <summary>Reads build script files.</summary>
internal static class CohortScriptReader
{
    /// <summary>
    /// Reads the text of a build script. Returns the lines that parse; adds a problem for each that does not, and
    /// for anything wrong with the file as a whole.
    /// </summary>
    public static List<ScriptFileLine> Read(string text, List<CohortScriptProblem> problems)
    {
        var lines = new List<ScriptFileLine>();
        string version = null;
        var sawCommands = false;
        var plainValues = 0;

        try
        {
            var parser = new Parser(new StringReader(text));
            // the stack holds, per open mapping, whether the next scalar is a key, and per sequence, null
            var stack = new Stack<bool?>();
            string topKey = null;
            var depth = 0;

            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case MappingStart or SequenceStart:
                        // an entry of Commands must be one quoted line, not a mapping or a list
                        if (depth == 2 && topKey == "Commands")
                            problems.Add(new CohortScriptProblem((int)parser.Current.Start.Line, CohortScriptRefusal.NotALine,
                                "This entry of Commands is not a line. Each entry is one command written as a double-quoted string."));
                        stack.Push(parser.Current is MappingStart ? true : null);
                        depth++;
                        break;
                    case AnchorAlias alias:
                        // an alias repeats text defined elsewhere; a build script spells every line out
                        problems.Add(new CohortScriptProblem((int)alias.Start.Line, CohortScriptRefusal.NotALine,
                            $"The file uses the YAML alias *{alias.Value}. A build script writes every line out in full, as a double-quoted string."));
                        AfterValue(stack);
                        break;
                    case MappingEnd:
                    case SequenceEnd:
                        stack.Pop();
                        depth--;
                        AfterValue(stack);
                        break;
                    case Scalar scalar:
                        var isKey = stack.Count > 0 && stack.Peek() == true;
                        if (isKey)
                        {
                            if (depth == 1)
                                topKey = scalar.Value;
                            stack.Pop();
                            stack.Push(false);
                            break;
                        }

                        if (scalar.Style != ScalarStyle.DoubleQuoted)
                            plainValues++;

                        if (depth == 1 && topKey == "cohort-script-format")
                            version = scalar.Value;
                        else if (depth == 2 && topKey == "Commands" && stack.Peek() == null)
                            lines.Add(ParseLine((int)scalar.Start.Line, scalar.Value, problems));

                        AfterValue(stack);
                        break;
                }

                if (depth == 2 && topKey == "Commands" && parser.Current is SequenceStart)
                    sawCommands = true;
            }
        }
        catch (YamlException e)
        {
            problems.Add(new CohortScriptProblem((int)e.Start.Line, CohortScriptRefusal.NotStrictYaml,
                $"The file is not valid YAML: {e.Message}"));
            return lines;
        }

        if (version == null)
            problems.Add(new CohortScriptProblem(0, CohortScriptRefusal.NotFormat1, sawCommands
                ? "The file has no cohort-script-format key, so it was written by the June export. Export the cohort again with this plugin."
                : "The file has no cohort-script-format key. A build script starts with cohort-script-format: \"1\"."));
        else if (version != CohortScriptFormat.Version)
            problems.Add(new CohortScriptProblem(0, CohortScriptRefusal.NotFormat1,
                $"The file is cohort script format {version}, but this plugin reads format {CohortScriptFormat.Version}."));

        if (!sawCommands)
            problems.Add(new CohortScriptProblem(0, CohortScriptRefusal.NotFormat1, "The file has no Commands list."));

        if (plainValues > 0)
            problems.Add(new CohortScriptProblem(0, CohortScriptRefusal.NotStrictYaml,
                $"{plainValues} value(s) in the file are not in double quotes. Every value of a build script is a double-quoted YAML scalar, so no text is lost."));

        return lines;
    }

    // after a value in a mapping, the next scalar is a key again
    private static void AfterValue(Stack<bool?> stack)
    {
        if (stack.Count > 0 && stack.Peek() == false)
        {
            stack.Pop();
            stack.Push(true);
        }
    }

    private static ScriptFileLine ParseLine(int fileLine, string text, List<CohortScriptProblem> problems)
    {
        try
        {
            return new ScriptFileLine(fileLine, text, ScriptLine.Parse(text));
        }
        catch (FormatException e)
        {
            problems.Add(new CohortScriptProblem(fileLine, CohortScriptRefusal.UnparseableLine, e.Message));
            return new ScriptFileLine(fileLine, text, null);
        }
    }

    /// <summary>The lines that parsed.</summary>
    public static IEnumerable<ScriptFileLine> Parsed(this IEnumerable<ScriptFileLine> lines) => lines.Where(l => l.Parsed != null);
}
