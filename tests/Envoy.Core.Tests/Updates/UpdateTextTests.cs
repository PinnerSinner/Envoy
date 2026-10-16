using System.Globalization;
using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class UpdateTextTests
{
    private static readonly ReleaseVersion Next = ReleaseVersion.Parse("1.3.0-beta.2");
    private static readonly ReleaseVersion Current = ReleaseVersion.Parse("1.2.0");

    [Fact]
    public void Failure_sentences_have_no_error_prefix_or_exception_text()
    {
        foreach (var kind in Enum.GetValues<UpdateErrorKind>())
        {
            foreach (var sentence in new[] { UpdateText.CheckFailed(kind), UpdateText.DownloadFailed(kind) })
            {
                Assert.False(string.IsNullOrWhiteSpace(sentence), kind.ToString());
                Assert.DoesNotContain("Error", sentence, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Exception", sentence, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(kind.ToString(), sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("{", sentence, StringComparison.Ordinal);
                Assert.EndsWith(".", sentence);
                Assert.True(char.IsUpper(sentence[0]));
            }
        }
        Assert.NotEqual(UpdateText.CheckFailed(UpdateErrorKind.Offline), UpdateText.CheckFailed(UpdateErrorKind.RateLimited));
    }

    [Fact]
    public void Headlines_contain_versions()
    {
        Assert.Equal("Envoy 1.3.0-beta.2 is available. You have 1.2.0.", UpdateText.Available(Next, Current));
        Assert.Contains("1.3.0-beta.2", UpdateText.AvailableAfterGame(Next), StringComparison.Ordinal);
        Assert.Equal("Downloading Envoy 1.3.0-beta.2… 42%", UpdateText.Downloading(Next, 42));
        Assert.StartsWith("Envoy 1.3.0-beta.2 is ready.", UpdateText.Ready(Next), StringComparison.Ordinal);
        Assert.Equal("Envoy was updated to 1.3.0-beta.2.", UpdateText.UpdatedTo(Next));
        Assert.Equal("Restart to update to 1.3.0-beta.2", UpdateText.TrayRestartItem(Next));
        foreach (var action in Enum.GetValues<UpdateAction>().Where(a => a != UpdateAction.None))
            Assert.False(string.IsNullOrEmpty(UpdateText.ActionLabel(action)), action.ToString());
        Assert.Equal("", UpdateText.ActionLabel(UpdateAction.None));
    }

    [Fact]
    public void Status_up_to_date_includes_local_check_time()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");
        var at = new DateTimeOffset(2026, 10, 14, 12, 2, 0, TimeSpan.Zero);
        var s = Snapshot(UpdateState.UpToDate, Current, at);
        var expected = new DateTime(2026, 10, 14, 14, 2, 0).ToString("t", CultureInfo.CurrentCulture);
        Assert.Equal("Up to date · checked " + expected, UpdateText.Status(s, zone));
        Assert.Equal(UpdateText.Checking, UpdateText.Status(s with { State = UpdateState.Checking }, zone));
        Assert.Equal(UpdateText.NeverChecked, UpdateText.Status(s with { State = UpdateState.Idle, LastChecked = null }, zone));
    }

    [Fact]
    public void Status_beta_ahead_of_stable_explains_no_downgrade()
    {
        var s = Snapshot(UpdateState.UpToDate, ReleaseVersion.Parse("1.3.0-beta.1"), DateTimeOffset.UnixEpoch);
        Assert.StartsWith("On a beta newer than the latest stable release", UpdateText.Status(s, TimeZoneInfo.Utc), StringComparison.Ordinal);
        Assert.StartsWith("Up to date", UpdateText.Status(s with { Channel = UpdateChannel.Beta }, TimeZoneInfo.Utc), StringComparison.Ordinal);
    }

    private static UpdateSnapshot Snapshot(UpdateState state, ReleaseVersion current, DateTimeOffset? checkedAt) =>
        new(InstallKind.Installed, current, state, null, 0, false, null, false, false, checkedAt, UpdateChannel.Stable);
}
