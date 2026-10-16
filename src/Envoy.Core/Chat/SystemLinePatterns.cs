using System.Text;
using System.Text.RegularExpressions;

namespace Envoy.Core.Chat;

/// <summary>A notice pattern that could not be used. <see cref="Message"/> is the regex error, for the log only.</summary>
/// <param name="Index">0-based position in the pattern list (the card shows <c>Index + 1</c> as the line number).</param>
public sealed record PatternError(int Index, string Pattern, string Message);

/// <summary>
/// The user's game-notice patterns (REQ-CHAT-23, 25), compiled once per settings change. Each pattern matches the whole
/// cleaned line, case-insensitively, with or without a leading timestamp: <c>*</c> matches any run, <c>{player}</c> 1–32
/// characters, and a <c>re:</c> prefix means a raw .NET regular expression. Blank lines and <c>#</c> comments are
/// ignored. Immutable and thread-safe.
/// </summary>
public sealed class SystemLinePatterns
{
    /// <summary>A pattern that runs longer than this counts as no match (REQ-CHAT-25).</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private const string RegexPrefix = "re:";
    private const string PlayerToken = "{player}";

    private readonly Regex[] _patterns;

    private SystemLinePatterns(Regex[] patterns) => _patterns = patterns;

    public static SystemLinePatterns Empty { get; } = new([]);

    /// <summary>How many patterns compiled.</summary>
    public int Count => _patterns.Length;

    /// <summary>Compiles every usable pattern; the others are reported in <paramref name="errors"/> and skipped. Never throws.</summary>
    public static SystemLinePatterns Compile(IEnumerable<string> patterns, out IReadOnlyList<PatternError> errors)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var compiled = new List<Regex>();
        var problems = new List<PatternError>();
        var index = -1;
        foreach (var raw in patterns)
        {
            index++;
            var pattern = (raw ?? "").Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#')) continue;
            try
            {
                var body = pattern.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase)
                    ? pattern[RegexPrefix.Length..].Trim()
                    : FromWildcard(pattern);
                if (body.Length == 0) throw new ArgumentException("The pattern is empty.");
                compiled.Add(new Regex("^(?:" + body + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout));
            }
            catch (ArgumentException ex)
            {
                problems.Add(new PatternError(index, pattern, ex.Message));
            }
        }
        errors = problems;
        return compiled.Count == 0 ? Empty : new SystemLinePatterns([.. compiled]);
    }

    /// <summary>True when any pattern matches the whole line, or the line without its leading timestamp.</summary>
    public bool IsMatch(string cleanedLine)
    {
        ArgumentNullException.ThrowIfNull(cleanedLine);
        if (_patterns.Length == 0) return false;
        var line = cleanedLine.Trim();
        var withoutTime = ChatLineParser.StripTimestamp(line);
        foreach (var pattern in _patterns)
        {
            if (Matches(pattern, line)) return true;
            if (withoutTime.Length != line.Length && Matches(pattern, withoutTime)) return true;
        }
        return false;
    }

    /// <summary>For the settings text box: splits on newlines, trims, and drops empty lines (comments are kept).</summary>
    public static List<string> SplitLines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // not logged per line, to avoid spam
        }
    }

    /// <summary><c>*</c> → <c>.*</c>, <c>{player}</c> → <c>.{1,32}?</c>, whitespace runs → <c>\s+</c>, the rest literal.</summary>
    private static string FromWildcard(string pattern)
    {
        var sb = new StringBuilder(pattern.Length * 2);
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (char.IsWhiteSpace(c))
            {
                while (i + 1 < pattern.Length && char.IsWhiteSpace(pattern[i + 1])) i++;
                sb.Append(@"\s+");
            }
            else if (c == '*')
            {
                sb.Append(".*");
            }
            else if (c == '{' && string.Compare(pattern, i, PlayerToken, 0, PlayerToken.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                sb.Append(".{1,32}?");
                i += PlayerToken.Length - 1;
            }
            else
            {
                sb.Append(Regex.Escape(new string(c, 1)));
            }
        }
        return sb.ToString();
    }
}
