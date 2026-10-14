namespace Envoy.Core.Diagnostics;

/// <summary>
/// Collapses identical entries (same level, category and message) within <see cref="Window"/> (REQ-REL-7). The first
/// occurrence is written; repeats inside the window are only counted; once the window has passed, the next entry of
/// any kind (or <see cref="Drain"/>) writes one summary with <see cref="LogEntry.RepeatCount"/> = n. Not thread-safe:
/// <see cref="LogHub"/> calls it under its lock.
/// </summary>
internal sealed class RepeatSuppressor(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>Above this many tracked keys, expired ones are swept on every entry.</summary>
    private const int SweepThreshold = 64;

    private readonly Dictionary<(LogLevel, string, string), State> _states = [];

    /// <summary>The entries to write for <paramref name="entry"/>, in order (possibly none).</summary>
    public List<LogEntry> Process(LogEntry entry)
    {
        var output = new List<LogEntry>(2);
        var now = clock.GetUtcNow();
        var key = (entry.Level, entry.Category, entry.Message);

        if (_states.TryGetValue(key, out var state) && now - state.WindowStart < Window)
        {
            state.Count++;
            state.Last = entry;
            return output;
        }

        if (state is not null && state.Count > 0) output.Add(Summary(state));
        _states[key] = new State(now, entry);
        output.Add(entry);
        if (_states.Count > SweepThreshold) Sweep(now, output);
        return output;
    }

    /// <summary>Summaries for every key with uncounted repeats; their counters restart.</summary>
    public List<LogEntry> Drain()
    {
        var output = new List<LogEntry>();
        foreach (var state in _states.Values.Where(s => s.Count > 0))
        {
            output.Add(Summary(state));
            state.Count = 0;
        }
        return output;
    }

    private void Sweep(DateTimeOffset now, List<LogEntry> output)
    {
        foreach (var (key, state) in _states.Where(kv => now - kv.Value.WindowStart >= Window).ToList())
        {
            if (state.Count > 0) output.Add(Summary(state));
            _states.Remove(key);
        }
    }

    private static LogEntry Summary(State state) => state.Last with { RepeatCount = state.Count };

    private sealed class State(DateTimeOffset windowStart, LogEntry last)
    {
        public DateTimeOffset WindowStart { get; } = windowStart;
        public LogEntry Last { get; set; } = last;
        public int Count { get; set; }
    }
}
