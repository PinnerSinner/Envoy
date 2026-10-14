using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class CoalescingTranslatorTests
{
    private static CoalescingTranslator Coalesce(ITranslator inner) => new(inner, r => TranslationCache.Key("", r));

    private static ScriptedTranslator Gated(TaskCompletionSource gate) =>
        new() { Default = async (r, ct) => { await gate.Task.WaitAsync(ct); return new TranslationResult("[en] " + r.Text, "zh-CN", "scripted"); } };

    [Fact]
    public async Task Identical_concurrent_requests_make_one_call()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = Gated(gate);
        var c = Coalesce(inner);
        var a = c.TranslateAsync(new("gg", "en"));
        var b = c.TranslateAsync(new("gg ", "en"));
        gate.SetResult();
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, inner.Calls);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(0, c.InFlight);
    }

    [Fact]
    public async Task Different_keys_are_not_coalesced()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = Gated(gate);
        var c = Coalesce(inner);
        var a = c.TranslateAsync(new("gg", "en"));
        var b = c.TranslateAsync(new("gg", "de"));
        var d = c.TranslateAsync(new("wp", "en"));
        gate.SetResult();
        await Task.WhenAll(a, b, d).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task One_waiter_cancelling_does_not_cancel_others()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = Gated(gate);
        var c = Coalesce(inner);
        using var cts = new CancellationTokenSource();
        var leaving = c.TranslateAsync(new("gg", "en"), cts.Token);
        var staying = c.TranslateAsync(new("gg", "en"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        Assert.Equal(1, inner.InFlight);
        gate.SetResult();
        Assert.Equal("[en] gg", (await staying.WaitAsync(TimeSpan.FromSeconds(5))).Text);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task All_waiters_cancelling_cancels_inner_call()
    {
        var inner = new ScriptedTranslator().Hang();
        var c = Coalesce(inner);
        using var one = new CancellationTokenSource();
        using var two = new CancellationTokenSource();
        var a = c.TranslateAsync(new("gg", "en"), one.Token);
        var b = c.TranslateAsync(new("gg", "en"), two.Token);
        one.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.Equal(1, inner.InFlight);
        two.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        for (var i = 0; i < 100 && inner.InFlight > 0; i++) await Task.Delay(10);
        Assert.Equal(0, inner.InFlight);
        Assert.Equal(0, c.InFlight);

        var fresh = await c.TranslateAsync(new("gg", "en")).WaitAsync(TimeSpan.FromSeconds(5)); // a new flight starts afresh
        Assert.Equal(2, inner.Calls);
        Assert.False(string.IsNullOrEmpty(fresh.Text));
    }

    [Fact]
    public async Task Failure_is_shared_and_entry_removed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedTranslator().Then(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            throw new TranslationException("down", null, TranslationErrorKind.Transient);
        });
        var c = Coalesce(inner);
        var a = c.TranslateAsync(new("gg", "en"));
        var b = c.TranslateAsync(new("gg", "en"));
        gate.SetResult();
        var ea = await Assert.ThrowsAsync<TranslationException>(() => a);
        var eb = await Assert.ThrowsAsync<TranslationException>(() => b);
        Assert.Same(ea, eb);
        Assert.Equal(0, c.InFlight);
        await c.TranslateAsync(new("gg", "en"));
        Assert.Equal(2, inner.Calls);
    }
}
