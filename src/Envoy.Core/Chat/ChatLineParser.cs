using System.Text.RegularExpressions;

namespace Envoy.Core.Chat;

/// <summary>
/// Splits OCR'd chat lines into channel / sender / text. Every AoE game formats chat slightly differently
/// ("[All] Name: msg", "Name (Team): msg", "12:04 Name: msg", "Name：消息" ...), so this is deliberately forgiving:
/// anything it can't recognise is kept as plain text rather than dropped.
/// </summary>
public static partial class ChatLineParser
{
    private const int MaxSenderLength = 32;

    [GeneratedRegex(@"^\s*[\[(]?\d{1,2}:\d{2}(?::\d{2})?[\])]?\s+")]
    private static partial Regex TimestampPrefix();

    [GeneratedRegex(@"^\s*[\[(<](?<ch>[^\])>]{1,16})[\])>]\s*:?\s*")]
    private static partial Regex BracketPrefix();

    [GeneratedRegex(@"\s*[\[(<](?<ch>[^\])>]{1,16})[\])>]\s*$")]
    private static partial Regex BracketSuffix();

    [GeneratedRegex(@"^(https?|www)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LooksLikeUrl();

    private static readonly HashSet<string> KnownChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "everyone", "team", "allies", "ally", "observers", "observer", "spectators", "private", "whisper",
        "全部", "所有人", "所有", "队伍", "隊伍", "团队", "團隊", "盟友", "观察者", "觀察者", "私聊",
        "전체", "팀", "아군", "관전자", "все", "команда", "союзники", "todos", "equipo", "aliados", "equipe",
        "alle", "verbündete", "tous", "équipe", "alliés", "全員", "チーム", "味方",
    };

    public static ChatMessage Parse(string line)
    {
        var raw = line.Trim();
        var rest = TimestampPrefix().Replace(raw, "", 1);

        string? channel = null;
        var prefix = BracketPrefix().Match(rest);
        if (prefix.Success && KnownChannels.Contains(prefix.Groups["ch"].Value.Trim()))
        {
            channel = Canonical(prefix.Groups["ch"].Value.Trim());
            rest = rest[prefix.Length..];
        }

        string? sender = null;
        var colon = FindSenderColon(rest);
        if (colon > 0)
        {
            var candidate = rest[..colon].Trim();
            if (candidate.Length is > 0 and <= MaxSenderLength && !LooksLikeUrl().IsMatch(candidate))
            {
                // "Name (Team): msg" style
                var suffix = BracketSuffix().Match(candidate);
                if (suffix.Success && KnownChannels.Contains(suffix.Groups["ch"].Value.Trim()))
                {
                    channel ??= Canonical(suffix.Groups["ch"].Value.Trim());
                    candidate = candidate[..suffix.Index].Trim();
                }
                if (candidate.Length > 0)
                {
                    sender = candidate;
                    rest = rest[(colon + 1)..];
                }
            }
        }

        return new ChatMessage(raw, channel, sender, rest.Trim());
    }

    /// <summary>First ':' or full-width '：' that isn't part of a time like 12:30 or a URL scheme.</summary>
    private static int FindSenderColon(string s)
    {
        for (var i = 1; i < s.Length && i <= MaxSenderLength + 1; i++)
        {
            if (s[i] is not (':' or '：')) continue;
            var digitBefore = char.IsDigit(s[i - 1]);
            var digitAfter = i + 1 < s.Length && char.IsDigit(s[i + 1]);
            if (digitBefore && digitAfter) continue;
            if (i + 2 < s.Length && s[i + 1] == '/' && s[i + 2] == '/') return -1;
            return i;
        }
        return -1;
    }

    private static string Canonical(string channel) => channel.ToLowerInvariant() switch
    {
        "all" or "everyone" or "全部" or "所有人" or "所有" or "전체" or "все" or "todos" or "alle" or "tous" or "全員" => "All",
        "team" or "allies" or "ally" or "队伍" or "隊伍" or "团队" or "團隊" or "盟友" or "팀" or "아군" or "команда"
            or "союзники" or "equipo" or "aliados" or "equipe" or "verbündete" or "équipe" or "alliés" or "チーム" or "味方" => "Team",
        "observers" or "observer" or "spectators" or "观察者" or "觀察者" or "관전자" => "Observers",
        "private" or "whisper" or "私聊" => "Private",
        _ => channel,
    };
}
