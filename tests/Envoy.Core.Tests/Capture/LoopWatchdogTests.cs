using Envoy.Core.Capture;
using Envoy.Core.Tests.Translation.Fakes;
using Xunit;

namespace Envoy.Core.Tests.Capture;

public class LoopWatchdogTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(15);

    [Fact]
    public void Healthy_while_heartbeats_arrive()
    {
        var clock = TestClock.Create();
        var dog = new LoopWatchdog(clock, Stall);
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            dog.Heartbeat();
            Assert.Equal(WatchdogVerdict.Healthy, dog.Check());
        }
    }

    [Fact]
    public void Stalled_after_threshold()
    {
        var clock = TestClock.Create();
        var dog = new LoopWatchdog(clock, Stall);
        clock.Advance(TimeSpan.FromSeconds(14.9));
        Assert.Equal(WatchdogVerdict.Healthy, dog.Check());
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(WatchdogVerdict.Stalled, dog.Check());
        dog.RecordRestart();
        Assert.Equal(WatchdogVerdict.Healthy, dog.Check()); // a restart is a heartbeat
        Assert.Equal(1, dog.Restarts);
    }

    [Fact]
    public void Gives_up_after_more_than_three_restarts_in_five_minutes()
    {
        var clock = TestClock.Create();
        var dog = new LoopWatchdog(clock, Stall);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal(WatchdogVerdict.Stalled, dog.Check());
            dog.RecordRestart();
        }
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(WatchdogVerdict.GiveUp, dog.Check());
    }

    [Fact]
    public void Restart_budget_recovers_after_window_and_ResetBudget()
    {
        var clock = TestClock.Create();
        var dog = new LoopWatchdog(clock, Stall);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            dog.RecordRestart();
        }
        clock.Advance(TimeSpan.FromMinutes(5)); // the oldest restarts leave the window
        Assert.Equal(WatchdogVerdict.Stalled, dog.Check());
        Assert.Equal(0, dog.Restarts);

        for (var i = 0; i < 3; i++) dog.RecordRestart();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(WatchdogVerdict.GiveUp, dog.Check());
        dog.ResetBudget();
        Assert.Equal(WatchdogVerdict.Healthy, dog.Check());
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(WatchdogVerdict.Stalled, dog.Check());
    }

    [Theory]
    [InlineData(4000, 1500, 15)]
    [InlineData(1000, 300, 15)]
    [InlineData(10000, 1500, 21.5)]
    [InlineData(30000, 5000, 65)]
    public void StallThreshold_is_max_of_15s_and_formula(int ocrMs, int maxIntervalMs, double expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), LoopWatchdog.StallThreshold(ocrMs, maxIntervalMs));

    [Fact]
    public void StallAfter_can_change_while_running()
    {
        var clock = TestClock.Create();
        var dog = new LoopWatchdog(clock, Stall);
        clock.Advance(TimeSpan.FromSeconds(16));
        dog.StallAfter = TimeSpan.FromSeconds(30);
        Assert.Equal(WatchdogVerdict.Healthy, dog.Check());
    }
}
