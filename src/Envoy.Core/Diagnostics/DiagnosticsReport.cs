using System.Globalization;
using System.IO.Compression;
using System.Text;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Diagnostics;

/// <param name="Scale">DPI scale, e.g. 1.5 for 150 %.</param>
public sealed record MonitorInfo(string Name, bool Primary, int X, int Y, int Width, int Height, double Scale);

public sealed record ProviderLine(string Id, bool KeySet);

public sealed record HotkeyLine(string ActionName, string Combo, string Status);

/// <summary>Everything the report shows besides the settings and the log. Built by the App's DiagnosticsCollector.</summary>
public sealed record DiagnosticsSnapshot
{
    /// <summary><c>AppHost.AppVersion</c> (SemVer, so a beta reads "1.2.0-beta.1"; contract §3).</summary>
    public required string AppVersion { get; init; }
    public string? InformationalVersion { get; init; }
    public required string Runtime { get; init; }
    public required string OsDescription { get; init; }
    public required string Architecture { get; init; }
    public string? Culture { get; init; }
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];

    /// <summary>Profile name only, never window titles.</summary>
    public string? Game { get; init; }
    public string? CaptureState { get; init; }

    /// <summary>"600×200" only, never the position.</summary>
    public string? RegionSize { get; init; }
    public IReadOnlyList<string> OcrInstalled { get; init; } = [];
    public IReadOnlyList<string> OcrSelected { get; init; } = [];
    public IReadOnlyList<ProviderLine> Providers { get; init; } = [];

    /// <summary>Today's <c>AppHost.ProviderStatus</c> (redacted again when rendered).</summary>
    public string? ProviderStatus { get; init; }

    /// <summary>Provider health for the active chain (03, contract §2.1). Rendered as state, counts, kind and status only.</summary>
    public IReadOnlyList<ProviderHealthSnapshot> TranslationHealth { get; init; } = [];

    /// <summary>Only the non-Registered <c>HotkeyResults</c>.</summary>
    public IReadOnlyList<HotkeyLine> Hotkeys { get; init; } = [];
    public SafeModeReason? SafeMode { get; init; }

    /// <summary>Its Settings property is not rendered.</summary>
    public SettingsLoadResult? SettingsLoad { get; init; }
    public CrashMarker? LastCrash { get; init; }
    public TimeSpan Uptime { get; init; }
    public long DroppedLogWrites { get; init; }

    /// <summary>Install kind, channel, state, last check and last background error kind (06, REQ-DIST-31); null → not shown.</summary>
    public string? Updates { get; init; }
}

/// <summary>
/// The plain-text "Copy diagnostics" report (REQ-REL-23): local only, redacted, at most
/// <see cref="MaxClipboardChars"/> characters. When too long, the oldest log lines go first; headers are never dropped.
/// </summary>
public static class DiagnosticsReport
{
    public const int MaxClipboardChars = 60_000;
    public const string Header = "## Envoy diagnostics";

    /// <summary>The section headers, in order. Every one appears in every report.</summary>
    public static IReadOnlyList<string> Sections { get; } =
        ["Envoy", "Windows", "Monitors", "Game & capture", "OCR", "Translation", "Hotkeys", "Settings", "Last crash", "Recent log"];

    public static string Render(DiagnosticsSnapshot snapshot, AppSettings settings, IReadOnlyList<LogEntry> recentLog,
                                int maxLogLines = 50, int maxChars = MaxClipboardChars, LogRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recentLog);
        var r = redactor ?? new LogRedactor();
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(r.Redact(text)).Append('\n');
        void Section(string name) => sb.Append('\n').Append("### ").Append(name).Append('\n');

        sb.Append(Header).Append('\n');

        Section("Envoy");
        Line("Version: " + snapshot.AppVersion + (string.IsNullOrEmpty(snapshot.InformationalVersion) ? "" : " (" + snapshot.InformationalVersion + ")"));
        Line("Runtime: " + snapshot.Runtime);
        Line("Uptime: " + snapshot.Uptime.ToString(@"d\.hh\:mm\:ss", inv));
        Line("Safe mode: " + (snapshot.SafeMode is { } safe ? "on (" + safe + ")" : "off"));
        if (!string.IsNullOrEmpty(snapshot.Updates)) Line("Updates: " + snapshot.Updates);
        Line("Dropped log writes: " + snapshot.DroppedLogWrites.ToString(inv));

        Section("Windows");
        Line("OS: " + snapshot.OsDescription);
        Line("Architecture: " + snapshot.Architecture);
        Line("Culture: " + (snapshot.Culture ?? "unknown"));

        Section("Monitors");
        if (snapshot.Monitors.Count == 0) Line("(none detected)");
        foreach (var m in snapshot.Monitors)
        {
            Line(string.Create(inv, $"- {m.Name}{(m.Primary ? " (primary)" : "")}: {m.Width}×{m.Height} at ({m.X}, {m.Y}), scale {m.Scale * 100:0} %"));
        }

        Section("Game & capture");
        Line("Game: " + (snapshot.Game ?? "none detected"));
        Line("Capture: " + (snapshot.CaptureState ?? "unknown"));
        Line("Chat area size: " + (snapshot.RegionSize ?? "not set"));

        Section("OCR");
        Line("Installed: " + List(snapshot.OcrInstalled));
        Line("Selected: " + List(snapshot.OcrSelected));

