using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class CandleMotionTests
{
    [Theory]
    [InlineData(null, CandleState.Lit, CandleChange.None)]
    [InlineData(null, CandleState.Snuffed, CandleChange.None)]
    [InlineData(CandleState.Lit, CandleState.Lit, CandleChange.None)]
    [InlineData(CandleState.Lit, CandleState.Guttering, CandleChange.Gutter)]
    [InlineData(CandleState.Guttering, CandleState.Lit, CandleChange.Flare)]
    [InlineData(CandleState.Lit, CandleState.Snuffed, CandleChange.Snuff)]
    [InlineData(CandleState.Guttering, CandleState.Snuffed, CandleChange.Snuff)]
    [InlineData(CandleState.Snuffed, CandleState.Lit, CandleChange.Relight)]
    [InlineData(CandleState.Snuffed, CandleState.Guttering, CandleChange.Relight)]
    public void Each_change_has_its_animation(CandleState? from, CandleState to, CandleChange expected) =>
        Assert.Equal(expected, CandleMotion.Between(from, to));

    [Fact]
    public void A_guttering_flame_flickers_slower_and_further_and_a_snuffed_one_not_at_all()
    {
        var lit = CandleMotion.FlickerFor(CandleState.Lit);
        var gutter = CandleMotion.FlickerFor(CandleState.Guttering);
        var snuffed = CandleMotion.FlickerFor(CandleState.Snuffed);
        Assert.True(gutter.Period > lit.Period);
        Assert.True(gutter.Stretch > lit.Stretch);
        Assert.True(lit.Stretch > 0);
        Assert.Equal(0, snuffed.Stretch);
        Assert.Equal(TimeSpan.Zero, snuffed.Period);
    }
}
