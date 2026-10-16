using Envoy.Core.Privacy;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class TranslationGateTests
{
    private static readonly TranslationRequest Request = new("你好", "en");

    [Fact]
    public async Task Closed_gate_never_calls_inner()
    {
        var inner = new ScriptedTranslator("google-free");
        var gate = new PrivacyGatedTranslator(inner, () => false);
        await Assert.ThrowsAsync<TranslationException>(() => gate.TranslateAsync(Request));
        await Assert.ThrowsAsync<TranslationException>(() => gate.TranslateAsync(Request));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Closed_gate_throws_configuration_kind()
    {
        var gate = new PrivacyGatedTranslator(new ScriptedTranslator(), () => false);
        var task = gate.TranslateAsync(Request); // a faulted task, not a synchronous throw
        var ex = await Assert.ThrowsAsync<TranslationException>(() => task);
        Assert.Equal(TranslationErrorKind.Configuration, ex.Kind);
        Assert.Equal(PrivacyText.TranslationOff, ex.Message);
    }

    [Fact]
    public async Task Open_gate_forwards()
    {
        var inner = new ScriptedTranslator("deepl", metered: true);
        var gate = new PrivacyGatedTranslator(inner, () => true);
        var result = await gate.TranslateAsync(Request);
        Assert.Equal("[en] 你好", result.Text);
        Assert.Equal(1, inner.Calls);
        Assert.Equal("deepl", gate.Id);
        Assert.True(gate.IsMetered);
        Assert.Same(inner, gate.Inner);
    }

    [Fact]
    public async Task Version_bump_closes_gate()
    {
        // AppHost publishes IsAcknowledged(PrivacyAcknowledgedVersion); a version below the current one closes it.
        var acknowledged = PrivacyText.Version - 1;
        var inner = new ScriptedTranslator();
        var gate = new PrivacyGatedTranslator(inner, () => PrivacyText.IsAcknowledged(acknowledged));
        await Assert.ThrowsAsync<TranslationException>(() => gate.TranslateAsync(Request));
        acknowledged = PrivacyText.Version;
        await gate.TranslateAsync(Request);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Cancelled_call_is_cancelled_even_when_closed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gate = new PrivacyGatedTranslator(new ScriptedTranslator(), () => false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.TranslateAsync(Request, cts.Token));
    }
}
