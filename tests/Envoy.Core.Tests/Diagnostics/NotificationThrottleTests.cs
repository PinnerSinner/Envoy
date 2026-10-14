using Envoy.Core.Notifications;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests.Diagnostics;

public class NotificationThrottleTests
{
    [Fact]
    public void Toast_same_key_window()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var throttle = new NotificationThrottle(clock);
        Assert.True(throttle.TryShowToast("capture:error"));
        Assert.False(throttle.TryShowToast("capture:error"));
        clock.Advance(NotificationThrottle.SameKeyWindow - TimeSpan.FromSeconds(1));
        Assert.False(throttle.TryShowToast("capture:error"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryShowToast("capture:error"));
        Assert.False(throttle.TryShowToast("capture:error"));
    }

    [Fact]
    public void Different_keys_are_independent()
    {
        var throttle = new NotificationThrottle(new FakeTimeProvider());
        Assert.True(throttle.TryShowToast("a"));
        Assert.True(throttle.TryShowToast("b"));
        Assert.True(throttle.TryShowToast("c"));
        Assert.True(throttle.TryShowToast("d")); // the visible cap is the ToastService's job
        Assert.False(throttle.TryShowToast("a"));
        Assert.Equal(3, NotificationThrottle.MaxVisibleToasts);
    }

    [Fact]
    public void Balloon_spacing_and_session_cap()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var throttle = new NotificationThrottle(clock);

        Assert.True(throttle.TryShowBalloon("n1"));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(throttle.TryShowBalloon("n2")); // < 60 s since the last balloon
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryShowBalloon("n2"));

        for (var i = 3; i <= 5; i++)
        {
            clock.Advance(NotificationThrottle.AdvisoryBalloonSpacing);
            Assert.True(throttle.TryShowBalloon("n" + i));
        }
        Assert.Equal(NotificationThrottle.MaxAdvisoryBalloonsPerSession, throttle.BalloonsShown);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(throttle.TryShowBalloon("n6")); // session cap

        // A balloon key shares the same-key window with toasts.
        var other = new NotificationThrottle(clock);
        Assert.True(other.TryShowToast("same"));
        Assert.False(other.TryShowBalloon("same"));
    }
}
