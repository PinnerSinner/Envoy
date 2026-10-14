using System.Text.Json;

namespace Envoy.Core.Diagnostics;

/// <summary>Why Envoy started in safe mode.</summary>
public enum SafeModeReason
{
    /// <summary>Started with <c>--safe-mode</c>.</summary>
    CommandLine,

    /// <summary>A Shift key was held while Envoy started.</summary>
    ShiftHeld,

    /// <summary>The previous starts never became healthy (REQ-REL-42).</summary>
    RepeatedStartupFailures,
}

/// <summary>Whether this start is in safe mode (REQ-REL-39).</summary>
public static class SafeModeDecision
{
    public const string Flag = "--safe-mode";

    /// <summary>Unfinished starts that switch the next start to safe mode automatically (REQ-REL-42).</summary>
    public const int AutoThreshold = 2;

    /// <summary>
    /// <see cref="Flag"/> &gt; Shift held &gt; (<paramref name="autoThreshold"/> &gt; 0 and
    /// <paramref name="unfinishedStarts"/> ≥ it) &gt; null. A threshold of 0 disables the automatic switch.
    /// </summary>
    public static SafeModeReason? Decide(IReadOnlyList<string> args, bool shiftHeld, int unfinishedStarts, int autoThreshold = 0)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase))) return SafeModeReason.CommandLine;
        if (shiftHeld) return SafeModeReason.ShiftHeld;
        if (autoThreshold > 0 && unfinishedStarts >= autoThreshold) return SafeModeReason.RepeatedStartupFailures;
        return null;
    }
}

/// <param name="UnfinishedStarts">Consecutive earlier starts that never became healthy.</param>
/// <param name="PreviousSessionEndedUncleanly">The previous session neither exited through ExitApp nor is still marked clean.</param>
public sealed record StartupHistory(int UnfinishedStarts, bool PreviousSessionEndedUncleanly);

/// <summary>
/// <c>{DataFolder}\session.json</c>: "starting" from <see cref="BeginStartup"/> until <see cref="MarkHealthy"/>, then
/// "running" until <see cref="EndSession"/> deletes it on a clean exit. A file left behind means the previous session
/// ended uncleanly (REQ-REL-22); consecutive "starting" records count unfinished starts (REQ-REL-42). Never throws.
/// </summary>
public sealed class StartupSentinel(string path, TimeProvider? clock = null)
{
    private const string Starting = "starting";
    private const string Running = "running";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Path { get; } = path;

    /// <summary>Reads the previous record, then records "starting".</summary>
    public StartupHistory BeginStartup()
    {
        var previous = Read(out var existed);
        var unfinished = previous?.State == Starting ? Math.Max(0, previous.UnfinishedStarts) : 0;
        Write(new Record(Starting, unfinished + 1, _clock.GetUtcNow()));
        return new StartupHistory(unfinished, existed);
    }

    /// <summary>Records "running" and resets the unfinished count.</summary>
    public void MarkHealthy() => Write(new Record(Running, 0, _clock.GetUtcNow()));

    /// <summary>Deletes the record on a clean exit.</summary>
    public void EndSession()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start reports an unclean end; harmless.
        }
    }

    private Record? Read(out bool existed)
    {
        existed = false;
        try
        {
            if (!File.Exists(Path)) return null;
            existed = true;
            return JsonSerializer.Deserialize<Record>(File.ReadAllText(Path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private void Write(Record record)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(record, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker Envoy only loses crash detection.
        }
    }

    private sealed record Record(string State, int UnfinishedStarts, DateTimeOffset Time);
}

/// <summary>What start-up creates (REQ-REL-40). Later areas add their own fields in their own step.</summary>
public sealed record StartupPlan(bool CreateOverlay, bool RegisterHotkeys, bool AutoStartCapture, bool ShowMainWindow, bool SafeMode)
{
    /// <summary>
    /// Safe mode: no overlay, no hotkeys, no automatic capture, MainWindow always shown. Otherwise overlay and hotkeys
    /// on, capture per <paramref name="startCapturingAutomatically"/>, and MainWindow unless started minimized after
    /// the first run.
    /// </summary>
    public static StartupPlan For(SafeModeReason? safeMode, bool minimized, bool firstRunCompleted, bool startCapturingAutomatically) =>
        safeMode is not null
            ? new StartupPlan(CreateOverlay: false, RegisterHotkeys: false, AutoStartCapture: false, ShowMainWindow: true, SafeMode: true)
            : new StartupPlan(CreateOverlay: true, RegisterHotkeys: true, AutoStartCapture: startCapturingAutomatically,
                ShowMainWindow: !minimized || !firstRunCompleted, SafeMode: false);
}
