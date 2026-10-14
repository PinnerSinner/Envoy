using System.IO.Compression;
using System.Text.RegularExpressions;
using Envoy.Core.Diagnostics;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests.Diagnostics;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "envoy-diag-" + Guid.NewGuid().ToString("N"));

    public DiagnosticsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static AppSettings PrivateSettings()
    {
        var settings = new AppSettings { MyPlayerName = "ZZPLAYERZZ", MutedPlayers = ["MUTEDZZ"] };
        settings.Translation.Primary = ProviderCatalog.DeepL;
        settings.Translation.Fallbacks = [ProviderCatalog.Claude, ProviderCatalog.GoogleFree];
        settings.Translation.ClaudeKey = "sk-ant-TESTSECRET123";
        settings.Translation.DeepLKey = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b:fx";
        settings.Capture.Regions["aoe2de"] = new ScreenRegion(1234, 987, 600, 200);
        return settings;
    }

    private static DiagnosticsSnapshot Snapshot(AppSettings settings) => new()
    {
        AppVersion = "1.2.3",
        InformationalVersion = "1.2.3-beta",
        Runtime = ".NET 8.0.10",
        OsDescription = "Microsoft Windows 10.0.22631",
        Architecture = "X64",
        Culture = "en-GB",
        Monitors = [new MonitorInfo(@"\\.\DISPLAY1", true, 0, 0, 2560, 1440, 1.5)],
        Game = "Age of Empires II: Definitive Edition",
        CaptureState = "Reading chat",
        RegionSize = SettingsRedaction.Size(settings.Capture.Regions["aoe2de"]),
        OcrInstalled = ["en-US", "zh-Hans-CN"],
        OcrSelected = settings.Capture.OcrLanguages,
        Providers = [new ProviderLine(ProviderCatalog.DeepL, true), new ProviderLine(ProviderCatalog.Claude, true), new ProviderLine(ProviderCatalog.GoogleFree, false)],
        ProviderStatus = "DeepL failed — https://api-free.deepl.com/v2/translate?auth_key=0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b:fx",
        Hotkeys = [new HotkeyLine("Reply", "Ctrl+Shift+T", "Taken")],
        SettingsLoad = new SettingsLoadResult(settings, SettingsLoadStatus.RecoveredFromBackup, 1, @"C:\x\settings.json.bak", "JsonException: bad"),
        LastCrash = new CrashMarker(DateTimeOffset.UnixEpoch, "1.2.2", "AppDomain", "System.NullReferenceException", "Object reference not set", "at X.Y()"),
        Uptime = TimeSpan.FromMinutes(12),
        DroppedLogWrites = 3,
    };

    [Fact]
    public void Crash_marker_round_trips_and_redacts()
    {
        var redactor = new LogRedactor();
        redactor.SetSecrets(["my-registered-key"]);
        Exception ex;
        try
        {
            throw new InvalidOperationException("calling https://h/x?key=abc with my-registered-key " + new string('s', 5000));
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero));
        var marker = CrashMarker.From(ex, "Dispatcher", "1.0.0", redactor, clock);
        Assert.DoesNotContain("my-registered-key", marker.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key=abc", marker.Stack, StringComparison.Ordinal);
        Assert.True(marker.Stack.Length <= CrashMarker.MaxStackChars);

        var path = Path.Combine(_folder, "last-crash.json");
        marker.Write(path);
        Assert.Equal(marker, CrashMarker.TryRead(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Contains("System.InvalidOperationException", marker.Summary, StringComparison.Ordinal);

        Assert.Null(CrashMarker.TryRead(Path.Combine(_folder, "missing.json")));
        File.WriteAllText(path, "{ not json");
        Assert.Null(CrashMarker.TryRead(path));
        File.WriteAllText(path, "[]");
        Assert.Null(CrashMarker.TryRead(path));
        File.WriteAllText(path, "{}");
        Assert.Null(CrashMarker.TryRead(path));
    }

    [Fact]
    public void Report_has_all_sections_and_no_private_values()
    {
        var settings = PrivateSettings();
        var redactor = new LogRedactor();
        redactor.SetSecrets(SettingsRedaction.SecretValues(settings));
        var log = new[] { new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Warning, "Translation", "Provider failed", "provider=deepl", null) };

        var report = DiagnosticsReport.Render(Snapshot(settings), settings, log, redactor: redactor);

        Assert.StartsWith(DiagnosticsReport.Header, report, StringComparison.Ordinal);
        foreach (var section in DiagnosticsReport.Sections) Assert.Contains("### " + section + "\n", report, StringComparison.Ordinal);
        Assert.Contains("DeepL [deepl]: key set", report, StringComparison.Ordinal);
        Assert.Contains("[claude]: key set", report, StringComparison.Ordinal);
        Assert.Contains("[google-free]: no key needed", report, StringComparison.Ordinal);
        Assert.Contains("Chat area size: 600×200", report, StringComparison.Ordinal);
        Assert.Contains("\"aoe2de\": \"600×200\"", report, StringComparison.Ordinal);
        Assert.Contains("Reply (Ctrl+Shift+T): Taken", report, StringComparison.Ordinal);
        Assert.Contains("Load status: RecoveredFromBackup", report, StringComparison.Ordinal);
        Assert.Contains("Backup: settings.json.bak", report, StringComparison.Ordinal);
        Assert.Contains("Dropped log writes: 3", report, StringComparison.Ordinal);
        Assert.Contains("scale 150 %", report, StringComparison.Ordinal);
        Assert.Contains("System.NullReferenceException", report, StringComparison.Ordinal);
        Assert.Contains("WRN [Translation] Provider failed provider=deepl", report, StringComparison.Ordinal);

        foreach (var secret in new[] { "TESTSECRET123", "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b", "ZZPLAYERZZ", "MUTEDZZ", "1234", "987", @"C:\x" })
            Assert.DoesNotContain(secret, report, StringComparison.Ordinal);
        Assert.True(report.Length <= DiagnosticsReport.MaxClipboardChars);
    }

    [Fact]
    public void Report_is_capped_by_dropping_old_log_lines()
    {
        var settings = PrivateSettings();
        var log = Enumerable.Range(0, 5000)
            .Select(i => new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, "Bulk", "line " + i + " " + new string('x', 100), "", null))
            .ToList();

        var report = DiagnosticsReport.Render(Snapshot(settings), settings, log, maxLogLines: 5000, maxChars: 20_000);

        Assert.True(report.Length <= 20_000, $"length {report.Length}");
        foreach (var section in DiagnosticsReport.Sections) Assert.Contains("### " + section, report, StringComparison.Ordinal);
        Assert.Contains("line 4999 ", report, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0 ", report, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"… \(\d+ earlier lines omitted\)"), report);
        Assert.EndsWith("```\n", report, StringComparison.Ordinal);

        var defaultCap = DiagnosticsReport.Render(Snapshot(settings), settings, log);
        Assert.Contains("line 4999 ", defaultCap, StringComparison.Ordinal);
        Assert.DoesNotContain("line 4949 ", defaultCap, StringComparison.Ordinal); // only the last 50
    }

    [Fact]
    public void Crash_loop_guard_trips_only_inside_window()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var guard = new CrashLoopGuard(3, TimeSpan.FromSeconds(30), clock);
        Assert.False(guard.RecordAndCheckTripped());
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.False(guard.RecordAndCheckTripped());
        clock.Advance(TimeSpan.FromSeconds(15)); // the first one is now outside the window
        Assert.False(guard.RecordAndCheckTripped());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(guard.RecordAndCheckTripped());
    }

    [Fact]
    public void Bundle_contains_only_expected_entries()
    {
        var logs = Path.Combine(_folder, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "envoy.log"), "current");
        File.WriteAllText(Path.Combine(logs, "envoy.1.log"), "older");
        File.WriteAllText(Path.Combine(logs, "chat-2026-10-14_180000.txt"), "chat transcript");
        File.WriteAllText(Path.Combine(_folder, "settings.json"), "raw");
        File.WriteAllText(Path.Combine(_folder, "settings.json.bak"), "raw");
        var crash = Path.Combine(_folder, "last-crash.json");
        File.WriteAllText(crash, "{}");

        using var open = new FileStream(Path.Combine(logs, "envoy.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var zipStream = new MemoryStream();
        var settings = PrivateSettings();
        DiagnosticsBundle.Write(zipStream, "## Envoy diagnostics", settings, Directory.GetFiles(logs).Append(Path.Combine(_folder, "settings.json")), crash);

        zipStream.Position = 0;
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.Equal(["last-crash.json", "logs/envoy.1.log", "logs/envoy.log", "report.txt", "settings.redacted.json"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var reader = new StreamReader(zip.GetEntry("settings.redacted.json")!.Open());
        var json = reader.ReadToEnd();
        Assert.DoesNotContain("TESTSECRET123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ZZPLAYERZZ", json, StringComparison.Ordinal);
    }
}

public class ProblemTextTests
{
    [Fact]
    public void Texts_follow_voice_rules()
    {
        var reports = ProblemText.All.Append(ProblemText.ReplyBlocked("Nothing was typed because the window you were replying to has closed.")).ToList();
        Assert.True(reports.Count >= 10);
        foreach (var report in reports)
        {
            foreach (var text in new[] { report.Title, report.Detail })
            {
                Assert.False(string.IsNullOrWhiteSpace(text), report.Key);
                Assert.DoesNotMatch(new Regex(@"\{\d+\}"), text);
                Assert.False(text.StartsWith("Error", StringComparison.OrdinalIgnoreCase), text);
                Assert.True(text.TrimEnd().EndsWith('.'), "whole sentences end with a full stop: " + text);
                Assert.True(char.IsUpper(text[0]), text);
            }
            Assert.False(string.IsNullOrWhiteSpace(report.Key));
        }

        foreach (var text in new[] { SafeModeText.Banner, SafeModeText.HotkeysOff, SafeModeText.ReasonCommandLine, SafeModeText.ReasonShiftHeld,
                     SafeModeText.ReasonRepeatedFailures, DiagnosticsText.Copied, DiagnosticsText.Privacy, DataText.ResetMessage })
        {
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), text);
            Assert.False(text.StartsWith("Error", StringComparison.OrdinalIgnoreCase), text);
        }
        Assert.Equal("Saved the diagnostics file to C:\\d.zip.", DiagnosticsText.Exported("C:\\d.zip"));
        Assert.Equal(DiagnosticsText.NoCrash, DiagnosticsText.LastCrash(null));
        Assert.Equal("", SafeModeText.Reason(null));
    }

    [Fact]
    public void Data_texts_list_the_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), "envoy-x");
        var files = new[] { new DataFile(Path.Combine(folder, "settings.json"), "settings.json", 10), new DataFile(Path.Combine(folder, "logs", "envoy.log"), "logs/envoy*.log", 5) };
        var message = DataText.DeleteAllMessage(files, folder);
        Assert.Contains("delete 2 files", message, StringComparison.Ordinal);
        Assert.Contains("• settings.json", message, StringComparison.Ordinal);
        Assert.Equal(DataText.DeleteAllNothing, DataText.DeleteAllMessage([], folder));
        Assert.Contains("• a.log", DataText.SomeFilesKept(["a.log"]), StringComparison.Ordinal);
    }
}
