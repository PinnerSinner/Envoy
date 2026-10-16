using System.Text.Json;
using Envoy.Core.Settings;
using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Settings;

public class UpdateSettingsTests
{
    [Fact]
    public void Update_settings_default_and_old_files_load()
    {
        var fresh = new AppSettings();
        Assert.True(fresh.CheckForUpdates);
        Assert.Equal(UpdateChannel.Stable, fresh.Updates.Channel);
        Assert.True(fresh.Updates.AutoDownload);
        Assert.Equal("", fresh.Updates.SkippedVersion);
        Assert.Null(fresh.Updates.LastCheckUtc);
        Assert.Equal("", fresh.Updates.LastRunVersion);

        // A settings file from before spec 06 has no "updates" object.
        var old = JsonSerializer.Deserialize<AppSettings>("""{ "version": 1, "checkForUpdates": false, "myLanguage": "fr" }""", SettingsStore.JsonOptions)!;
        Assert.False(old.CheckForUpdates);
        Assert.NotNull(old.Updates);
        Assert.Equal(UpdateChannel.Stable, old.Updates.Channel);
        Assert.True(old.Updates.AutoDownload);
        Assert.Equal(1, AppSettings.CurrentVersion);
    }

    [Fact]
    public void Update_settings_round_trip()
    {
        var settings = new AppSettings();
        settings.Updates.Channel = UpdateChannel.Beta;
        settings.Updates.AutoDownload = false;
        settings.Updates.SkippedVersion = "1.2.0-beta.1";
        settings.Updates.LastCheckUtc = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        settings.Updates.LastRunVersion = "1.1.0";
        var json = JsonSerializer.Serialize(settings, SettingsStore.JsonOptions);
        Assert.Contains("\"Beta\"", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
        Assert.Equal(UpdateChannel.Beta, back.Updates.Channel);
        Assert.False(back.Updates.AutoDownload);
        Assert.Equal("1.2.0-beta.1", back.Updates.SkippedVersion);
        Assert.Equal(settings.Updates.LastCheckUtc, back.Updates.LastCheckUtc);
        Assert.Equal("1.1.0", back.Updates.LastRunVersion);
    }
}
