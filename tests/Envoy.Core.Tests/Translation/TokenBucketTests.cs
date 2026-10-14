using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class TokenBucketTests
{
    [Fact]
    public void Burst_then_refills_at_rate()
    {
        var clock = TestClock.Create();
        var bucket = new TokenBucket(2, 6, clock);
        for (var i = 0; i < 6; i++) Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
        clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 6; i++) Assert.True(bucket.TryTake()); // never more than the burst
        Assert.False(bucket.TryTake());
    }

    [Fact]
    public async Task TakeAsync_gives_up_after_max_wait()
    {
        var clock = TestClock.Create();
        var bucket = new TokenBucket(0.5, 1, clock);
        Assert.True(bucket.TryTake());
        // The next token is 2 s away: more than the 1.5 s allowed, so no wait at all.
        Assert.False(await bucket.TakeAsync(TimeSpan.FromMilliseconds(1500), CancellationToken.None));

        var waiting = bucket.TakeAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await waiting);
    }

    [Fact]
    public async Task TakeAsync_honours_cancellation()
    {
        var clock = TestClock.Create();
        var bucket = new TokenBucket(1, 1, clock);
        Assert.True(bucket.TryTake());
        using var cts = new CancellationTokenSource();
        var waiting = bucket.TakeAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(bucket.TryTake()); // the cancelled waiter gave its token back
    }

    [Fact]
    public async Task Waiters_are_served_in_order_at_the_rate()
    {
        var clock = TestClock.Create();
        var bucket = new TokenBucket(2, 6, clock);
        var takes = Enumerable.Range(0, 8).Select(_ => bucket.TakeAsync(TimeSpan.FromMilliseconds(1500), CancellationToken.None)).ToList();
        Assert.Equal(6, takes.Count(t => t.IsCompleted));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Task.WhenAny(takes[6], Task.Delay(1000));
        Assert.True(takes[6].IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(await takes[7]);
    }

    [Fact]
    public void Zero_rate_is_unlimited()
    {
        var bucket = new TokenBucket(0, 0, TestClock.Create());
        for (var i = 0; i < 1000; i++) Assert.True(bucket.TryTake());
    }
}
