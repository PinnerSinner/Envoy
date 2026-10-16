using Envoy.Core.Languages;

namespace Envoy.Core.Chat;

/// <summary>What Envoy has learned about one player this session.</summary>
public sealed record PlayerInfo(string Name, string? Language, int MessageCount, DateTimeOffset LastSeen);

/// <summary>
/// Remembers which language each player writes in, so replies default to the right language even when two
/// opponents in a team game speak different ones. Names are matched loosely because OCR misreads them.
/// </summary>
public sealed class PlayerDirectory(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, PlayerInfo> _players = new();
    private readonly object _gate = new();

    public IReadOnlyList<PlayerInfo> Players
    {
        get { lock (_gate) return _players.Values.OrderByDescending(p => p.LastSeen).ToList(); }
    }

    /// <summary>The player who spoke most recently (excluding you), if any.</summary>
    public PlayerInfo? LastSpeaker
    {
        get { lock (_gate) return _players.Values.MaxBy(p => p.LastSeen); }
    }

    /// <summary>Records a message. Language may be null when it isn't known yet.</summary>
    public void Observe(string? sender, string? language)
    {
        if (string.IsNullOrWhiteSpace(sender)) return;
        lock (_gate)
        {
            var key = Find(sender) ?? TextSimilarity.Normalize(sender);
            if (key.Length == 0) return;
            var now = _clock.GetUtcNow();
            _players[key] = _players.TryGetValue(key, out var existing)
                ? existing with
                {
                    // A known non-null language is sticky: one English "gg" shouldn't flip a Chinese player to English.
                    Language = language is null ? existing.Language : PreferLanguage(existing, language),
                    MessageCount = existing.MessageCount + 1,
                    LastSeen = now,
                }
                : new PlayerInfo(sender.Trim(), language is null ? null : LanguageCatalog.Normalize(language), 1, now);
        }
    }

    /// <summary>The display names of every player seen this match.</summary>
    public IReadOnlyList<string> KnownNames
    {
        get { lock (_gate) return _players.Values.Select(p => p.Name).ToList(); }
    }

    /// <summary>
    /// The display name of a known player whose <see cref="TextSimilarity.OcrKey"/> equals <paramref name="sender"/>'s
    /// (so "Zhanq Wei" becomes "Zhang Wei"), otherwise <paramref name="sender"/> unchanged (REQ-CHAT-21).
    /// </summary>
    public string? Canonicalize(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender)) return sender;
        var key = TextSimilarity.OcrKey(sender);
        if (key.Length == 0) return sender;
        lock (_gate)
        {
            foreach (var player in _players.Values)
            {
                if (TextSimilarity.OcrKey(player.Name) == key) return player.Name;
            }
        }
        return sender;
    }

    public string? LanguageOf(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender)) return null;
        lock (_gate) return Find(sender) is { } key ? _players[key].Language : null;
    }

    public void Clear()
    {
        lock (_gate) _players.Clear();
    }

    private string? Find(string sender)
    {
        var normalized = TextSimilarity.Normalize(sender);
        if (_players.ContainsKey(normalized)) return normalized;
        return _players.Keys.FirstOrDefault(k => TextSimilarity.Similarity(k, normalized) >= 0.8);
    }

    private static string PreferLanguage(PlayerInfo existing, string observed)
    {
        var normalized = LanguageCatalog.Normalize(observed);
        if (existing.Language is null) return normalized;
        // English shorthand ("gg", "wp") from a player we know writes another language doesn't change them.
        return normalized == "en" ? existing.Language : normalized;
    }
}

/// <summary>Players whose messages are hidden from the overlay (spam, toxicity).</summary>
public static class MuteList
{
    public static bool IsMuted(string? sender, IEnumerable<string> muted)
    {
        if (string.IsNullOrWhiteSpace(sender)) return false;
        var s = TextSimilarity.Normalize(sender);
        return muted.Any(m =>
        {
            var n = TextSimilarity.Normalize(m);
            return n.Length > 0 && (n == s || TextSimilarity.Similarity(n, s) >= 0.85);
        });
    }
}
