using Envoy.Core.Compose;
using Xunit;

namespace Envoy.Core.Tests.Compose;

public class ReplyHistoryTests
{
    [Fact]
    public void Add_ignores_blank_and_moves_duplicate_to_front()
    {
        var h = new ReplyHistory();
        h.Add("gg wp");
        h.Add("  ");
        h.Add("");
        h.Add(null);
        h.Add(" rematch? ");
        h.Add("gg wp");
        Assert.Equal(["gg wp", "rematch?"], h.Entries);
        h.Add("GG WP"); // Ordinal: a different case is a different entry.
        Assert.Equal(["GG WP", "gg wp", "rematch?"], h.Entries);
    }

    [Fact]
    public void Capacity_drops_oldest()
    {
        var h = new ReplyHistory(3);
        foreach (var t in new[] { "a", "b", "c", "d" }) h.Add(t);
        Assert.Equal(["d", "c", "b"], h.Entries);
        Assert.Equal(50, Enumerable.Range(0, 60).Aggregate(new ReplyHistory(), (x, i) => { x.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture)); return x; }).Entries.Count);
    }

    [Fact]
    public void Older_walks_back_and_stops_at_oldest()
    {
        var h = new ReplyHistory();
        Assert.Null(h.Older("draft"));
        Assert.False(h.IsBrowsing);
        h.Add("first");
        h.Add("second");
        Assert.Equal("second", h.Older("dr"));
        Assert.True(h.IsBrowsing);
        Assert.Equal("first", h.Older("second"));
        Assert.Null(h.Older("first"));
        Assert.True(h.IsBrowsing);
    }

    [Fact]
    public void Newer_past_newest_restores_draft()
    {
        var h = new ReplyHistory();
        h.Add("first");
        h.Add("second");
        Assert.Equal("second", h.Older("dr"));
        Assert.Equal("first", h.Older("second"));
        Assert.Equal("second", h.Newer());
        Assert.Equal("dr", h.Newer());
        Assert.False(h.IsBrowsing);
        Assert.Null(h.Newer());
    }

    [Fact]
    public void Add_and_EndBrowsing_reset_cursor()
    {
        var h = new ReplyHistory();
        h.Add("first");
        h.Add("second");
        h.Older("");
        h.Older("");
        h.EndBrowsing();
        Assert.False(h.IsBrowsing);
        Assert.Equal("second", h.Older("typed"));
        h.Add("third");
        Assert.False(h.IsBrowsing);
        Assert.Equal("third", h.Older(""));
    }

    [Fact]
    public void Newer_without_browsing_returns_null()
    {
        var h = new ReplyHistory();
        Assert.Null(h.Newer());
        h.Add("x");
        Assert.Null(h.Newer());
    }
}
