using Envoy.Core.Capture;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Capture;

public class AdaptivePollPolicyTests
{
    private static AdaptivePollPolicy Policy() => new(PollingOptions.From(new CaptureSettings()));

    private static double Ms(TimeSpan t) => t.TotalMilliseconds;

    [Fact]
    public void Changed_frame_drops_to_min()
    {
        var p = Policy();
        for (var i = 0; i < 20; i++) p.Next(FrameOutcome.Unchanged);
        Assert.Equal(1500, Ms(p.Current));
        Assert.Equal(300, Ms(p.Next(FrameOutcome.Changed)));
        Assert.Equal(300, Ms(p.Current));
    }

    [Fact]
    public void Stays_at_min_for_fast_frames_after_change()
    {
        var p = Policy();
        p.Next(FrameOutcome.Changed);
        Assert.Equal(300, Ms(p.Next(FrameOutcome.Unchanged)));
        Assert.Equal(300, Ms(p.Next(FrameOutcome.Unchanged)));
        Assert.Equal(300, Ms(p.Next(FrameOutcome.Unchanged)));
        Assert.Equal(450, Ms(p.Next(FrameOutcome.Unchanged)));
    }

    [Fact]
    public void Unchanged_frames_back_off_by_factor_to_max()
    {
        var p = Policy();
        p.Next(FrameOutcome.Changed);
        for (var i = 0; i < 3; i++) p.Next(FrameOutcome.Unchanged);
        var expected = new[] { 450, 675, 1012.5, 1500, 1500 };
        foreach (var e in expected) Assert.Equal(e, Ms(p.Next(FrameOutcome.Unchanged)), 3);
    }

    [Fact]
    public void Failures_double_to_error_cap_and_success_recovers()
    {
        var p = Policy();
        p.Next(FrameOutcome.Changed);
        for (var i = 0; i < 5; i++) p.Next(FrameOutcome.Unchanged); // 450, 675
        var before = p.Current;
        var waits = Enumerable.Range(0, 7).Select(_ => Ms(p.Next(FrameOutcome.Failed))).ToList();
        Assert.Equal([600, 1200, 2400, 4800, 5000, 5000, 5000], waits);
        Assert.Equal(7, p.ConsecutiveFailures);
        Assert.Equal(before, p.Current); // failures don't disturb the adaptive interval
        Assert.Equal(Ms(before) * 1.5, Ms(p.Next(FrameOutcome.Unchanged)), 3);
        Assert.Equal(0, p.ConsecutiveFailures);
        Assert.Equal(600, Ms(p.Next(FrameOutcome.Failed))); // the doubling starts again from Min
        Assert.Equal(300, Ms(p.Next(FrameOutcome.Changed)));
    }

    [Fact]
    public void Paused_uses_paused_interval_and_preserves_current()
    {
        var p = Policy();
        for (var i = 0; i < 6; i++) p.Next(FrameOutcome.Unchanged);
        var current = p.Current;
        Assert.Equal(1000, Ms(p.Next(FrameOutcome.Paused)));
        Assert.Equal(current, p.Current);
    }

    [Fact]
    public void Disabled_adaptive_uses_fixed_interval()
    {
        var p = new AdaptivePollPolicy(PollingOptions.From(new CaptureSettings { AdaptivePolling = false, IntervalMs = 700 }));
        Assert.Equal(700, Ms(p.Next(FrameOutcome.Changed)));
        for (var i = 0; i < 10; i++) Assert.Equal(700, Ms(p.Next(FrameOutcome.Unchanged)));
        Assert.Equal(1400, Ms(p.Next(FrameOutcome.Failed)));
    }

    [Theory]
    [InlineData(true, 10, 99999, 700, 150, 5000)]
    [InlineData(true, 5000, 100, 700, 2000, 2000)]
    [InlineData(true, 300, 1500, 700, 300, 1500)]
    [InlineData(false, 300, 1500, 50, 150, 150)]
    [InlineData(false, 300, 1500, 90000, 5000, 5000)]
    [InlineData(false, 300, 1500, 700, 700, 700)]
    public void Options_clamp_out_of_range_settings(bool adaptive, int min, int max, int fixedMs, int expectedMin, int expectedMax)
    {
        var o = PollingOptions.From(new CaptureSettings { AdaptivePolling = adaptive, MinIntervalMs = min, MaxIntervalMs = max, IntervalMs = fixedMs });
        Assert.Equal(expectedMin, o.MinIntervalMs);
        Assert.Equal(expectedMax, o.MaxIntervalMs);
    }

    [Theory]
    [InlineData(600, 300, 600)]
    [InlineData(0, 300, 300)]
    [InlineData(290, 300, 290)]
    [InlineData(10, 30, 50)]
    public void DelayAfter_respects_duty_cycle_and_floor(int workMs, int intervalMs, int expectedMs)
    {
        var p = Policy();
        Assert.Equal(expectedMs, Ms(p.DelayAfter(TimeSpan.FromMilliseconds(intervalMs), TimeSpan.FromMilliseconds(workMs))));
    }

    [Fact]
    public void Reset_returns_to_min()
    {
        var p = Policy();
        for (var i = 0; i < 10; i++) p.Next(FrameOutcome.Unchanged);
        p.Next(FrameOutcome.Failed);
        p.Reset();
        Assert.Equal(300, Ms(p.Current));
        Assert.Equal(0, p.ConsecutiveFailures);
        Assert.Equal(300, Ms(p.Next(FrameOutcome.Unchanged))); // fast frames refilled
    }

    [Fact]
    public void Swapping_options_reclamps_current()
    {
        var p = Policy();
        for (var i = 0; i < 10; i++) p.Next(FrameOutcome.Unchanged);
        Assert.Equal(1500, Ms(p.Current));
        p.Options = PollingOptions.From(new CaptureSettings { MaxIntervalMs = 800 });
        Assert.Equal(800, Ms(p.Current));
    }
}
