using Envoy.Core.Notifications;
using Envoy.Core.Settings;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests.Notifications;

public class NoticeCenterTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly AppSettings _settings = new();
    private readonly NotificationThrottle _throttle;
    private readonly NoticeCenter _center;

    public NoticeCenterTests()
    {
        _throttle = new NotificationThrottle(_clock);
        _center = new NoticeCenter(_settings, _throttle);
    }

    private static Notice Ocr(params string[] codes) => NoticeFactory.OcrMissing(codes)!;

    private static Notice Provider(string id, string text = "x") =>
        new(NoticeKind.ProviderFailing, NoticeFactory.ProviderKey(id), NoticeText.ProviderTitle, text, NoticeActions.OpenTranslation, true);

    private static Notice Update(string version) =>
        new(NoticeKind.UpdateAvailable, NoticeFactory.UpdateKeyPrefix + version, NoticeText.UpdateTitle, "Envoy " + version + " is available.", NoticeActions.UpdatePrimary, false);

    /// <summary>Past the throttle's balloon spacing and same-key window.</summary>
    private void Later() => _clock.Advance(NotificationThrottle.SameKeyWindow + TimeSpan.FromSeconds(1));

    [Fact]
    public void Shows_first_notice()
    {
        var changes = 0;
        _center.Changed += () => changes++;
        Assert.Equal(NoticeDecision.Show, _center.Submit(Ocr("ja"), gameForeground: false));
        Assert.Single(_center.Active);
        Assert.Equal(1, changes);
        Assert.Equal(1, _throttle.BalloonsShown);
        Assert.Empty(_center.Deferred);
    }

    [Fact]
    public void Defers_while_game_foreground_and_releases_after()
    {
        Assert.Equal(NoticeDecision.Defer, _center.Submit(Ocr("ja"), gameForeground: true));
        Assert.Single(_center.Active); // listed at once
        Assert.Equal(0, _throttle.BalloonsShown); // the throttle is not consumed while deferred
        Assert.Null(_center.TakeDeferred(gameForeground: true));
        Assert.Single(_center.Deferred);
        var released = _center.TakeDeferred(gameForeground: false);
        Assert.Equal(Ocr("ja"), released);
        Assert.Empty(_center.Deferred);
        Assert.Null(_center.TakeDeferred(gameForeground: false));
    }

    [Fact]
    public void Hold_defers_everything()
    {
        _center.Hold(true);
        Assert.True(_center.IsHeld);
        Assert.Equal(NoticeDecision.Defer, _center.Submit(Ocr("ja"), false));
        Assert.Equal(NoticeDecision.Defer, _center.Submit(Provider("deepl"), false));
        Assert.Equal(NoticeDecision.Defer, _center.Submit(Update("9.0.0"), false));
        Assert.Null(_center.TakeDeferred(false));
        Assert.Equal(3, _center.Deferred.Count);
        _center.Hold(false);
        Assert.NotNull(_center.TakeDeferred(false));
    }

    [Fact]
    public void Throttle_refusal_defers_not_drops()
    {
        Assert.Equal(NoticeDecision.Show, _center.Submit(Ocr("ja"), false));
        // A second balloon within the 60 s spacing is refused by the throttle: deferred, not dropped.
        Assert.Equal(NoticeDecision.Defer, _center.Submit(Provider("deepl"), false));
        Assert.Null(_center.TakeDeferred(false));
        _clock.Advance(NotificationThrottle.AdvisoryBalloonSpacing);
        Assert.Equal(NoticeFactory.ProviderKey("deepl"), _center.TakeDeferred(false)?.Key);
        Assert.Equal(2, _throttle.BalloonsShown);
    }

    [Fact]
    public void Latest_wins_per_key_in_queue()
    {
        _center.Submit(Provider("deepl", "old"), gameForeground: true);
        _center.Submit(Ocr("ko"), gameForeground: true);
        _center.Submit(Provider("deepl", "new"), gameForeground: true);
        Assert.Equal(2, _center.Deferred.Count);
        Assert.Equal("new", _center.Deferred[0].Text); // replaced in place, FIFO position kept
        Assert.Equal("new", _center.Active.Single(n => n.Key == "provider:deepl").Text);
        Assert.Equal("new", _center.TakeDeferred(false)?.Text);
    }

    [Fact]
    public void Announced_key_not_reballooned_until_resolved()
    {
        Assert.Equal(NoticeDecision.Show, _center.Submit(Provider("deepl"), false));
        Later();
        Assert.Equal(NoticeDecision.Suppress, _center.Submit(Provider("deepl"), false));
        Assert.Equal(NoticeDecision.Suppress, _center.Submit(Provider("deepl", "other text"), false));
        Assert.Single(_center.Active);
    }

    [Fact]
    public void Resolve_rearms()
    {
        var changes = 0;
        _center.Changed += () => changes++;
        Assert.Equal(NoticeDecision.Show, _center.Submit(Provider("deepl"), false));
        _center.Resolve("provider:deepl");
        Assert.Empty(_center.Active);
        Assert.Equal(2, changes);
        Later();
        Assert.Equal(NoticeDecision.Show, _center.Submit(Provider("deepl"), false));
    }

    [Fact]
    public void Resolve_drops_a_queued_notice()
    {
        _center.Submit(Ocr("ja"), gameForeground: true);
        _center.ResolveWhere(k => k.StartsWith(NoticeFactory.OcrKeyPrefix, StringComparison.Ordinal));
        Assert.Empty(_center.Deferred);
        Assert.Empty(_center.Active);
        Assert.Null(_center.TakeDeferred(false));
    }

    [Fact]
    public void Master_switch_off_keeps_active_and_suppresses_balloon()
    {
        _settings.Notifications.Enabled = false;
        Assert.Equal(NoticeDecision.Suppress, _center.Submit(Ocr("ja"), false));
        Assert.Single(_center.Active);
        Assert.Equal(0, _throttle.BalloonsShown);
    }

    [Fact]
    public void Switching_off_while_queued_drops_the_balloon()
    {
        _center.Submit(Ocr("ja"), gameForeground: true);
        _settings.Notifications.Enabled = false;
        Assert.Null(_center.TakeDeferred(false));
        Assert.Empty(_center.Deferred);
        Assert.Single(_center.Active);
    }

    [Theory]
    [InlineData(NoticeKind.OcrLanguageMissing)]
    [InlineData(NoticeKind.ProviderFailing)]
    [InlineData(NoticeKind.UpdateAvailable)]
    public void Disabled_kind_suppresses_balloon_but_keeps_active(NoticeKind kind)
    {
        var n = _settings.Notifications;
        n.OcrLanguageMissing = kind != NoticeKind.OcrLanguageMissing;
        n.ProviderFailing = kind != NoticeKind.ProviderFailing;
        n.UpdateAvailable = kind != NoticeKind.UpdateAvailable;
        var notice = kind switch
        {
            NoticeKind.OcrLanguageMissing => Ocr("ja"),
            NoticeKind.ProviderFailing => Provider("deepl"),
            _ => Update("9.0.0"),
        };
        Assert.Equal(NoticeDecision.Suppress, _center.Submit(notice, false));
        Assert.Equal(kind != NoticeKind.UpdateAvailable, _center.Active.Any(a => a.Key == notice.Key));
        // The other kinds still balloon.
        var other = kind == NoticeKind.OcrLanguageMissing ? Provider("deepl") : Ocr("ja");
        Assert.Equal(NoticeDecision.Show, _center.Submit(other, false));
    }

    [Fact]
    public void Dismissed_key_never_shows_or_lists()
    {
        _center.Submit(Ocr("ja"), false);
        _center.Dismiss("ocr-missing:ja");
        _center.Dismiss("ocr-missing:ja");
        Assert.Equal(["ocr-missing:ja"], _settings.Notifications.Dismissed);
        Assert.Empty(_center.Active);
        Later();
        Assert.Equal(NoticeDecision.Suppress, _center.Submit(Ocr("ja"), false));
        Assert.Empty(_center.Active);
        // A different key (another language missing too) is a new notice.
        Assert.Equal(NoticeDecision.Show, _center.Submit(Ocr("ja", "ko"), false));
    }

    [Fact]
    public void Update_not_listed_in_active()
    {
        Assert.Equal(NoticeDecision.Show, _center.Submit(Update("9.0.0"), false));
        Assert.Empty(_center.Active);
        Assert.Equal("9.0.0", _settings.Updates.LastAnnouncedVersion);
    }

    [Fact]
    public void Update_version_announced_once_across_instances()
    {
        Assert.Equal(NoticeDecision.Show, _center.Submit(Update("9.0.0"), false));
        // A new session (a new center and throttle over the saved settings) never balloons the same version again.
        var next = new NoticeCenter(_settings, new NotificationThrottle(_clock));
        Assert.Equal(NoticeDecision.Suppress, next.Submit(Update("9.0.0"), false));
        Assert.Equal(NoticeDecision.Show, next.Submit(Update("9.1.0"), false));
        Assert.Equal("9.1.0", _settings.Updates.LastAnnouncedVersion);
    }

    [Fact]
    public void Deferred_update_is_suppressed_once_announced_elsewhere()
    {
        _center.Submit(Update("9.0.0"), gameForeground: true);
        _settings.Updates.LastAnnouncedVersion = "9.0.0";
        Assert.Null(_center.TakeDeferred(false));
        Assert.Empty(_center.Deferred);
    }
}