        Section("Translation");
        if (snapshot.Providers.Count == 0) Line("(no providers)");
        foreach (var p in snapshot.Providers)
        {
            var info = ProviderCatalog.Find(p.Id);
            var key = info?.Credential == ProviderCredential.None ? "no key needed" : p.KeySet ? "key set" : "key not set";
            Line("- " + (info?.Name ?? p.Id) + " [" + p.Id + "]: " + key);
        }
        Line("Status: " + (string.IsNullOrWhiteSpace(snapshot.ProviderStatus) ? "(none)" : snapshot.ProviderStatus.Replace('\n', ' ')));
        foreach (var h in snapshot.TranslationHealth) Line(HealthLine(h));

        Section("Hotkeys");
        if (snapshot.Hotkeys.Count == 0) Line("Every configured hotkey is registered.");
        foreach (var h in snapshot.Hotkeys) Line("- " + h.ActionName + " (" + (h.Combo.Length == 0 ? "not set" : h.Combo) + "): " + h.Status);

        Section("Settings");
        Line("Settings version: " + AppSettings.CurrentVersion.ToString(inv));
        if (snapshot.SettingsLoad is { } load)
        {
            Line("File version: " + load.FileVersion.ToString(inv));
            Line("Load status: " + load.Status);
            if (load.BackupPath is not null) Line("Backup: " + FileName(load.BackupPath));
            if (load.Error is not null) Line("Load error: " + load.Error);
        }
        sb.Append("```json\n").Append(r.Redact(SettingsRedaction.ToRedactedJson(settings))).Append("\n```\n");

        Section("Last crash");
        if (snapshot.LastCrash is { } crash)
        {
            Line("When: " + crash.Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", inv));
            Line("Version: " + crash.Version);
            Line("Source: " + crash.Source);
            Line("Exception: " + crash.ExceptionType + ": " + crash.Message.Replace('\n', ' '));
            sb.Append("```\n").Append(r.Redact(crash.Stack)).Append("\n```\n");
        }
        else
        {
            Line("None recorded.");
        }

        Section("Recent log");
        var lines = recentLog.TakeLast(Math.Max(0, maxLogLines)).Select(e => r.Redact(LogFormatter.Format(e))).ToList();
        var prefix = sb.ToString();
        const string Open = "```\n";
        const string Close = "```\n";
        // Room for the fences and the "(N earlier lines omitted)" note, whatever N turns out to be.
        var budget = maxChars - prefix.Length - Open.Length - Close.Length - OmittedNote(lines.Count).Length - 1;
        var kept = new List<string>();
        var used = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var cost = lines[i].Length + 1;
            if (used + cost > budget) break;
            kept.Insert(0, lines[i]);
            used += cost;
        }
        var omitted = lines.Count - kept.Count;
        var body = new StringBuilder(prefix).Append(Open);
        if (lines.Count == 0) body.Append("(empty)\n");
        if (omitted > 0) body.Append(OmittedNote(omitted)).Append('\n');
        foreach (var l in kept) body.Append(l).Append('\n');
        body.Append(Close);
        var text = body.ToString();
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    /// <summary>
    /// <c>{id}: {state}, median {ms} ms, ok {n}, failed {n}, last error kind {kind} ({status})</c>: kind and status only,
    /// never <see cref="ProviderHealthSnapshot.LastError"/> text.
    /// </summary>
    internal static string HealthLine(ProviderHealthSnapshot h)
    {
        var inv = CultureInfo.InvariantCulture;
        var median = h.MedianLatency is { } m ? ((long)m.TotalMilliseconds).ToString(inv) : "-";
        var kind = h.LastErrorKind?.ToString() ?? "none";
        var status = h.LastErrorStatus is { } code ? code.ToString(inv) : "-";
        return string.Create(inv, $"- {h.ProviderId}: {h.State}, median {median} ms, ok {h.Successes}, failed {h.Failures}, last error kind {kind} ({status})");
    }

    private static string OmittedNote(int count) =>
        "… (" + count.ToString(CultureInfo.InvariantCulture) + " earlier lines omitted)";

    /// <summary>The file name only (never the folder, which contains the Windows user name), for either separator.</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    private static string List(IReadOnlyList<string> items) => items.Count == 0 ? "(none)" : string.Join(", ", items);
}

/// <summary>"Export diagnostics…": a zip of the report, the redacted settings, the diagnostic log files and the crash marker (REQ-REL-24).</summary>
public static class DiagnosticsBundle
{
    public const string ReportEntry = "report.txt";
    public const string SettingsEntry = "settings.redacted.json";
    public const string CrashEntry = "last-crash.json";

    /// <summary>
    /// Writes the zip. Only files named <c>envoy*.log</c> are added from <paramref name="logFiles"/> (never transcripts,
    /// <c>settings.json</c> or any <c>.bak</c>); each is opened with <c>FileShare.ReadWrite | FileShare.Delete</c>, so
    /// the live log can be included.
    /// </summary>
    public static void Write(Stream destination, string reportText, AppSettings settings, IEnumerable<string> logFiles, string? crashMarkerPath)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(reportText);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logFiles);
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        AddText(zip, ReportEntry, reportText);
        AddText(zip, SettingsEntry, SettingsRedaction.ToRedactedJson(settings));
        foreach (var path in logFiles)
        {
            var name = Path.GetFileName(path);
            if (!RollingFileSink.IsLogFileName(name) || !File.Exists(path)) continue;
            AddFile(zip, "logs/" + name, path);
        }
        if (crashMarkerPath is not null && File.Exists(crashMarkerPath)) AddFile(zip, CrashEntry, crashMarkerPath);
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddFile(ZipArchive zip, string name, string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        source.CopyTo(target);
    }
}
