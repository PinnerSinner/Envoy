using Envoy.Core.Phrases;
using Xunit;

namespace Envoy.Core.Tests;

public class PhrasePreviewTests
{
    private static Phrase P(string id, params (string Lang, string Text)[] text) =>
        new() { Id = id, Category = "Cat", Icon = "★", Text = text.ToDictionary(t => t.Lang, t => t.Text) };

    [Fact]
    public void Build_UsesMyLanguageThenEnglishThenId()
    {
        var book = new PhraseBook([
            P("a", ("de", "Hallo"), ("en", "Hello"), ("ko", "안녕")),
            P("b", ("en", "Bye"), ("ko", "잘가")),
            P("c", ("ko", "ㅋㅋ")),
        ]);
        var rows = PhrasePreview.Build(book, "de", "ko");
        Assert.Equal(["Hallo", "Bye", "c"], rows.Select(r => r.Mine));
        Assert.Equal(["안녕", "잘가", "ㅋㅋ"], rows.Select(r => r.Theirs));
        Assert.Equal(new PhrasePreviewRow("★", "Cat", "Hallo", "안녕"), rows[0]);
    }

    [Fact]
    public void Build_MissingTargetShowsDash()
    {
        var book = new PhraseBook([P("a", ("en", "Hello"))]);
        var row = Assert.Single(PhrasePreview.Build(book, "en", "ja"));
        Assert.Equal("—", row.Theirs);
        Assert.Equal(PhrasebookText.Missing, row.Theirs);
    }

    [Fact]
    public void Build_PreservesBookOrder()
    {
        var builtIn = PhraseBook.LoadBuiltIn();
        var rows = PhrasePreview.Build(builtIn, "en", "zh-CN");
        Assert.Equal(builtIn.All.Count, rows.Count);
        Assert.Equal(builtIn.All.Select(p => p.Icon), rows.Select(r => r.Icon));
        Assert.Equal(builtIn.All.Select(p => p.In("en") ?? p.Id), rows.Select(r => r.Mine));
    }

    [Fact]
    public void UserFileTemplate_LoadsAsUserPhrase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "envoy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "phrases.json");
            File.WriteAllText(path, PhrasePreview.UserFileTemplate);
            var book = PhraseBook.LoadWithUserFile(path);
            var mine = Assert.Single(book.All, p => p.Id == "my-first-phrase");
            Assert.Equal("만나서 반가워요!", mine.In("ko"));
            Assert.Equal("Nice to meet you!", mine.In("en"));
            Assert.Same(mine, book.All[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UserFileTemplate_MatchesLegacyLayout()
    {
        Assert.StartsWith("[\n  {\n    \"id\": \"my-first-phrase\",", PhrasePreview.UserFileTemplate.ReplaceLineEndings("\n"));
        Assert.EndsWith("  }\n]", PhrasePreview.UserFileTemplate.ReplaceLineEndings("\n"));
    }
}
