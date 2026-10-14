using Envoy.Core.Capture;
using Envoy.Core.Tests.Translation.Fakes;
using Xunit;

namespace Envoy.Core.Tests.Capture;

public class SingleFlightTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    [Fact]
    public async Task Completed_call_clears_busy()
    {
        var flight = new SingleFlight(TestClock.Create());
        var result = await flight.RunAsync(_ => Task.FromResult(42), Timeout, CancellationToken.None);
        Assert.Equal(42, result);
        Assert.False(flight.IsBusy);

        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = flight.RunAsync(_ => gate.Task, Timeout, CancellationToken.None);
        Assert.True(flight.IsBusy);
        gate.SetResult(7);
        Assert.Equal(7, await call);
        Assert.False(flight.IsBusy);
    }

    [Fact]
    public async Task Second_call_while_busy_throws_busy()
    {
        var flight = new SingleFlight(TestClock.Create());
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = flight.RunAsync(_ => gate.Task, Timeout, CancellationToken.None);
        var ran = false;
        await Assert.ThrowsAsync<SingleFlightBusyException>(() => flight.RunAsync(_ =>
        {
            ran = true;
            return Task.FromResult(1);
        }, Timeout, CancellationToken.None));
        Assert.False(ran);
        gate.SetResult(1);
        await first;
    }

    [Fact]
    public async Task Timeout_throws_TimeoutException_and_cancels_work_token()
    {
        var clock = TestClock.Create();
        var flight = new SingleFlight(clock);
        CancellationToken workToken = default;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = flight.RunAsync(token =>
        {
            workToken = token;
            return gate.Task;
        }, Timeout, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(3.9));
        await TestClock.Settle();
        Assert.False(call.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        await Assert.ThrowsAsync<TimeoutException>(() => call);
        Assert.True(workToken.IsCancellationRequested);
        gate.SetResult(0);
    }

    [Fact]
    public async Task Busy_stays_true_after_timeout_until_work_completes()
    {
        var clock = TestClock.Create();
        var flight = new SingleFlight(clock);
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = flight.RunAsync(_ => gate.Task, Timeout, CancellationToken.None);
        clock.Advance(Timeout + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => call);

        Assert.True(flight.IsBusy);
        await Assert.ThrowsAsync<SingleFlightBusyException>(() => flight.RunAsync(_ => Task.FromResult(1), Timeout, CancellationToken.None));

        gate.SetException(new InvalidOperationException("the abandoned call fails late")); // observed, never unobserved
        for (var i = 0; i < 100 && flight.IsBusy; i++) await TestClock.Settle(5);
        Assert.False(flight.IsBusy);
        Assert.Equal(2, await flight.RunAsync(_ => Task.FromResult(2), Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task Faulted_work_clears_busy_and_rethrows()
    {
        var flight = new SingleFlight(TestClock.Create());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flight.RunAsync<int>(_ => Task.FromException<int>(new InvalidOperationException("boom")), Timeout, CancellationToken.None));
        Assert.False(flight.IsBusy);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            flight.RunAsync<int>(_ => throw new ArgumentException("thrown before any task"), Timeout, CancellationToken.None));
        Assert.False(flight.IsBusy);

        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = flight.RunAsync(_ => gate.Task, Timeout, CancellationToken.None);
        gate.SetException(new InvalidOperationException("later"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        Assert.False(flight.IsBusy);
    }

    [Fact]
    public async Task Caller_cancellation_cancels_the_work()
    {
        var flight = new SingleFlight(TestClock.Create());
        using var cts = new CancellationTokenSource();
        CancellationToken workToken = default;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = flight.RunAsync(token =>
        {
            workToken = token;
            return gate.Task;
        }, Timeout, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(workToken.IsCancellationRequested);
        gate.SetCanceled();
        for (var i = 0; i < 100 && flight.IsBusy; i++) await TestClock.Settle(5);
        Assert.False(flight.IsBusy);
    }
}
