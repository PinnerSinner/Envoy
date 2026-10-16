namespace Envoy.Core.Chat;

/// <summary>
/// Chat lines stay on screen for several seconds and get re-read on every capture, often with small OCR differences.
/// This remembers what we've already seen so each message is translated once. A message's identity is (sender, text):
/// channel, timestamp and position are ignored (REQ-CHAT-16), fragments of remembered messages are suppressed
/// (REQ-CHAT-15), and a whole capture is decided at once under one lock (REQ-CHAT-20).
/// </summary>
public sealed class ChatDeduplicator(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<Entry> _recent = [];
    private readonly object _gate = new();

    /// <summary>How similar two reads' texts must be (0..1) to count as the same message.</summary>
    public double Threshold { get; set; } = 0.8;

    /// <summary>How similar two senders must be (0..1) for their messages to be the same one.</summary>
    public double SenderThreshold { get; set; } = 0.75;

    /// <summary>How long a message is remembered. Should exceed how long the game keeps chat visible.</summary>
    public TimeSpan Memory { get; set; } = TimeSpan.FromMinutes(3);

    public int Capacity { get; set; } = 200;

    /// <summary>Returns true (and remembers the line) if it hasn't been seen recently.</summary>
    public bool TryAccept(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return AcceptNew([new ChatMessage(line, null, null, line)]).Count == 1;
    }

    /// <summary>
    /// Decides atomically which messages of one capture are new, in capture order, and remembers every message that
    /// isn't a suppressed fragment, including ones the caller will filter out (REQ-CHAT-18). If one capture holds N
    /// reads of the same message and at most M were seen at once before, N − M of them are new (REQ-CHAT-19).
    /// </summary>
    public IReadOnlyList<ChatMessage> AcceptNew(IReadOnlyList<ChatMessage> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var accepted = new List<ChatMessage>();
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _recent.RemoveAll(e => now - e.SeenAt > Memory);
            var hits = new Dictionary<Entry, int>(ReferenceEqualityComparer.Instance);

            foreach (var message in capture)
            {
                var text = TextSimilarity.Normalize(message.Text);
                if (text.Length == 0) continue;
                var sender = message.Sender is { } s ? TextSimilarity.Normalize(s) : null;
                if (sender is { Length: 0 }) sender = null;

                if (message.Kind == ChatLineKind.Fragment && text.Length >= 2 && IsContained(text)) continue;

                var entry = BestMatch(text, sender);
                if (entry is null)
                {
                    entry = new Entry(sender, text, now);
                    _recent.Add(entry);
                }
                else
                {
                    entry.SeenAt = now;
                    if (text.Length > entry.Text.Length) entry.Text = text;
                    entry.Sender ??= sender;
                }

                var count = hits.TryGetValue(entry, out var n) ? n + 1 : 1;
                hits[entry] = count;
                if (count > entry.MaxSimultaneous)
                {
                    entry.MaxSimultaneous = count;
                    accepted.Add(message);
                }
            }

            if (_recent.Count > Capacity)
            {
                _recent.Sort((a, b) => b.SeenAt.CompareTo(a.SeenAt));
                _recent.RemoveRange(Capacity, _recent.Count - Capacity);
            }
        }
        return accepted;
    }

    public void Clear()
    {
        lock (_gate) _recent.Clear();
    }

    /// <summary>REQ-CHAT-15: the fragment's text is inside a remembered message, exactly or fuzzily.</summary>
    private bool IsContained(string fragment) =>
        _recent.Any(e => e.Text.Length >= fragment.Length - 1 && TextSimilarity.ContainsFuzzy(e.Text, fragment, Threshold));

    private Entry? BestMatch(string text, string? sender)
    {
        Entry? best = null;
        var bestScore = double.MinValue;
        foreach (var entry in _recent)
        {
            var score = TextScore(text, entry.Text);
            if (score < 0) continue;
            if (sender is not null && entry.Sender is not null)
            {
                if (sender != entry.Sender && TextSimilarity.Similarity(sender, entry.Sender) < SenderThreshold) continue;
                if (sender == entry.Sender) score += 0.001; // prefer the same sender among equally good texts
            }
            if (score > bestScore)
            {
                best = entry;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>How well two normalised texts match, or −1 when they are different messages.</summary>
    private double TextScore(string a, string b)
    {
        if (a == b) return 2;
        // A message that's still being revealed / partially covered reads as a prefix of the full one.
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length >= 6 && longer.StartsWith(shorter, StringComparison.Ordinal)) return 1.5;
        var similarity = TextSimilarity.Similarity(a, b);
        return similarity >= Threshold ? similarity : -1;
    }

    private sealed class Entry(string? sender, string text, DateTimeOffset seenAt)
    {
        public string? Sender { get; set; } = sender;
        public string Text { get; set; } = text;
        public DateTimeOffset SeenAt { get; set; } = seenAt;
        public int MaxSimultaneous { get; set; }
    }
}
