using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

/// <summary>Spec 09a, REQ-HRD-6: the banner rail keeps all nine tabs reachable on short windows.</summary>
public class RailLayoutTests
{
    [Fact]
    public void A_tall_rail_shows_everything_with_full_tabs()
    {
        var layout = RailLayouts.For(2000);
        Assert.Equal(new RailLayout(CompactTabs: false, Crest: true, Motto: true, Status: true), layout);
    }

    [Fact]
    public void Parts_fold_away_in_order_as_the_rail_shrinks()
    {
        var seen = new List<RailLayout>();
        for (var h = 1000.0; h >= 300; h -= 1)
        {
            var layout = RailLayouts.For(h);
            if (seen.Count == 0 || seen[^1] != layout)
                seen.Add(layout);
        }
        Assert.Equal(
        [
            new RailLayout(false, true, true, true),
            new RailLayout(true, true, true, true),
            new RailLayout(true, false, true, true),
            new RailLayout(true, false, false, true),
            new RailLayout(true, false, false, false),
        ], seen);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(560)]
    [InlineData(780)]
    [InlineData(812)]
    [InlineData(1200)]
    public void The_chosen_layout_fits_whenever_any_layout_fits(double height)
    {
        var layout = RailLayouts.For(height);
        var barest = new RailLayout(true, false, false, false);
        if (RailLayouts.Needs(barest) <= height)
            Assert.True(RailLayouts.Needs(layout) <= height);
        else
            Assert.Equal(barest, layout);
    }

    [Fact]
    public void The_default_window_keeps_the_crest_and_the_minimum_window_keeps_every_tab()
    {
        // The window is 820 px tall by default (the rail spans the whole client area) and at least 600 px.
        Assert.True(RailLayouts.For(812).Crest);
        Assert.True(RailLayouts.Needs(RailLayouts.For(592)) <= 592);
    }

    [Fact]
    public void Compact_tabs_are_smaller_than_full_ones()
    {
        Assert.True(RailLayouts.Tabs(compact: true) < RailLayouts.Tabs(compact: false));
        Assert.Equal(4 + 9 * 54, RailLayouts.Tabs(compact: false));
    }
}
