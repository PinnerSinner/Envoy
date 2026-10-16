using System.Text;
using Envoy.Core.Phrases;
using Envoy.Core.Security;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Phrases;

public sealed class PhraseBookTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"envoy-phrases-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    private static int BuiltInCount => PhraseBook.LoadBuiltIn().All.Count;

    [Fact]
    public void Load_reports_invalid_json()
    {
        File.WriteAllText(_file, "[\n  {\"id\": \"mine\",\n  oops\n]");
        var book = PhraseBook.LoadWithUserFile(_file, out var error);
        Assert.Equal(BuiltInCount, book.All.Count);
        Assert.Equal("it isn't valid JSON (line 3)", error);
        Assert.NotNull(PageHints.Phrasebook(error));
    }

    [Fact]
    public void Load_reports_oversize()
    {
        var json = """[{"id":"mine","category":"Mine","text":{"en":"hi"}}]""";
        File.WriteAllText(_file, json + new string(' ', (int)PhraseLimits.MaxFileBytes + 1 - Encoding.UTF8.GetByteCount(json)));
        var book = PhraseBook.LoadWithUserFile(_file, out var error);
        Assert.Equal(PhrasebookText.LoadTooLarge, error);
        Assert.DoesNotContain(book.All, p => p.Id == "mine");
    }

    [Fact]
    public void Missing_file_has_no_error()
    {
        var book = PhraseBook.LoadWithUserFile(_file, out var error);
        Assert.Null(error);
        Assert.Equal(BuiltInCount, book.All.Count);
    }

    [Fact]
    public void Valid_file_has_no_error()
    {
        File.WriteAllText(_file, """[{"id":"mine","category":"Mine","text":{"en":"hi"}}]""");
        var book = PhraseBook.LoadWithUserFile(_file, out var error);
        Assert.Null(error);
        Assert.Contains(book.All, p => p.Id == "mine");
    }

    [Fact]
    public void One_argument_overload_unchanged()
    {
        File.WriteAllText(_file, "not json");
        Assert.Equal(BuiltInCount, PhraseBook.LoadWithUserFile(_file).All.Count);
        File.WriteAllText(_file, """[{"id":"mine","category":"Mine","text":{"en":"hi"}}]""");
        Assert.Equal(BuiltInCount + 1, PhraseBook.LoadWithUserFile(_file).All.Count);
    }
}
