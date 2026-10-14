using Envoy.Core.Phrases;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Providers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests;

internal sealed class FakeTranslator(string id = "fake", bool metered = false) : ITranslator
{
    public List<TranslationRequest> Requests { get; } = [];
    public Func<TranslationRequest, TranslationResult>? Respond { get; set; }
    public Exception? Throw { get; set; }

    public string Id => id;
    public bool IsMetered => metered;

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Throw is not null) throw Throw;
        return Task.FromResult(Respond?.Invoke(request) ?? new TranslationResult($"[{request.TargetLanguage}] {request.Text}", "zh-CN", id));
    }
}

public class ProviderParsingTests
{
    [Fact]
    public void Parses_google_response()
    {
        const string json = """[[["Hello, ","你好，",null,null,10],["play again?","再来一局？",null,null,10]],null,"zh-CN",null,null,null,1,[],[["zh-CN"],null,[1],["zh-CN"]]]""";
        var r = GoogleFreeTranslator.Parse(json, "google-free");
        Assert.Equal("Hello, play again?", r.Text);
        Assert.Equal("zh-CN", r.DetectedSourceLanguage);
    }

    [Fact]
    public void Parses_edge_response()
    {
        const string json = """[{"detectedLanguage":{"language":"zh-Hans","score":1.0},"translations":[{"text":"Good game","to":"en"}]}]""";
        var r = EdgeFreeTranslator.Parse(json, "edge-free");
        Assert.Equal("Good game", r.Text);
        Assert.Equal("zh-CN", r.DetectedSourceLanguage);
    }

    [Fact]
    public void Parses_deepl_response()
    {
        const string json = """{"translations":[{"detected_source_language":"KO","text":"Let's go"}]}""";
        var r = DeepLTranslator.Parse(json, "deepl");
        Assert.Equal("Let's go", r.Text);
        Assert.Equal("ko", r.DetectedSourceLanguage);
    }

    [Theory]
    [InlineData("zh-CN|Nice play!", "Nice play!", "zh-CN")]
    [InlineData("ko | GG, I give up", "GG, I give up", "ko")]
    [InlineData("Just text with no code", "Just text with no code", null)]
    [InlineData("ru|Easy | too easy", "Easy | too easy", "ru")]
    public void Parses_claude_reply(string reply, string text, string? lang)
    {
        var r = ClaudeTranslator.Parse(reply, "claude");
        Assert.Equal(text, r.Text);
        Assert.Equal(lang, r.DetectedSourceLanguage);
    }
}

public class TranslatorStackTests
{
    [Fact]
    public async Task Cache_serves_repeats_without_calling_provider()
    {
        var inner = new FakeTranslator();
        var cached = new CachingTranslator(inner);
        await cached.TranslateAsync(new("gg", "en"));
        var second = await cached.TranslateAsync(new("gg", "en"));
        Assert.Single(inner.Requests);
        Assert.True(second.FromCache);
    }

    [Fact]
    public async Task Fallback_uses_next_provider_when_one_fails()
    {
        var broken = new FakeTranslator("a") { Throw = new TranslationException("down") };
        var working = new FakeTranslator("b");
        var failures = new List<string>();
        var chain = new FallbackTranslator([broken, working]);
        chain.ProviderFailed += (id, _) => failures.Add(id);

        var result = await chain.TranslateAsync(new("你好", "en"));
        Assert.Equal("b", result.ProviderId);
        Assert.Equal(["a"], failures);
    }

    [Fact]
    public async Task Fallback_reports_all_errors_when_everything_fails()
    {
        var chain = new FallbackTranslator([
            new FakeTranslator("a") { Throw = new TranslationException("first") },
            new FakeTranslator("b") { Throw = new TranslationException("second") },
        ]);
        var ex = await Assert.ThrowsAsync<TranslationException>(() => chain.TranslateAsync(new("x", "en")));
        Assert.Contains("first", ex.Message);
        Assert.Contains("second", ex.Message);
    }

