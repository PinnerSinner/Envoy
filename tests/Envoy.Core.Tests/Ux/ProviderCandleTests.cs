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

    [Fact]
    public void Every_state_is_mapped() =>
        Assert.All(Enum.GetValues<ProviderState>(), s => Assert.True(Enum.IsDefined(ProviderCandle.For(s))));
}
