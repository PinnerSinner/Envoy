namespace Envoy.Core.Chat;

/// <summary>
/// Chat lines stay on screen for several seconds and get re-read on every capture, often with small OCR differences.
/// This remembers what we've already seen so each message is translated once.
/// </summary>
public sealed class ChatDeduplicator(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly LinkedList<(string Normalized, DateTimeOffset SeenAt)> _recent = new();
    private readonly object _gate = new();

    /// <summary>How similar two reads must be (0..1) to count as the same line.</summary>
    public double Threshold { get; set; } = 0.8;

    /// <summary>How long a line is remembered. Should exceed how long the game keeps chat visible.</summary>
    public TimeSpan Memory { get; set; } = TimeSpan.FromMinutes(3);

    public int Capacity { get; set; } = 200;

    /// <summary>Returns true (and remembers the line) if it hasn't been seen recently.</summary>
    public bool TryAccept(string line)
    {
        var normalized = TextSimilarity.Normalize(line);
        if (normalized.Length == 0) return false;

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Expire(now);

            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (IsSameLine(normalized, node.Value.Normalized))
                {
                    // Keep the freshest timestamp so a line that stays on screen keeps being suppressed.
                    node.Value = (LongerOf(normalized, node.Value.Normalized), now);
                    return false;
                }
            }

            _recent.AddFirst((normalized, now));
            while (_recent.Count > Capacity) _recent.RemoveLast();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) _recent.Clear();
    }

    private bool IsSameLine(string a, string b)
    {
        if (a == b) return true;
        // A line that's still being revealed / partially covered reads as a prefix of the full line.
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length >= 6 && longer.StartsWith(shorter, StringComparison.Ordinal)) return true;
        return TextSimilarity.Similarity(a, b) >= Threshold;
    }

    private static string LongerOf(string a, string b) => a.Length >= b.Length ? a : b;

    private void Expire(DateTimeOffset now)
    {
        while (_recent.Last is { } last && now - last.Value.SeenAt > Memory) _recent.RemoveLast();
    }
}