    [Fact]
    public async Task Budget_blocks_metered_provider_over_daily_limit()
    {
        var usage = new UsageTracker(new FakeTimeProvider());
        var paid = new BudgetedTranslator(new FakeTranslator("paid", metered: true), usage, () => 10);
        await paid.TranslateAsync(new("12345", "en"));
        await paid.TranslateAsync(new("12345", "en"));
        await Assert.ThrowsAsync<BudgetExceededException>(() => paid.TranslateAsync(new("1", "en")));
    }

    [Fact]
    public async Task Budget_never_blocks_free_providers()
    {
        var usage = new UsageTracker(new FakeTimeProvider());
        var free = new BudgetedTranslator(new FakeTranslator("free", metered: false), usage, () => 1);
        await free.TranslateAsync(new("a long message", "en"));
        await free.TranslateAsync(new("another long message", "en"));
    }

    [Fact]
    public void Budget_resets_the_next_day()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var usage = new UsageTracker(clock);
        Assert.True(usage.TryConsume("p", 10, 10));
        Assert.False(usage.TryConsume("p", 1, 10));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(usage.TryConsume("p", 10, 10));
    }

    [Fact]
    public void Factory_skips_providers_missing_credentials_and_keeps_a_free_one()
    {
        var failures = new List<string>();
        var factory = new TranslatorFactory(new HttpClient(), new UsageTracker());
        var settings = new TranslationSettings { Primary = ProviderCatalog.Aws, Fallbacks = [] };
        var translator = factory.Build(settings, (id, _) => failures.Add(id));
        Assert.Contains(ProviderCatalog.Aws, failures);
        Assert.NotNull(translator);
    }
}

public class ChatPipelineTests
{
    private static ChatPipeline Pipeline(FakeTranslator t, string myName = "Me") =>
        new(t, new ChatPipelineOptions { MyLanguage = "en", MyPlayerName = myName });

