using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class BudgetTests
{
    private const string Text = "0123456789"; // 10 characters

    private static (BudgetedTranslator Budgeted, UsageTracker Usage, ScriptedTranslator Inner) Make()
    {
        var clock = TestClock.Create();
        var usage = new UsageTracker(clock);
        var inner = new ScriptedTranslator(ProviderCatalog.DeepL, metered: true);
        return (new BudgetedTranslator(inner, usage, () => 1000, clock: clock), usage, inner);
    }

    [Fact]
    public async Task Local_rejection_refunds_characters()
    {
        var (b, usage, inner) = Make();
        inner.Fail(new ProviderUnavailableException(ProviderCatalog.DeepL, ProviderState.CoolingDown, null, "skipped"));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => b.TranslateAsync(new(Text, "en")));
        Assert.Equal(0, usage.Today(ProviderCatalog.DeepL));

        inner.Fail(TranslationErrorKind.Configuration);
        await Assert.ThrowsAsync<TranslationException>(() => b.TranslateAsync(new(Text, "en")));
        Assert.Equal(0, usage.Today(ProviderCatalog.DeepL));
    }

    [Fact]
    public async Task Connection_failure_refunds_characters()
    {
        var (b, usage, inner) = Make();
        inner.Fail(new TranslationException("x", new HttpRequestException(HttpRequestError.ConnectionError, "refused"), TranslationErrorKind.Transient));
        await Assert.ThrowsAsync<TranslationException>(() => b.TranslateAsync(new(Text, "en")));
        Assert.Equal(0, usage.Today(ProviderCatalog.DeepL));

        inner.Fail(new TranslationException("x", new HttpRequestException(HttpRequestError.NameResolutionError, "dns"), TranslationErrorKind.Transient));
        await Assert.ThrowsAsync<TranslationException>(() => b.TranslateAsync(new(Text, "en")));
        Assert.Equal(0, usage.Today(ProviderCatalog.DeepL));

        inner.Fail(new TranslationException("x", new HttpRequestException(HttpRequestError.ResponseEnded, "cut"), TranslationErrorKind.Transient));
        await Assert.ThrowsAsync<TranslationException>(() => b.TranslateAsync(new(Text, "en")));
        Assert.Equal(10, usage.Today(ProviderCatalog.DeepL)); // it was sent
    }

    [Fact]
    public async Task Timeout_is_not_refunded()
    {
        var (b, usage, inner) = Make();
        inner.Fail(TranslationErrorKind.Timeout);
        await Assert.ThrowsAsync<TranslationException>(() => b.TranslateAsync(new(Text, "en")));
        inner.Fail(TranslationErrorKind.Transient, 500);
        await Assert.ThrowsAsync<TranslationException>(() => b.TranslateAsync(new(Text, "en")));
        Assert.Equal(20, usage.Today(ProviderCatalog.DeepL));
    }

    [Fact]
    public async Task Retries_reserve_characters_once()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL, jitter: 0);
        var usage = new UsageTracker(s.Clock);
        var metered = new ScriptedTranslator(ProviderCatalog.DeepL, metered: true)
            .Fail(TranslationErrorKind.Transient, 503).Fail(TranslationErrorKind.Transient, 503).Succeed();
        var resilient = new ResilientTranslator(metered, s.Guard, TranslationLane.Chat, s.Clock, () => 0);
        var b = new BudgetedTranslator(resilient, usage, () => 1000);
        await b.TranslateAsync(new(Text, "en"));
        Assert.Equal(3, metered.Calls);
        Assert.Equal(10, usage.Today(ProviderCatalog.DeepL));
    }

    [Fact]
    public async Task A_sent_attempt_is_never_refunded_by_a_later_connection_failure()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL, jitter: 0);
        var usage = new UsageTracker(s.Clock);
        var metered = new ScriptedTranslator(ProviderCatalog.DeepL, metered: true)
            .Fail(TranslationErrorKind.Transient, 503)
            .Fail(new TranslationException("x", new HttpRequestException(HttpRequestError.ConnectionError, "refused"), TranslationErrorKind.Transient))
            .Fail(new TranslationException("x", new HttpRequestException(HttpRequestError.ConnectionError, "refused"), TranslationErrorKind.Transient));
        var b = new BudgetedTranslator(new ResilientTranslator(metered, s.Guard, TranslationLane.Chat, s.Clock, () => 0), usage, () => 1000);
        await Assert.ThrowsAsync<TranslationException>(() => b.TranslateAsync(new(Text, "en")));
        Assert.Equal(10, usage.Today(ProviderCatalog.DeepL));
    }

    [Fact]
    public void Refund_never_goes_below_zero()
    {
        var usage = new UsageTracker(TestClock.Create());
        Assert.True(usage.TryConsume("p", 5, 0));
        usage.Refund("p", 50);
        Assert.Equal(0, usage.Today("p"));
        usage.Refund("unknown", 5);
        Assert.Equal(0, usage.Today("unknown"));
    }
}
