using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class TaleTextTests
{
    private static IEnumerable<string> AllText(IReadOnlyList<TaleChapter> chapters) =>
        chapters.SelectMany(c => new[] { c.Heading, c.Sentence, c.SealName })
            .Concat(TaleText.Promises)
            .Concat([
                TaleText.WindowTitle, TaleText.Title, TaleText.Subtitle, TaleText.ChapterOfText(0), TaleText.Play, TaleText.Pause,
                TaleText.Next, TaleText.Back, TaleText.Skip, TaleText.SetUp, TaleText.WatchAgain, TaleText.Offer, TaleText.StillHint,
                TaleText.ScreenLabel, TaleText.ScoutReads, TaleText.ProviderNote, TaleText.ReplyTo, TaleText.YourWords,
                TaleText.ScribeWrites, TaleText.GameChat, TaleText.Delivered,
            ]);

    [Fact]
    public void The_desk_heading_is_whole_and_splits_into_drop_cap_and_rest()
    {
        Assert.StartsWith("Reply to ", TaleText.ReplyTo, StringComparison.Ordinal);
        Assert.Equal("R", TaleText.ReplyToDropCap);
        Assert.Equal(TaleText.ReplyTo, TaleText.ReplyToDropCap + TaleText.ReplyToRest);
    }

    [Fact]
    public void There_are_six_chapters_in_order()
    {
        var chapters = TaleText.Chapters("Ctrl+Shift+T");
        Assert.Equal(6, chapters.Count);
        Assert.Equal(TalePlayer.ChapterCount, chapters.Count);
        Assert.Equal(Enumerable.Range(0, 6), chapters.Select(c => c.Index));
        Assert.Equal(["I", "II", "III", "IV", "V", "VI"], chapters.Select(c => c.Numeral));
        Assert.Equal(6, chapters.Select(c => c.Heading).Distinct().Count());
    }

    [Theory]
    [InlineData("Ctrl+Shift+T")]
    [InlineData("")]
    [InlineData(null)]
    public void No_em_or_en_dash_anywhere(string? hotkey)
    {
        foreach (var text in AllText(TaleText.Chapters(hotkey)))
        {
            Assert.DoesNotContain('—', text);
            Assert.DoesNotContain('–', text);
            Assert.DoesNotContain("never touch", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Ctrl+Shift+T")]
    [InlineData("Ctrl+Alt+Shift+F12")]
    [InlineData(null)]
    public void Each_caption_is_one_plain_sentence_under_120_characters(string? hotkey)
    {
        foreach (var chapter in TaleText.Chapters(hotkey))
        {
            Assert.InRange(chapter.Sentence.Length, 20, 119);
            Assert.EndsWith(".", chapter.Sentence, StringComparison.Ordinal);
            Assert.DoesNotContain(". ", chapter.Sentence, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(chapter.Heading));
            Assert.DoesNotContain('{', chapter.Sentence);
        }
    }

    [Fact]
    public void Chapter_four_names_the_reply_hotkey()
    {
        Assert.Contains("Ctrl+Shift+T", TaleText.Chapters(" Ctrl+Shift+T ")[3].Sentence, StringComparison.Ordinal);
        Assert.Equal(TaleText.Sentence4NoHotkey, TaleText.Chapters("  ")[3].Sentence);
    }

    [Fact]
    public void Seals_and_the_counter_name_the_chapter()
    {
        Assert.Equal("Chapter III: The interpreter answers", TaleText.Chapters(null)[2].SealName);
        Assert.Equal("Chapter VI of VI", TaleText.ChapterOfText(5));
        Assert.Equal("Chapter I of VI", TaleText.ChapterOfText(-3));
    }

    [Fact]
    public void Chapter_six_makes_three_promises() => Assert.Equal(3, TaleText.Promises.Count);
}
