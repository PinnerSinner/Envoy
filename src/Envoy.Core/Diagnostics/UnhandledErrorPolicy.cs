using System.Text.Json;
using Envoy.Core.Notifications;

namespace Envoy.Core.Diagnostics;

/// <summary>Where an unhandled exception surfaced.</summary>
public enum UnhandledSource
{
    /// <summary>WPF <c>Application.DispatcherUnhandledException</c>.</summary>
    Dispatcher,

    /// <summary>WinForms <c>Application.ThreadException</c> (tray menu callbacks).</summary>
    WinForms,

    /// <summary><c>TaskScheduler.UnobservedTaskException</c>.</summary>
    TaskScheduler,

    /// <summary><c>AppDomain.CurrentDomain.UnhandledException</c> (a dying thread).</summary>
    AppDomain,
}

/// <summary>What the App does with a UI-thread exception.</summary>
public enum UiErrorAction
{
    /// <summary>Let it propagate (smoke test, or an exception while handling one).</summary>
    LeaveUnhandled,

    /// <summary>Mark it handled and show <see cref="UiErrorOutcome.Problem"/> as a toast.</summary>
    ShowProblem,

    /// <summary>Too many errors in a short time: show the one crash-loop message and exit with code 1 (REQ-REL-15).</summary>
    ExitCrashLoop,

    /// <summary>
    /// The crash-loop exit is already under way: mark it handled and show nothing. The crash-loop box pumps messages,
    /// so the recurring source keeps throwing until the user dismisses it; each repeat is only logged.
    /// </summary>
    HandledWhileExiting,
}

public sealed record UiErrorOutcome(UiErrorAction Action, ProblemReport? Problem);

