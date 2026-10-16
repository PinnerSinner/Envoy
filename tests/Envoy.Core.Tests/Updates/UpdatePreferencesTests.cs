using Envoy.Core.Settings;
using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class UpdatePreferencesTests
{
    [Fact]
    public void Snapshot_is_unaffected_by_later_settings_changes()
    {
        var settings = new AppSettings();
        settings.Updates.Channel = UpdateChannel.Beta;
        settings.Updates.SkippedVersion = "1.2.0";
        settings.Updates.LastCheckUtc = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var prefs = UpdatePreferences.From(settings);

        settings.CheckForUpdates = false;
        settings.Updates.AutoDownload = false;
        settings.Updates.Channel = UpdateChannel.Stable;
        settings.Updates.SkippedVersion = "";
        settings.Updates.LastCheckUtc = null;

        Assert.True(prefs.CheckAutomatically);
        Assert.True(prefs.AutoDownload);
        Assert.Equal(UpdateChannel.Beta, prefs.Channel);
        Assert.Equal(ReleaseVersion.Parse("1.2.0"), prefs.SkippedVersion);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), prefs.LastCheckUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a version")]
    public void Empty_skipped_version_is_null(string skipped)
    {
        var settings = new AppSettings();
        settings.Updates.SkippedVersion = skipped;
        Assert.Null(UpdatePreferences.From(settings).SkippedVersion);
    }
}
