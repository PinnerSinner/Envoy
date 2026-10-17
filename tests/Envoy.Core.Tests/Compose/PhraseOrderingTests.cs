using Envoy.Core.Compose;
using Envoy.Core.Phrases;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Compose;

public class PhraseOrderingTests
{
    private static Phrase P(string id, params string[] languages) =>
        new() { Id = id, Text = languages.ToDictionary(l => l, l => id + "-" + l) };

    private static readonly IReadOnlyList<Phrase> Book = [P("gg", "en"), P("glhf", "en", "ko"), P("wp", "en", "ko", "ru"), P("rematch", "en", "ru")];

    private static string[] Ids(IEnumerable<Phrase> phrases) => phrases.Select(p => p.Id).ToArray();

    [Fact]
    public void Book_order_unchanged()
    {
        var usage = new Dictionary<string, Dictionary<string, int>> { ["ko"] = new() { ["wp"] = 9 } };
        Assert.Same(Book, PhraseOrdering.Order(Book, "ko", usage, PhraseOrder.BookOrder));
    }

    [Fact]
    public void Translated_first_then_untranslated()
    {
        Assert.Equal(["glhf", "wp", "gg", "rematch"], Ids(PhraseOrdering.Order(Book, "ko", new Dictionary<string, Dictionary<string, int>>(), PhraseOrder.MostUsed)));
    }

    [Fact]
    public void Most_used_first_within_group_ties_keep_book_order()
    {
        var usage = new Dictionary<string, Dictionary<string, int>>
        {
            ["ko"] = new() { ["wp"] = 3, ["rematch"] = 5, ["gg"] = 5 },
        };
        // Translated group: wp (3) before glhf (0). Untranslated group: gg and rematch tie at 5, book order.
        Assert.Equal(["wp", "glhf", "gg", "rematch"], Ids(PhraseOrdering.Order(Book, "ko", usage, PhraseOrder.MostUsed)));
    }

    [Fact]
    public void Usage_is_per_target_language()
    {
        var usage = new Dictionary<string, Dictionary<string, int>>();
        PhraseOrdering.RecordUse(usage, "ko", "wp");
        PhraseOrdering.RecordUse(usage, "ko", "wp");
        Assert.Equal("wp", PhraseOrdering.Order(Book, "ko", usage, PhraseOrder.MostUsed)[0].Id);
        Assert.Equal(["wp", "rematch", "gg", "glhf"], Ids(PhraseOrdering.Order(Book, "ru", usage, PhraseOrder.MostUsed)));
        PhraseOrdering.RecordUse(usage, "ru", "rematch");
        Assert.Equal(["rematch", "wp", "gg", "glhf"], Ids(PhraseOrdering.Order(Book, "ru", usage, PhraseOrder.MostUsed)));
    }

    [Fact]
    public void RecordUse_normalises_language_saturates_and_caps()
    {
        var usage = new Dictionary<string, Dictionary<string, int>>();
        PhraseOrdering.RecordUse(usage, "zh-Hans", "gg");
        PhraseOrdering.RecordUse(usage, "zh_cn", "gg");
        Assert.Equal(2, usage["zh-CN"]["gg"]);
        Assert.Single(usage);

        usage["en"] = new() { ["gg"] = PhraseOrdering.MaxCount };
        PhraseOrdering.RecordUse(usage, "en", "gg");
        Assert.Equal(PhraseOrdering.MaxCount, usage["en"]["gg"]);

        var many = new Dictionary<string, int>();
        for (var i = 0; i < PhraseOrdering.MaxIdsPerLanguage; i++) many["p" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture)] = i + 2;
        many["p000"] = 1; // the lowest
        usage["ru"] = many;
        PhraseOrdering.RecordUse(usage, "ru", "fresh");
        Assert.Equal(PhraseOrdering.MaxIdsPerLanguage, usage["ru"].Count);
        Assert.False(usage["ru"].ContainsKey("p000"));
        Assert.Equal(1, usage["ru"]["fresh"]);
    }
}
