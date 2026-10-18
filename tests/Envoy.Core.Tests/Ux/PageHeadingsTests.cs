using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

/// <summary>Spec 09a, REQ-HRD-6, 11: the war table's tabs and headings.</summary>
public class PageHeadingsTests
{
    [Fact]
    public void Nine_pages_in_sidebar_order_with_playful_and_plain_names()
    {
        Assert.Equal(["Dashboard", "Translation", "Capture", "Overlay", "Reply", "Phrases", "Hotkeys", "Appearance", "About"],
            PageHeadings.All.Select(h => h.PageId));
        Assert.Equal(["War Room", "Interpreters", "Scouts", "Heralds", "Couriers", "Codex", "Seals", "Livery", "Chronicle"],
            PageHeadings.All.Select(h => h.Name));
        Assert.Equal(["Home", "Translation", "Reading chat", "Overlay", "Replying", "Phrasebook", "Hotkeys", "Appearance", "About"],
            PageHeadings.All.Select(h => h.PlainName));
    }

    [Fact]
    public void Drop_cap_is_the_first_letter_and_the_accessible_name_is_plain()
    {
        foreach (var h in PageHeadings.All)
        {
            Assert.Equal(h.Name[..1], h.DropCap);
            Assert.Equal(h.PlainName, h.AccessibleName);
        }
    }

    [Fact]
    public void Ledes_are_one_plain_line()
    {
        foreach (var h in PageHeadings.All)
        {
            Assert.InRange(h.Lede.Length, 20, 120);
            Assert.DoesNotContain('\n', h.Lede);
            foreach (var archaic in new[] { "thou", "thee", "thy", "thine" })
                Assert.DoesNotContain(archaic, h.Lede.Split(' ', ',', ';', '.', ':'), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain('—', h.Lede + h.FigureCaption);
            Assert.StartsWith("Fig. ", h.FigureCaption);
        }
    }

    [Fact]
    public void Unknown_page_is_the_war_room() => Assert.Equal("War Room", PageHeadings.For("nope").Name);

    [Theory]
    [InlineData(1099.9, false)]
    [InlineData(1100, true)]
    [InlineData(1400, true)]
    public void Marginalia_from_1100_px(double width, bool shown) => Assert.Equal(shown, PageHeadings.ShowsMarginalia(width));
}
