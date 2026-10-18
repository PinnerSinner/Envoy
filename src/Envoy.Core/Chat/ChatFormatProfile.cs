using System.Text;
using System.Text.RegularExpressions;

namespace Envoy.Core.Chat;

/// <summary>
/// How one game (or family of games) formats chat lines: channel words, brackets, sender separators, timestamps and
/// stitching limits (REQ-CHAT-30, 31). Built by <see cref="ChatFormatCatalog"/> from <c>chat-formats.json</c>; immutable
/// and thread-safe.
/// </summary>
public sealed class ChatFormatProfile
{
    public const string GenericId = "generic";

    /// <summary>The canonical channel names a profile can map aliases to.</summary>
    public static IReadOnlyList<string> CanonicalChannels { get; } = ["All", "Team", "Observers", "Private"];

    private readonly Dictionary<string, string> _aliases;

    internal ChatFormatProfile(
        string id,
        string name,
        IReadOnlyDictionary<string, IReadOnlyList<string>> channels,
        IReadOnlyList<string> brackets,
        IReadOnlyList<string> separators,
        string timestampPattern,
        int maxSenderLength,
        bool stitchEnabled,
        StitchOptions stitching,
        IReadOnlyDictionary<string, IReadOnlyList<string>> systemPatterns)
    {
        Id = id;
        Name = name;
        Channels = channels;
        Brackets = brackets;
        Separators = separators;
        TimestampPattern = timestampPattern;
        MaxSenderLength = maxSenderLength;
        StitchEnabled = stitchEnabled;
        Stitching = stitching;
        SystemPatterns = systemPatterns;

        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, words) in channels)
        {
            foreach (var word in words) _aliases.TryAdd(word.Trim(), canonical);
        }

        _openers = string.Concat(brackets.Select(b => b[0]));
        var openers = ClassEscape(brackets.Select(b => b[0]));
        var closers = ClassEscape(brackets.Select(b => b[1]));
        BracketPrefix = new Regex($@"^\s*[{openers}](?<ch>[^{closers}]{{1,16}})[{closers}]\s*:?\s*", RegexOptions.None, MatchTimeout);
        BracketSuffix = new Regex($@"\s*[{openers}](?<ch>[^{closers}]{{1,16}})[{closers}]\s*$", RegexOptions.None, MatchTimeout);
        Timestamp = new Regex(timestampPattern, RegexOptions.None, MatchTimeout);
    }

    /// <summary>Every profile regex gives up after this long; a timeout means "no match".</summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    public string Id { get; }
    public string Name { get; }

    /// <summary>Canonical channel ("All", "Team", "Observers", "Private") → the words games use for it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Channels { get; }

    /// <summary>Two-character open/close pairs around channel tags, e.g. "[]".</summary>
    public IReadOnlyList<string> Brackets { get; }

    /// <summary>What separates a sender from the message, e.g. ":" and "：".</summary>
    public IReadOnlyList<string> Separators { get; }

    /// <summary>A .NET regex for a leading timestamp (including the space after it).</summary>
    public string TimestampPattern { get; }

    public int MaxSenderLength { get; }
    public bool StitchEnabled { get; }
    public StitchOptions Stitching { get; }

    /// <summary>Notice patterns by language code or <c>*</c>; added to the user's list (REQ-CHAT-29).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SystemPatterns { get; }

    /// <summary>The built-in <c>generic</c> profile.</summary>
    public static ChatFormatProfile Generic => ChatFormatCatalog.BuiltIn.Generic;

    private readonly string _openers;

    /// <summary>True for an opening bracket of this profile's channel tags.</summary>
    internal bool IsOpeningBracket(char c) => _openers.Contains(c, StringComparison.Ordinal);

    internal Regex BracketPrefix { get; }
    internal Regex BracketSuffix { get; }
    internal Regex Timestamp { get; }

    /// <summary>"All"/"Team"/"Observers"/"Private" for a known channel word, otherwise null.</summary>
    public string? CanonicalChannel(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _aliases.TryGetValue(token.Trim(), out var canonical) ? canonical : null;
    }

    /// <summary>The notice patterns for <paramref name="language"/> plus those under <c>*</c>.</summary>
    public IReadOnlyList<string> SystemPatternsFor(string? language)
    {
        var result = new List<string>();
        foreach (var (key, patterns) in SystemPatterns)
        {
            if (key == "*" || (language is not null && Languages.LanguageCatalog.SameLanguage(key, language))) result.AddRange(patterns);
        }
        return result;
    }

    /// <summary>Removes a leading timestamp; a regex timeout leaves the line unchanged.</summary>
    internal string StripTimestamp(string line)
    {
        try
        {
            var m = Timestamp.Match(line);
            return m.Success && m.Index == 0 && m.Length > 0 ? line[m.Length..] : line;
        }
        catch (RegexMatchTimeoutException)
        {
            return line;
        }
    }

    public override string ToString() => Id;

    /// <summary>Characters escaped for use inside a regex character class.</summary>
    private static string ClassEscape(IEnumerable<char> chars)
    {
        var sb = new StringBuilder();
        foreach (var c in chars.Distinct())
        {
            if (c is '\\' or ']' or '[' or '^' or '-') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
