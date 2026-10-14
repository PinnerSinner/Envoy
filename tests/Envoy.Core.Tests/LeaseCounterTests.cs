using Envoy.Core.Threading;
using Xunit;

namespace Envoy.Core.Tests;

public class LeaseCounterTests
{
    [Fact]
    public void Acquire_SetsHeld_DisposeClears()
    {
        var counter = new LeaseCounter();
        Assert.False(counter.IsHeld);
        var lease = counter.Acquire();
        Assert.True(counter.IsHeld);
        Assert.Equal(1, counter.Count);
        lease.Dispose();
        Assert.False(counter.IsHeld);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void TwoLeases_DisposeOne_StillHeld()
    {
        // The settings page and the setup guide each hold a lease; closing one must not stop the other's preview.
        var counter = new LeaseCounter();
        var main = counter.Acquire();
        var guide = counter.Acquire();
        guide.Dispose();
        Assert.True(counter.IsHeld);
        main.Dispose();
        Assert.False(counter.IsHeld);
    }

    [Fact]
    public void Dispose_Twice_DecrementsOnce()
    {
        var counter = new LeaseCounter();
        var keep = counter.Acquire();
        var lease = counter.Acquire();
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, counter.Count);
        keep.Dispose();
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void Parallel_AcquireRelease_EndsAtZero()
    {
        var counter = new LeaseCounter();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                var lease = counter.Acquire();
                lease.Dispose();
                lease.Dispose();
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Equal(0, counter.Count);
        Assert.False(counter.IsHeld);
    }
}
