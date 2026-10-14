using System.Text;
using System.Text.Json;
using Envoy.Core.Phrases;
using Envoy.Core.Security;
using Xunit;

namespace Envoy.Core.Tests.Security;

public sealed class PhraseBookLimitsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"envoy-phrases-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    private PhraseBook Load(object phrases)
    {
        File.WriteAllText(_file, JsonSerializer.Serialize(phrases));
        return PhraseBook.LoadWithUserFile(_file);
    }

    private static int BuiltInCount => PhraseBook.LoadBuiltIn().All.Count;

    [Fact]
    public void Oversized_user_file_is_ignored()
    {
        var json = """[{"id":"mine","category":"Mine","text":{"en":"hi"}}]""";
        var padding = new string(' ', (int)PhraseLimits.MaxFileBytes + 1 - Encoding.UTF8.GetByteCount(json));
        File.WriteAllText(_file, json + padding);
        Assert.Equal(PhraseLimits.MaxFileBytes + 1, new FileInfo(_file).Length);

        var book = PhraseBook.LoadWithUserFile(_file);
        Assert.Equal(BuiltInCount, book.All.Count);
        Assert.DoesNotContain(book.All, p => p.Id == "mine");
    }

    [Fact]
    public void File_at_the_limit_still_loads()
    {
        var json = """[{"id":"mine","category":"Mine","text":{"en":"hi"}}]""";
        File.WriteAllText(_file, json + new string(' ', (int)PhraseLimits.MaxFileBytes - json.Length));
        Assert.Contains(PhraseBook.LoadWithUserFile(_file).All, p => p.Id == "mine");
    }

    [Fact]
    public void User_text_is_sanitized()
    {
        var book = Load(new[] { new { id = "mine", text = new Dictionary<string, string> { ["en"] = "gl\r\nhf\u202E" } } });
        Assert.Equal("gl hf", book.All.Single(p => p.Id == "mine").In("en"));
    }

    [Fact]
    public void User_text_is_capped_and_empty_texts_are_removed()
    {
        var book = Load(new[]
        {
            new { id = "mine", text = new Dictionary<string, string> { ["en"] = new string('a', 900), ["de"] = "\n\u202E " } },
        });
        var phrase = book.All.Single(p => p.Id == "mine");
        Assert.Equal(PhraseLimits.MaxTextLength, phrase.In("en")!.Length);
        Assert.Null(phrase.In("de"));
    }

    [Fact]
    public void User_phrase_count_is_capped()
    {
        var user = Enumerable.Range(0, 600).Select(i => new { id = $"user-{i}", text = new Dictionary<string, string> { ["en"] = "x" } });
        var book = Load(user.ToArray());
        var userIds = book.All.Where(p => p.Id.StartsWith("user-", StringComparison.Ordinal)).Select(p => p.Id).ToList();
        Assert.Equal(PhraseLimits.MaxPhrases, userIds.Count);
        Assert.Equal("user-0", userIds[0]);
        Assert.Equal("user-499", userIds[^1]);
    }

    [Fact]
    public void Long_fields_are_truncated()
    {
        var book = Load(new[]
        {
            new { id = new string('i', 100), category = new string('c', 100), icon = new string('x', 40), text = new Dictionary<string, string> { ["en"] = "x" } },
        });
        var phrase = book.All.Single(p => p.Id.StartsWith('i'));
        Assert.Equal(PhraseLimits.MaxIdLength, phrase.Id.Length);
        Assert.Equal(PhraseLimits.MaxCategoryLength, phrase.Category.Length);
        Assert.Equal(PhraseLimits.MaxIconLength, phrase.Icon.Length);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair() =>
        Assert.Equal(new string('x', 15), PhraseLimits.Truncate(new string('x', 15) + "😀", 16));

    [Fact]
    public void Phrases_without_id_are_dropped()
    {
        File.WriteAllText(_file, """[{"id":"","category":"Mine","text":{"en":"no id"}},{"category":"Mine","text":{"en":"none"}},{"id":"ok","text":null}]""");
        var book = PhraseBook.LoadWithUserFile(_file);
        Assert.DoesNotContain(book.All, p => p.Id.Length == 0);
        Assert.DoesNotContain(book.All, p => p.In("en") is "no id" or "none");
        Assert.Equal(BuiltInCount + 1, book.All.Count);
    }
}