/// <summary>
/// The bodies of the four global exception handlers (REQ-REL-11/12/13). The App only wires the events and applies the
/// outcome, so the decisions are unit-tested here.
/// </summary>
public sealed class UnhandledErrorPolicy(LogHub hub, Func<bool> isSmokeTest, Func<string?> crashMarkerPath,
                                         string version, CrashLoopGuard? crashLoop = null)
{
    public const string Category = "Unhandled";

    private readonly ILog _log = hub.For(Category);
    private int _active;
    private int _exitingCrashLoop;

    /// <summary>True once <see cref="UiErrorAction.ExitCrashLoop"/> has been returned. Never resets.</summary>
    public bool IsExitingCrashLoop => Volatile.Read(ref _exitingCrashLoop) != 0;

    /// <summary>
    /// Logs at Error with the source. During the smoke test → <see cref="UiErrorAction.LeaveUnhandled"/> (CI must fail).
    /// Crash loop tripped → logs Critical, writes the crash marker, flushes → <see cref="UiErrorAction.ExitCrashLoop"/>,
    /// exactly once: every later call returns <see cref="UiErrorAction.HandledWhileExiting"/> without touching the marker.
    /// Otherwise → <see cref="UiErrorAction.ShowProblem"/> with <see cref="ProblemText.Unexpected"/>, keyed by the
    /// exception type and target site. An exception raised while one is being handled logs Critical and is left
    /// unhandled, so the handler never recurses.
    /// </summary>
    public UiErrorOutcome OnUiException(Exception ex, UnhandledSource source)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (Interlocked.Increment(ref _active) > 1)
        {
            Interlocked.Decrement(ref _active);
            _log.Critical("Exception while handling an unhandled exception", ex, LogField.Of("source", source));
            return new UiErrorOutcome(UiErrorAction.LeaveUnhandled, null);
        }
        try
        {
            _log.Error("Unhandled exception", ex, LogField.Of("source", source));
            if (isSmokeTest()) return new UiErrorOutcome(UiErrorAction.LeaveUnhandled, null);
            if (IsExitingCrashLoop) return new UiErrorOutcome(UiErrorAction.HandledWhileExiting, null);
            if (crashLoop?.RecordAndCheckTripped() == true)
            {
                if (Interlocked.Exchange(ref _exitingCrashLoop, 1) != 0) return new UiErrorOutcome(UiErrorAction.HandledWhileExiting, null);
                _log.Critical("Too many errors in a short time; Envoy is closing", null, LogField.Of("source", source));
                WriteMarker(ex, source.ToString());
                hub.Flush();
                return new UiErrorOutcome(UiErrorAction.ExitCrashLoop, null);
            }
            return new UiErrorOutcome(UiErrorAction.ShowProblem, ProblemText.Unexpected(KeyFor(ex)));
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>Logs a Warning "Unobserved task exception" with the exception. The caller calls <c>SetObserved()</c>.</summary>
    public void OnUnobserved(AggregateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var inner = ex.InnerExceptions.Count == 1 ? ex.InnerException! : ex;
        _log.Warn("Unobserved task exception", inner, LogField.Of("source", UnhandledSource.TaskScheduler));
    }

    /// <summary>
    /// Logs Critical with <paramref name="isTerminating"/>, writes the crash marker (when the path is known) and flushes.
    /// Never throws and never touches UI: it runs on the dying thread.
    /// </summary>
    public void OnDomainException(object? exceptionObject, bool isTerminating)
    {
        try
        {
            var ex = exceptionObject as Exception
                     ?? new InvalidOperationException("Non-exception object thrown: " + (exceptionObject?.GetType().FullName ?? "null"));
            _log.Critical("Unhandled exception on a background thread", ex,
                LogField.Of("source", UnhandledSource.AppDomain), LogField.Of("isTerminating", isTerminating));
            WriteMarker(ex, nameof(UnhandledSource.AppDomain));
        }
        catch (Exception)
        {
            // Last line of defence: nothing more can be done on a dying thread.
        }
        finally
        {
            hub.Flush();
        }
    }

    /// <summary>The throttle key of an unexpected exception: type plus the method that threw.</summary>
    public static string KeyFor(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string? site;
        try
        {
            site = ex.TargetSite?.Name;
        }
        catch (Exception)
        {
            site = null;
        }
        return ex.GetType().FullName + "@" + (site ?? "?");
    }

    private void WriteMarker(Exception ex, string source)
    {
        var path = crashMarkerPath();
        if (path is null) return;
        CrashMarker.From(ex, source, version, hub.Redactor, hub.Clock).Write(path);
    }
}

/// <summary>Trips when <c>threshold</c> UI exceptions happen within <c>window</c> (default 5 in 30 s, REQ-REL-15). Thread-safe.</summary>
public sealed class CrashLoopGuard(int threshold = 5, TimeSpan? window = null, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _times = new();
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Records one error; true once <c>threshold</c> or more fall inside the window.</summary>
    public bool RecordAndCheckTripped()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _times.Enqueue(now);
            while (_times.Count > 0 && now - _times.Peek() >= _window) _times.Dequeue();
            return _times.Count >= threshold;
        }
    }
}

/// <summary>
/// <c>{DataFolder}\last-crash.json</c>: what the last crash was, redacted (REQ-REL-13). Read at the next start for the
/// "closed unexpectedly" toast and the diagnostics report.
/// </summary>
public sealed record CrashMarker(DateTimeOffset Time, string Version, string Source, string ExceptionType,
                                 string Message, string Stack)
{
    public const int MaxStackChars = 4000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A marker for <paramref name="ex"/>, with message and stack redacted and the stack capped at 4,000 chars.</summary>
    public static CrashMarker From(Exception ex, string source, string version, LogRedactor redactor, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(redactor);
        var stack = redactor.Redact(ex.ToString());
        if (stack.Length > MaxStackChars) stack = stack[..MaxStackChars];
        return new CrashMarker((clock ?? TimeProvider.System).GetUtcNow(), version, source, ex.GetType().FullName ?? ex.GetType().Name,
            redactor.Redact(ex.Message), stack);
    }

    /// <summary>Writes the marker atomically (temp file, then move). Never throws.</summary>
    public void Write(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is not null) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Writing the marker is best effort: it runs while the process is dying.
        }
    }

    /// <summary>The marker at <paramref name="path"/>, or null when it is missing or unreadable.</summary>
    public static CrashMarker? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var marker = JsonSerializer.Deserialize<CrashMarker>(File.ReadAllText(path), Json);
            return marker is { ExceptionType: not null, Message: not null, Stack: not null, Version: not null, Source: not null } ? marker : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>One line for the report and the Troubleshooting section: time, type and redacted message.</summary>
    public string Summary =>
        Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)
        + " · " + ExceptionType + " · " + Message;
}