    [Fact]
    public async Task Translates_new_foreign_messages_once()
    {
        var t = new FakeTranslator { Respond = r => new("Hello, rematch?", "zh-CN", "fake") };
        var p = Pipeline(t);

        var first = await p.ProcessAsync(["[All] 小明: 你好，再来一局？"]);
        var again = await p.ProcessAsync(["[All] 小明: 你好，再来一局？"]);

        var msg = Assert.Single(first);
        Assert.Empty(again);
        Assert.Equal(IncomingStatus.Translated, msg.Status);
        Assert.Equal("Hello, rematch?", msg.Translation);
        Assert.Equal("小明", msg.Chat.Sender);
        Assert.Equal("zh-CN", p.LastOpponentLanguage);
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task Ignores_my_own_messages()
    {
        var t = new FakeTranslator();
        var p = Pipeline(t, myName: "Pinner");
        var result = await p.ProcessAsync(["[All] Pinner: 你好"]);
        Assert.Empty(result);
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task Slang_is_answered_offline()
    {
        var t = new FakeTranslator();
        var p = Pipeline(t);
        var msg = Assert.Single(await p.ProcessAsync(["Kim: ㅈㅈ"]));
        Assert.True(msg.FromGlossary);
        Assert.Contains("give up", msg.Translation);
        Assert.Equal("ko", p.LastOpponentLanguage);
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task Messages_in_my_language_are_shown_untranslated()
    {
        var t = new FakeTranslator { Respond = r => new(r.Text, "en", "fake") };
        var p = Pipeline(t);
        var msg = Assert.Single(await p.ProcessAsync(["Bob: nice wall bro"]));
        Assert.Equal(IncomingStatus.Untranslated, msg.Status);
        Assert.Null(msg.Translation);
        Assert.Null(p.LastOpponentLanguage);
    }

    [Fact]
    public async Task Non_latin_text_in_my_language_skips_the_provider()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions { MyLanguage = "zh-CN" });
        var msg = Assert.Single(await p.ProcessAsync(["小明: 你好"]));
        Assert.Equal(IncomingStatus.Untranslated, msg.Status);
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task Provider_failure_marks_message_failed_instead_of_throwing()
    {
        var t = new FakeTranslator { Throw = new TranslationException("offline") };
        var p = Pipeline(t);
        var updated = new List<IncomingMessage>();
        p.MessageUpdated += updated.Add;
        var msg = Assert.Single(await p.ProcessAsync(["小明: 你好"]));
        Assert.Equal(IncomingStatus.Failed, msg.Status);
        Assert.Equal("offline", msg.Error);
        Assert.Single(updated);
    }

    [Fact]
    public async Task Noise_lines_are_dropped()
    {
        var t = new FakeTranslator();
        var p = Pipeline(t);
        Assert.Empty(await p.ProcessAsync(["", "  ", "|", "12 / 200", "--"]));
    }
}

public class OutgoingTests
{
    [Fact]
    public async Task Uses_phrasebook_translation_for_known_phrases()
    {
        var t = new FakeTranslator();
        var o = new OutgoingTranslator(() => t, PhraseBook.LoadBuiltIn());
        var r = await o.TranslateAsync("good luck, have fun", "en", "zh-CN");
        Assert.Equal("祝你好运，玩得开心！", r.Text);
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task Falls_back_to_translator_for_free_text()
    {
        var t = new FakeTranslator();
        var o = new OutgoingTranslator(() => t, PhraseBook.LoadBuiltIn());
        await o.TranslateAsync("your trebuchets are in range", "en", "ko");
        var req = Assert.Single(t.Requests);
        Assert.Equal("ko", req.TargetLanguage);
        Assert.Equal("en", req.SourceLanguage);
    }

    [Theory]
    [InlineData("auto", "ko", "en", "ko")]
    [InlineData("auto", null, "en", "zh-CN")]
    [InlineData("auto", null, "de", "en")]
    [InlineData("ru", "ko", "en", "ru")]
    public void Resolves_reply_language(string configured, string? last, string mine, string expected) =>
        Assert.Equal(expected, OutgoingTranslator.ResolveTarget(configured, last, mine));
}

public class PhraseBookTests
{
    [Fact]
    public void Every_built_in_phrase_covers_every_listed_language()
    {
        var book = PhraseBook.LoadBuiltIn();
        Assert.NotEmpty(book.All);
        var langs = book.All[0].Text.Keys.ToList();
        Assert.All(book.All, p => Assert.Equal(langs.OrderBy(x => x), p.Text.Keys.OrderBy(x => x)));
        Assert.All(book.All, p => Assert.All(p.Text.Values, v => Assert.False(string.IsNullOrWhiteSpace(v))));
    }

    [Fact]
    public void User_phrases_override_built_ins()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, """[{"id":"hello","category":"Mine","text":{"en":"Howdy","zh-CN":"你好呀"}}]""");
        var book = PhraseBook.LoadWithUserFile(file);
        Assert.Equal("Howdy", book.All.Single(p => p.Id == "hello").In("en"));
        Assert.Contains(book.All, p => p.Id == "ggwp");
    }
}

public class SettingsStoreTests
{
    private sealed class ReverseProtector : ISecretProtector
    {
        public string Protect(string s) => "enc:" + new string(s.Reverse().ToArray());
        public string Unprotect(string s) => s.StartsWith("enc:") ? new string(s[4..].Reverse().ToArray()) : throw new FormatException();
    }

    [Fact]
    public void Round_trips_settings_and_encrypts_secrets_on_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new SettingsStore(path, new ReverseProtector());
        var s = new AppSettings { MyLanguage = "de" };
        s.Translation.ClaudeKey = "sk-ant-secret";
        s.Capture.Regions["aoe4"] = new ScreenRegion(10, 20, 300, 200);
        store.Save(s);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("sk-ant-secret", raw);
        Assert.Equal("sk-ant-secret", s.Translation.ClaudeKey); // in-memory copy untouched

        var loaded = store.Load();
        Assert.Equal("de", loaded.MyLanguage);
        Assert.Equal("sk-ant-secret", loaded.Translation.ClaudeKey);
        Assert.Equal(new ScreenRegion(10, 20, 300, 200), loaded.Capture.Regions["aoe4"]);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ not json");
        var loaded = new SettingsStore(path, new NoOpProtector()).Load();
        Assert.Equal("en", loaded.MyLanguage);
        Assert.True(File.Exists(path + ".broken"));
    }
}
