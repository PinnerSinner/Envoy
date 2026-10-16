using System.Text.RegularExpressions;

namespace Envoy.Core.Chat;

/// <summary>
/// Splits OCR'd chat lines into channel / sender / text. Every AoE game formats chat slightly differently
/// ("[All] Name: msg", "Name (Team): msg", "12:04 Name: msg", "Name：消息" ...), so this is deliberately forgiving:
/// anything it can't recognise is kept as plain text rather than dropped. The channel words, brackets, separators and
/// timestamp format come from a <see cref="ChatFormatProfile"/> (REQ-CHAT-30); <see cref="Parse(string)"/> uses the
/// built-in <c>generic</c> profile, which reproduces the original hard-coded rules.
/// </summary>
public static partial class ChatLineParser
{
    [GeneratedRegex(@"^(https?|www)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LooksLikeUrl();

    /// <summary>Parses with the built-in <c>generic</c> profile.</summary>
    public static ChatMessage Parse(string line) => Parse(line, ChatFormatProfile.Generic);

    public static ChatMessage Parse(string line, ChatFormatProfile profile)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(profile);
        var raw = line.Trim();
        var rest = profile.StripTimestamp(raw);

        string? channel = null;
        var prefix = Match(profile.BracketPrefix, rest);
        if (prefix is not null && profile.CanonicalChannel(prefix.Groups["ch"].Value) is { } prefixChannel)
        {
            channel = prefixChannel;
            rest = rest[prefix.Length..];
        }

        string? sender = null;
        var (separator, length) = FindSeparator(rest, profile);
        if (separator > 0)
        {
            var candidate = rest[..separator].Trim();
            if (candidate.Length > 0 && candidate.Length <= profile.MaxSenderLength && !LooksLikeUrl().IsMatch(candidate))
            {
                // "Name (Team): msg" style
                var suffix = Match(profile.BracketSuffix, candidate);
                if (suffix is not null && profile.CanonicalChannel(suffix.Groups["ch"].Value) is { } suffixChannel)
                {
                    channel ??= suffixChannel;
                    candidate = candidate[..suffix.Index].Trim();
                }
                if (candidate.Length > 0)
                {
                    sender = candidate;
                    rest = rest[(separator + length)..];
                }
            }
        }

        return new ChatMessage(raw, channel, sender, rest.Trim());
    }

    /// <summary>The line without a leading timestamp (generic format), for notice patterns.</summary>
    internal static string StripTimestamp(string line) => ChatFormatProfile.Generic.StripTimestamp(line);

    private static Match? Match(Regex regex, string text)
    {
        try
        {
            var m = regex.Match(text);
            return m.Success ? m : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first separator (':' or '：' in generic) that isn't part of a time like 12:30 or a URL scheme; -1 when there
    /// is none within the sender length.
    /// </summary>
    private static (int Index, int Length) FindSeparator(string s, ChatFormatProfile profile)
    {
        for (var i = 1; i < s.Length && i <= profile.MaxSenderLength + 1; i++)
        {
            var separator = profile.Separators.FirstOrDefault(sep => s.AsSpan(i).StartsWith(sep, StringComparison.Ordinal));
            if (separator is null) continue;
            var end = i + separator.Length;
            var digitBefore = char.IsDigit(s[i - 1]);
            var digitAfter = end < s.Length && char.IsDigit(s[end]);
            if (digitBefore && digitAfter) continue;
            if (end + 1 < s.Length && s[end] == '/' && s[end + 1] == '/') return (-1, 0);
            return (i, separator.Length);
        }
        return (-1, 0);
    }
}
