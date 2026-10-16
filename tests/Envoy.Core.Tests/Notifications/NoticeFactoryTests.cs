using Envoy.Core.Notifications;
using Envoy.Core.Translation.Resilience;
using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Notifications;

public class NoticeFactoryTests
{
    private static readonly ReleaseVersion Current = ReleaseVersion.Parse("1.0.0");
    private static readonly ReleaseVersion Next = ReleaseVersion.Parse("1.1.0");

    private static UpdateSnapshot Snapshot(UpdateState state, InstallKind kind = InstallKind.Installed) =>
        new(kind, Current, state, new AvailableUpdate(Next, null, UpdateEndpoints.ReleasePage(Next), null, null), 0, false, null, false, false, null,
            UpdateChannel.Stable);

    [Fact]
    public void Provider_notice_has_no_error_text()
    {
        var health = ProviderHealthSnapshot.Empty("deepl") with { State = ProviderState.AuthFailed, LastError = "secret-key-123 boom" };
        var notice = NoticeFactory.ProviderFailing(health.ProviderId, "DeepL", health.State)!;
        Assert.DoesNotContain("secret-key-123", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", notice.Title, StringComparison.Ordinal);
        Assert.StartsWith("DeepL rejected your key.", notice.Text, StringComparison.Ordinal);
        Assert.Equal("provider:deepl", notice.Key);
        Assert.Equal(NoticeActions.OpenTranslation, notice.ActionId);
        Assert.True(notice.IsWarning);
    }

    [Theory]
    [InlineData(ProviderState.AuthFailed, "rejected your key")]
    [InlineData(ProviderState.QuotaExceeded, "reached its limit")]
    [InlineData(ProviderState.CoolingDown, "keeps failing")]
    public void Provider_text_per_state(ProviderState state, string fragment) =>
        Assert.Contains(fragment, NoticeFactory.ProviderFailing("deepl", "DeepL", state)!.Text, StringComparison.Ordinal);

    [Fact]
    public void Ocr_notice_lists_language_names_not_codes()
    {
        var notice = NoticeFactory.OcrMissing(["ja", "ko"])!;
        Assert.Contains("Japanese, Korean", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ja,", notice.Text, StringComparison.Ordinal);
        Assert.Equal(NoticeText.OcrMissingTitle, notice.Title);
        Assert.Equal(NoticeActions.OpenCapture, notice.ActionId);
        Assert.Equal(NoticeKind.OcrLanguageMissing, notice.Kind);
    }

    [Fact]
    public void Ocr_key_is_order_independent()
    {
        Assert.Equal("ocr-missing:ja,ko,zh-CN", NoticeFactory.OcrMissing(["zh-CN", "ko", "ja"])!.Key);
        Assert.Equal(NoticeFactory.OcrMissing(["ko", "ja"])!.Key, NoticeFactory.OcrMissing(["ja", "ko", "ja"])!.Key);
    }

    [Fact]
    public void Returns_null_for_non_notice_states()
    {
        Assert.Null(NoticeFactory.OcrMissing([]));
        foreach (var state in new[] { ProviderState.Unknown, ProviderState.Healthy, ProviderState.Degraded, ProviderState.Throttled })
            Assert.Null(NoticeFactory.ProviderFailing("deepl", "DeepL", state));
    }

    [Fact]
    public void Update_uses_headline_and_primary_action()
    {
        var snapshot = Snapshot(UpdateState.Available);
        var view = UpdateViewBuilder.Build(snapshot, zone: TimeZoneInfo.Utc);
        var notice = NoticeFactory.UpdateAvailable(snapshot, view)!;
        Assert.Equal(view.Headline, notice.Text);
        Assert.Equal(NoticeText.UpdateTitle, notice.Title);
        Assert.Equal("update:1.1.0", notice.Key);
        Assert.Equal(NoticeActions.UpdatePrimary, notice.ActionId);
        Assert.Equal(NoticeKind.UpdateAvailable, notice.Kind);
    }

    [Theory]
    [InlineData(UpdateState.Idle, false)]
    [InlineData(UpdateState.Checking, false)]
    [InlineData(UpdateState.UpToDate, false)]
    [InlineData(UpdateState.Available, true)]
    [InlineData(UpdateState.Downloading, false)]
    [InlineData(UpdateState.ReadyToRestart, true)]
    [InlineData(UpdateState.Failed, false)]
    public void Update_only_for_available_or_ready(UpdateState state, bool expected)
    {
        var snapshot = Snapshot(state);
        Assert.Equal(expected, NoticeFactory.UpdateAvailable(snapshot, UpdateViewBuilder.Build(snapshot, zone: TimeZoneInfo.Utc)) is not null);
    }

    [Fact]
    public void Update_needs_a_version()
    {
        var snapshot = Snapshot(UpdateState.Available) with { Available = null };
        Assert.Null(NoticeFactory.UpdateAvailable(snapshot, UpdateViewBuilder.Build(Snapshot(UpdateState.Available), zone: TimeZoneInfo.Utc)));
    }
}
