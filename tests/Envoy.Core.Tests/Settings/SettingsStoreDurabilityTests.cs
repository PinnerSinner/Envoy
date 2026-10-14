using System.Text.Json.Nodes;
using Envoy.Core.Diagnostics;
using Envoy.Core.Settings;
using Envoy.Core.Tests.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests.Settings;

/// <summary>Encrypts as "enc:" + reversed text and refuses anything else, like DPAPI on another PC.</summary>
internal sealed class ReversingProtector : ISecretProtector
{
    public string Protect(string s) => "enc:" + new string(s.Reverse().ToArray());

    public string Unprotect(string s) =>
        s.StartsWith("enc:", StringComparison.Ordinal) ? new string(s[4..].Reverse().ToArray()) : throw new FormatException("not ours");
}

public sealed class SettingsStoreDurabilityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "envoy-store-" + Guid.NewGuid().ToString("N"));
    private readonly LogHub _hub = new();
    private readonly ListSink _sink = new();

    public SettingsStoreDurabilityTests()
    {
        Directory.CreateDirectory(_folder);
        _hub.AddSink(_sink);
    }

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        _hub.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private SettingsStore Store(TimeProvider? clock = null) => new(SettingsPath, new ReversingProtector(), _hub.For("Settings"), clock);

    [Fact]
    public void Save_always_writes_current_version()
    {
        var settings = new AppSettings { Version = 42 };
        Store().Save(settings);
        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal(AppSettings.CurrentVersion, (int)json["version"]!);
        Assert.Equal(1, AppSettings.CurrentVersion);
        Assert.Equal(42, settings.Version); // the in-memory object is untouched
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_recovers_from_last_known_good()
    {
        var store = Store();
        var settings = new AppSettings { MyLanguage = "de" };
        settings.Translation.DeepLKey = "deepl-key-123";
        store.Save(settings);
        Assert.Equal(SettingsLoadStatus.Loaded, store.LoadWithReport().Status); // refreshes .bak

        File.WriteAllText(SettingsPath, "{ \"myLanguage\": \"fr\", "); // truncated write
        var result = Store().LoadWithReport();

        Assert.Equal(SettingsLoadStatus.RecoveredFromBackup, result.Status);
        Assert.Equal("de", result.Settings.MyLanguage);
        Assert.Equal("deepl-key-123", result.Settings.Translation.DeepLKey);
        Assert.Equal(store.LastKnownGoodPath, result.BackupPath);
        Assert.NotNull(result.Error);
        Assert.Equal("{ \"myLanguage\": \"fr\", ", File.ReadAllText(store.BrokenPath));
    }

    [Fact]
    public void Corrupt_without_backup_resets()
    {
        File.WriteAllText(SettingsPath, "[1, 2]"); // valid JSON, wrong shape
        var result = Store().LoadWithReport();
        Assert.Equal(SettingsLoadStatus.ResetCorrupt, result.Status);
        Assert.Equal("en", result.Settings.MyLanguage);
        Assert.True(File.Exists(SettingsPath + ".broken"));
        Assert.Contains(_sink.Entries, e => e.Message == "Settings reset to defaults");

        File.WriteAllText(SettingsPath + ".bak", "also broken");
        Assert.Equal(SettingsLoadStatus.ResetCorrupt, Store().LoadWithReport().Status);
    }

    [Fact]
    public void Successful_load_refreshes_last_known_good()
    {
        var store = Store();
        store.Save(new AppSettings { MyLanguage = "ja" });
        var text = File.ReadAllText(SettingsPath);
        Assert.False(File.Exists(store.LastKnownGoodPath));

        store.LoadWithReport();

        Assert.Equal(text, File.ReadAllText(store.LastKnownGoodPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Unreadable_file_is_treated_as_corrupt()
    {
        Directory.CreateDirectory(SettingsPath); // a directory where the file should be: reading throws
        var store = Store();
        var result = store.LoadWithReport();
        Assert.Equal(SettingsLoadStatus.Defaults, result.Status); // File.Exists is false for a directory

        Directory.Delete(SettingsPath);
        File.WriteAllBytes(SettingsPath, [0xFF, 0xFE, 0x00, 0xD8]); // not UTF-8 JSON
        Assert.Equal(SettingsLoadStatus.ResetCorrupt, Store().LoadWithReport().Status);
    }

    [Fact]
    public void Load_status_is_reported()
    {
        var store = Store();
        var fresh = store.LoadWithReport();
        Assert.Equal(SettingsLoadStatus.Defaults, fresh.Status);
        Assert.Equal(AppSettings.CurrentVersion, fresh.FileVersion);
        Assert.Null(fresh.Error);

        store.Save(new AppSettings());
        var loaded = store.LoadWithReport();
        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(1, loaded.FileVersion);
        Assert.Null(loaded.BackupPath);
        Assert.Equal("en", store.Load().MyLanguage); // Load() still works for existing callers
    }

    [Fact]
    public void Suppressed_store_never_writes()
    {
        var store = Store();
        store.Save(new AppSettings { MyLanguage = "ko" });
        var before = File.ReadAllText(SettingsPath);

        store.SuppressSaves();
        Assert.True(store.SavesSuppressed);
        store.Save(new AppSettings { MyLanguage = "ru" });
        Assert.Equal(before, File.ReadAllText(SettingsPath));

        File.Delete(SettingsPath); // "Delete all Envoy data"
        store.Save(new AppSettings());
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Reset_then_save_leaves_defaults()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 18, 3, 12, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var store = Store(clock);
        var settings = new AppSettings { MyLanguage = "zh-CN" };
        store.Save(settings);

        var backup = store.ResetToDefaults();

        Assert.Equal(SettingsPath + ".reset-20261014-180312.bak", backup);
        Assert.Contains("zh-CN", File.ReadAllText(backup!), StringComparison.Ordinal);
        store.Save(settings); // the exit save, MainWindow.OnClosed and Host.Dispose all do this
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal("en", Store().Load().MyLanguage);
        Assert.Null(Store().ResetToDefaults());
    }

    [Fact]
    public void Null_sections_are_repaired()
    {
        File.WriteAllText(SettingsPath, """{ "version": 1, "myLanguage": "de", "capture": null, "translation": { "fallbacks": null, "deepLKey": null }, "mutedPlayers": null, "myPlayerName": null }""");
        var result = Store().LoadWithReport();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        var s = result.Settings;
        Assert.Equal("de", s.MyLanguage);
        Assert.NotNull(s.Capture);
        Assert.NotNull(s.Capture.Regions);
        Assert.Equal(new TranslationSettings().Fallbacks, s.Translation.Fallbacks);
        Assert.Equal("", s.Translation.DeepLKey);
        Assert.Empty(s.MutedPlayers);
        Assert.Equal("", s.MyPlayerName);
        Assert.Null(s.Usage);
    }

    [Fact]
    public void Undecryptable_secret_is_logged_by_name()
    {
        File.WriteAllText(SettingsPath, """{ "version": 1, "translation": { "claudeKey": "copied-from-another-pc-SECRETVALUE" } }""");
        var result = Store().LoadWithReport();
        Assert.Equal("", result.Settings.Translation.ClaudeKey);
        var warning = Assert.Single(_sink.Entries, e => e.Message.Contains("could not be decrypted", StringComparison.Ordinal));
        Assert.Equal("property=ClaudeKey", warning.Fields);
        Assert.DoesNotContain(_sink.Entries, e => (e.Message + e.Fields + e.Exception).Contains("SECRETVALUE", StringComparison.Ordinal));
    }
}
