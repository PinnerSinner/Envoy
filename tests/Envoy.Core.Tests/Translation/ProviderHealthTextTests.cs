using Envoy.Core.Diagnostics;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class ProviderHealthTextTests
{
    private static readonly DateTimeOffset Now = TestClock.Start;

    private static ProviderHealthSnapshot Snap(ProviderState state, TimeSpan? retryIn = null, TimeSpan? median = null,
                                              TranslationErrorKind? kind = null, string id = "google-free") =>
        ProviderHealthSnapshot.Empty(id) with
        {
            State = state,
            RetryAt = retryIn is { } r ? Now + r : null,
            MedianLatency = median,
            LastErrorKind = kind,
        };

    public static TheoryData<ProviderHealthSnapshot, string> States() => new()
    {
        { Snap(ProviderState.Unknown), "Not used yet." },
        { Snap(ProviderState.Unknown, kind: TranslationErrorKind.Configuration), "Not set up." },
        { Snap(ProviderState.Healthy, median: TimeSpan.FromMilliseconds(340)), "Working · 340 ms" },
        { Snap(ProviderState.Healthy), "Working." },
        { Snap(ProviderState.Degraded, median: TimeSpan.FromMilliseconds(900)), "Working, but some requests failed · 900 ms" },
        { Snap(ProviderState.Throttled, TimeSpan.FromSeconds(18)), "Skipping for 18 s: it is limiting requests." },
        { Snap(ProviderState.Throttled), "It was limiting requests. Envoy tries it again with the next message." },
        { Snap(ProviderState.CoolingDown, TimeSpan.FromSeconds(17.2)), "Skipping for 18 s after repeated failures." },
        { Snap(ProviderState.CoolingDown, TimeSpan.FromSeconds(-1)), "It failed repeatedly. Envoy tries it again with the next message." },
        { Snap(ProviderState.AuthFailed), "Your key was rejected. Check it above." },
        { Snap(ProviderState.QuotaExceeded, TimeSpan.FromMinutes(42)), "Its quota is used up. Envoy tries it again in 42 min." },
        { Snap(ProviderState.QuotaExceeded, TimeSpan.FromHours(11.5)), "Its quota is used up. Envoy tries it again in 12 h." },
        { Snap(ProviderState.QuotaExceeded), "Its quota is used up." },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Describe_formats_each_state(ProviderHealthSnapshot snapshot, string expected) =>
        Assert.Equal(expected, ProviderHealthText.Describe(snapshot, Now));

    [Fact]
    public void Summary_names_active_provider_when_primary_unavailable()
    {
        string[] names = ["Google Translate (free)", "Microsoft Translator (free)"];
        Assert.Equal("Using Google Translate (free).",
            ProviderHealthText.Summary([Snap(ProviderState.Healthy), Snap(ProviderState.Unknown, id: "edge-free")], names, Now));
        Assert.Equal("Google Translate (free) is unavailable, so Envoy is using Microsoft Translator (free).",
            ProviderHealthText.Summary([Snap(ProviderState.CoolingDown, TimeSpan.FromSeconds(20)), Snap(ProviderState.Healthy, id: "edge-free")], names, Now));
        Assert.Equal("Using Google Translate (free).", // the cooling-down period is over: the next line probes it
            ProviderHealthText.Summary([Snap(ProviderState.CoolingDown, TimeSpan.FromSeconds(-1)), Snap(ProviderState.Healthy, id: "edge-free")], names, Now));
        Assert.Equal(ProviderHealthText.NoneAvailable,
            ProviderHealthText.Summary([Snap(ProviderState.AuthFailed), Snap(ProviderState.Throttled, TimeSpan.FromSeconds(5), id: "edge-free")], names, Now));
    }

    [Fact]
    public void Sanitize_uses_LogRedactor_mask()
    {
        var redactor = new LogRedactor();
        redactor.SetSecrets(["my-registered-secret"]);
        var text = ProviderHealthText.Sanitize("key sk-ant-api03-ABCDEFGHIJKLMNOP and my-registered-secret at https://x.test/?key=1", redactor);
        Assert.Contains(LogRedactor.Mask, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFGHIJ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("my-registered-secret", text, StringComparison.Ordinal);

        var cut = ProviderHealthText.Sanitize(new string('x', 500) + "\nline", redactor);
        Assert.Equal(ProviderHealthText.MaxErrorLength, cut.Length);
        Assert.DoesNotContain('\n', cut);
    }

    [Fact]
    public void Retry_and_last_success_text()
    {
        Assert.Equal("Retry DeepL now", ProviderHealthText.RetryButtonName("DeepL"));
        Assert.Equal(ProviderHealthText.LastSuccessNever, ProviderHealthText.LastSuccess(Snap(ProviderState.Unknown), Now));
        Assert.Equal("Last translation 12 s ago.", ProviderHealthText.LastSuccess(Snap(ProviderState.Healthy) with { LastSuccess = Now.AddSeconds(-12) }, Now));
        Assert.Equal("Last translation 3 min ago.", ProviderHealthText.LastSuccess(Snap(ProviderState.Healthy) with { LastSuccess = Now.AddMinutes(-3) }, Now));
        Assert.Equal("Last translation 2 h ago.", ProviderHealthText.LastSuccess(Snap(ProviderState.Healthy) with { LastSuccess = Now.AddHours(-2) }, Now));
        Assert.Equal("1 translation saved on this PC.", TranslationCacheText.Count(1));
        Assert.Equal(TranslationCacheText.SavedNone, TranslationCacheText.Count(0));
    }
}
