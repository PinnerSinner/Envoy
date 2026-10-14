using System.Globalization;
using System.Text;

namespace Envoy.Core.Diagnostics;

/// <summary>Severity of a log entry. The file sink writes <c>DBG/INF/WRN/ERR/CRT</c>.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// A named value attached to a log entry. Chat text and player names go only through <see cref="Sensitive"/>, which
/// renders <c>&lt;N chars&gt;</c> unless the user turned on <c>Diagnostics.LogChatText</c> (REQ-REL-5).
/// </summary>
public readonly record struct LogField(string Name, object? Value, bool IsSensitive = false)
{
    public static LogField Of(string name, object? value) => new(name, value);
    public static LogField Sensitive(string name, string? value) => new(name, value, true);
}

/// <summary>A rendered, already-redacted entry. Sinks never see raw values.</summary>
/// <param name="Fields">Space-separated <c>name=value</c> pairs, already redacted; "" when there are none.</param>
/// <param name="Exception">The redacted <c>Exception.ToString()</c>, or null.</param>
/// <param name="RepeatCount">0 for a normal entry; n for the summary of n suppressed repeats (REQ-REL-7).</param>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message,
                              string Fields, string? Exception, int RepeatCount = 0);

/// <summary>A destination for log entries. Called under the hub's lock; may throw (the hub counts and swallows it).</summary>
public interface ILogSink
{
    void Write(LogEntry entry);
    void Flush();
}

/// <summary>A category-bound logger. Writing never throws (REQ-REL-3).</summary>
public interface ILog
{
    string Category { get; }
    bool IsEnabled(LogLevel level);
    void Write(LogLevel level, string message, Exception? exception = null, params LogField[] fields);
}

/// <summary>Level helpers for <see cref="ILog"/>.</summary>
public static class LogExtensions
{
    public static void Debug(this ILog log, string message, params LogField[] fields) =>
        log.Write(LogLevel.Debug, message, null, fields);

    public static void Info(this ILog log, string message, params LogField[] fields) =>
        log.Write(LogLevel.Info, message, null, fields);

    public static void Warn(this ILog log, string message, Exception? ex = null, params LogField[] fields) =>
        log.Write(LogLevel.Warning, message, ex, fields);

    public static void Error(this ILog log, string message, Exception? ex = null, params LogField[] fields) =>
        log.Write(LogLevel.Error, message, ex, fields);

    public static void Critical(this ILog log, string message, Exception? ex = null, params LogField[] fields) =>
        log.Write(LogLevel.Critical, message, ex, fields);
}

/// <summary>A logger that drops everything (for code that runs before, or without, a hub).</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    private NullLog()
    {
    }

    public string Category => "";
    public bool IsEnabled(LogLevel level) => false;

    public void Write(LogLevel level, string message, Exception? exception = null, params LogField[] fields)
    {
    }
}

/// <summary>
/// The one logging hub (REQ-REL-1): filters by level, renders and redacts each entry through <see cref="Redactor"/>,
/// collapses repeats (REQ-REL-7) and fans the entry out to every sink. Thread-safe; no NuGet dependency. A sink that
/// throws is isolated: the failure is counted in <see cref="DroppedWrites"/> and never reaches the caller.
/// </summary>
public sealed class LogHub : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly RepeatSuppressor _repeats;
    private ILogSink[] _sinks = [];
    private volatile LogLevel _minimumLevel = LogLevel.Info;
    private volatile bool _includeSensitive;
    private long _droppedWrites;
    private bool _disposed;

    public LogHub(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _repeats = new RepeatSuppressor(_clock);
    }

    /// <summary>Entries below this level are dropped. Default Info; Debug with <c>Diagnostics.VerboseLogging</c>.</summary>
    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    /// <summary>When true, <see cref="LogField.Sensitive"/> values are written quoted (<c>Diagnostics.LogChatText</c>).</summary>
    public bool IncludeSensitive
    {
        get => _includeSensitive;
        set => _includeSensitive = value;
    }

    /// <summary>The one redactor every entry passes through before any sink sees it.</summary>
    public LogRedactor Redactor { get; } = new();

    /// <summary>Sink writes that failed (unwritable folder, full disk, a throwing sink).</summary>
    public long DroppedWrites => Interlocked.Read(ref _droppedWrites);

    public TimeProvider Clock => _clock;

    public void AddSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate) _sinks = [.. _sinks, sink];
    }

    public void RemoveSink(ILogSink sink)
    {
        lock (_gate) _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
    }

    public ILog For(string category) => new CategoryLog(this, category);

    /// <summary>Writes pending repeat summaries, then flushes every sink. Never throws.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                foreach (var summary in _repeats.Drain()) Deliver(summary);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _droppedWrites);
            }
            foreach (var sink in _sinks)
            {
                try
                {
                    sink.Flush();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _droppedWrites);
                }
            }
        }
    }

    /// <summary>Flushes repeat summaries, then the sinks, then disposes disposable sinks. Never throws.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Flush();
            _disposed = true;
            foreach (var sink in _sinks.OfType<IDisposable>())
            {
                try
                {
                    sink.Dispose();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _droppedWrites);
                }
            }
            _sinks = [];
        }
    }

    internal bool IsEnabled(LogLevel level) => level >= _minimumLevel;

    internal void Write(string category, LogLevel level, string message, Exception? exception, LogField[]? fields)
    {
        if (!IsEnabled(level)) return;
        try
        {
            var renderedFields = RenderFields(fields, _includeSensitive);
            var entry = new LogEntry(
                _clock.GetUtcNow(),
                level,
                category,
                Redactor.Redact(message),
                Redactor.Redact(renderedFields),
                exception is null ? null : Redactor.Redact(exception.ToString()));
            lock (_gate)
            {
                if (_disposed) return;
                foreach (var e in _repeats.Process(entry)) Deliver(e);
            }
        }
        catch (Exception)
        {
            // Rendering or redaction failed (a field whose ToString throws): count it, never throw to the caller.
            Interlocked.Increment(ref _droppedWrites);
        }
    }

    private void Deliver(LogEntry entry)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Write(entry);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _droppedWrites);
            }
        }
    }

    /// <summary>
    /// Space-separated <c>name=value</c>. Sensitive values become <c>&lt;N chars&gt;</c> unless
    /// <paramref name="includeSensitive"/>; then they are quoted with <c>\r</c>, <c>\n</c> and <c>"</c> escaped.
    /// Other values are quoted only when they contain whitespace or quotes.
    /// </summary>
    internal static string RenderFields(LogField[]? fields, bool includeSensitive)
    {
        if (fields is null || fields.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var field in fields)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(field.Name).Append('=');
            if (field.IsSensitive)
            {
                var text = field.Value as string ?? "";
                if (includeSensitive) sb.Append(Quote(text));
                else sb.Append('<').Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(" chars>");
                continue;
            }
            var value = field.Value switch
            {
                null => "",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var v => v.ToString() ?? "",
            };
            sb.Append(NeedsQuotes(value) ? Quote(value) : value);
        }
        return sb.ToString();
    }

    private static bool NeedsQuotes(string value) => value.Any(c => char.IsWhiteSpace(c) || c == '"');

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private sealed class CategoryLog(LogHub hub, string category) : ILog
    {
        public string Category { get; } = category;
        public bool IsEnabled(LogLevel level) => hub.IsEnabled(level);

        public void Write(LogLevel level, string message, Exception? exception = null, params LogField[] fields) =>
            hub.Write(Category, level, message ?? "", exception, fields);
    }
}
