using Xunit;
using Microsoft.Extensions.Time.Testing;

namespace Envoy.Core.Tests.Translation.Fakes;

internal static class TestClock
{
    public static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Create()
    {
        var clock = new FakeTimeProvider(Start);
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }

    /// <summary>Advances fake time in <paramref name="step"/>s, yielding real time between steps, until <paramref name="task"/> ends.</summary>
    public static async Task RunUntil(this FakeTimeProvider clock, Task task, TimeSpan step, int maxSteps = 2000)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            await Task.Delay(1);
            if (task.IsCompleted) break;
            clock.Advance(step);
        }
        if (!task.IsCompleted) await Task.WhenAny(task, Task.Delay(2000));
        Assert.True(task.IsCompleted, "the task did not finish in fake time");
    }

    /// <summary>Lets queued continuations run (real time, short).</summary>
    public static async Task Settle(int ms = 20) => await Task.Delay(ms);
}
