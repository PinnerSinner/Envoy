namespace Envoy.Core.Diagnostics;

/// <summary>
/// The last <c>capacity</c> entries in memory (a ring), for the diagnostics report's "Recent log" section and the smoke
/// test. No events: readers poll <see cref="Snapshot"/>. Thread-safe.
/// </summary>
public sealed class MemoryLogSink(int capacity = 500) : ILogSink
{
    private readonly object _gate = new();
    private readonly LogEntry?[] _ring = new LogEntry?[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private int _next;
    private int _count;

    public int Capacity => _ring.Length;

    public void Write(LogEntry entry)
    {
        lock (_gate)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
    }

    public void Flush()
    {
    }

    /// <summary>The newest <paramref name="max"/> entries at or above <paramref name="minimum"/>, oldest first (newest last).</summary>
    public IReadOnlyList<LogEntry> Snapshot(LogLevel minimum = LogLevel.Debug, int max = int.MaxValue)
    {
        lock (_gate)
        {
            var all = new List<LogEntry>(_count);
            var start = (_next - _count + _ring.Length) % _ring.Length;
            for (var i = 0; i < _count; i++)
            {
                var entry = _ring[(start + i) % _ring.Length]!;
                if (entry.Level >= minimum) all.Add(entry);
            }
            return all.Count <= max ? all : all.GetRange(all.Count - max, max);
        }
    }
}
