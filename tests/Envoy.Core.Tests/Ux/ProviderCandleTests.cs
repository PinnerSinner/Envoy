using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class ProviderCandleTests
{
    [Theory]
    [InlineData(ProviderState.Unknown, CandleState.Lit)]
    [InlineData(ProviderState.Healthy, CandleState.Lit)]
    [InlineData(ProviderState.Degraded, CandleState.Guttering)]
    [InlineData(ProviderState.Throttled, CandleState.Guttering)]
    [InlineData(ProviderState.CoolingDown, CandleState.Guttering)]
    [InlineData(ProviderState.AuthFailed, CandleState.Snuffed)]
    [InlineData(ProviderState.QuotaExceeded, CandleState.Snuffed)]
    public void Each_state_has_a_candle(ProviderState state, CandleState expected) =>
        Assert.Equal(expected, ProviderCandle.For(state));

    [Theory]
    [InlineData(ProviderState.Unknown, null, CandleState.Lit)]
    [InlineData(ProviderState.Unknown, TranslationErrorKind.Configuration, CandleState.Snuffed)]
    [InlineData(ProviderState.Unknown, TranslationErrorKind.Transient, CandleState.Lit)]
    [InlineData(ProviderState.Healthy, TranslationErrorKind.Configuration, CandleState.Lit)]
    [InlineData(ProviderState.AuthFailed, TranslationErrorKind.Auth, CandleState.Snuffed)]
    public void Not_set_up_is_snuffed(ProviderState state, TranslationErrorKind? kind, CandleState expected) =>
        Assert.Equal(expected, ProviderCandle.For(state, kind));

    [Fact]
    public void Every_state_is_mapped() =>
        Assert.All(Enum.GetValues<ProviderState>(), s => Assert.True(Enum.IsDefined(ProviderCandle.For(s))));
}
