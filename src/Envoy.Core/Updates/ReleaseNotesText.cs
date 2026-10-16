using System.Text;
using System.Text.RegularExpressions;

namespace Envoy.Core.Updates;

/// <summary>Release notes for the update banner: Markdown reduced to short plain text (REQ-DIST-16).</summary>
public static partial class ReleaseNotesText
{
    public const int DefaultMaxChars = 1200;

    /// <summary>
    /// Markdown → plain text: drops heading marks, turns "- "/"* " bullets into "• ", [text](url) into text, strips
    /// **, __ and `; collapses blank lines; cuts at the last line break before <paramref name="maxChars"/> and appends
    /// "…". Null or blank → "".
    /// </summary>
    public static string ToPlain(string? markdown, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        var lines = new List<string>();
        var blank = false;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                blank = lines.Count > 0;
                continue;
            }
            var indent = line.Length - trimmed.Length;
            if (trimmed.StartsWith('#')) trimmed = trimmed.TrimStart('#').TrimStart();
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal))
                trimmed = new string(' ', Math.Min(indent, 4)) + "• " + trimmed[2..].TrimStart();
            trimmed = Image().Replace(trimmed, "$1");
            trimmed = Link().Replace(trimmed, "$1");
            trimmed = trimmed.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
            if (trimmed.Trim().Length == 0) continue;
            if (blank) lines.Add("");
            blank = false;
            lines.Add(trimmed);
        }
        var text = string.Join('\n', lines);
        if (text.Length <= maxChars) return text;

        // Cut at the last line break that leaves room for the ellipsis; a single long line is cut at maxChars - 1.
        var limit = maxChars - 1;
        var cut = text.LastIndexOf('\n', Math.Max(0, limit - 1));
        var head = cut > 0 ? text[..cut] : text[..limit];
        return new StringBuilder(head.TrimEnd()).Append('…').ToString();
    }

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();
}
