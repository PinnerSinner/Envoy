using System.Reflection;
using Envoy.Core.Chat;
using Envoy.Core.Diagnostics;
using Envoy.Core.Ocr;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests;

public class ChatPrivacyTests
{
    [Fact]
    public async Task Pipeline_logs_no_chat_text_by_default()
    {
        using var hub = new LogHub { MinimumLevel = LogLevel.Debug, IncludeSensitive = false };
        var sink = new MemoryLogSink();
        hub.AddSink(sink);
        var log = hub.For("Chat");

        // Invalid patterns (logged once), a lifecycle cancel and an analyzer fallback.
        var t = new GatedTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions { SystemLinePatterns = ["re:(Tiramisu", "*Zanzibar*"] }, log: log);
        var run = p.ProcessAsync(OcrLine.FromTexts(["Wolfgang: Quixotic rhubarb plans", "continue northward"], 18, 4));
        await t.WaitForStartedAsync(1);
        p.NewMatch();
        await run;
        p.Translator = new FakeTranslator();
        p.BeforeAnalyze = () => throw new InvalidOperationException("Wolfgang said Quixotic");
        await p.ProcessAsync(OcrLine.FromTexts(["Wolfgang: Quixotic again"], 18));
        var formats = ChatFormatCatalog.LoadWithUserFile("/nonexistent/Zanzibar/chat-formats.json", log);
        Assert.Empty(formats.Errors);
        hub.Flush();

        var text = string.Join("\n", sink.Snapshot().Select(e => $"{e.Message} {e.Fields} {e.Exception}"));
        Assert.Contains("Notice pattern ignored", text, StringComparison.Ordinal);
        Assert.Contains("Chat analysis failed", text, StringComparison.Ordinal);
        foreach (var word in new[] { "Wolfgang", "Quixotic", "rhubarb", "northward", "Tiramisu", "Zanzibar" })
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemLinePatterns_is_marked_personal()
    {
        var property = typeof(ChatSettings).GetProperty(nameof(ChatSettings.SystemLinePatterns))!;
        Assert.NotNull(property.GetCustomAttribute<PersonalAttribute>());

        var settings = new AppSettings();
        settings.Chat.SystemLinePatterns = ["Wolfgang resigned", "*Zanzibar*"];
        var redacted = SettingsRedaction.ToRedactedJson(settings);
        Assert.DoesNotContain("Wolfgang", redacted, StringComparison.Ordinal);
    }
}
