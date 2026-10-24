// Writes YAML double-quoted scalars. That is the only YAML style that keeps every character: a new line, a
// carriage return, a tab, a double quote and a backslash are written as escapes, so no text is lost or changed
// on the way through the file. Every value the plugin writes uses it.

using System.Globalization;
using System.Text;

namespace RdmpCohortScript;

/// <summary>Writes a string as a YAML double-quoted scalar.</summary>
internal static class YamlText
{
    public static string DoubleQuoted(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value ?? "")
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when char.IsControl(c) || c is (char)0x85 or (char)0x2028 or (char)0x2029 =>
                    "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
        return sb.Append('"').ToString();
    }
}
