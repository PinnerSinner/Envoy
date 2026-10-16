using Envoy.Core.Chat;
using Envoy.Core.Ocr;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests;

public class ChatPipelineOptionsTests
{
    [Fact]
    public void Snapshot_is_isolated_from_settings()
    {
        var settings = new AppSettings { MyPlayerName = "Hoplite", MutedPlayers = ["Spammer"] };
        settings.Chat.SystemLinePatterns = ["*resigned*"];
        var snapshot = ChatPipelineOptions.From(settings, null);

        settings.MutedPlayers.Add("Toxic99");
        settings.Chat.SystemLinePatterns.Add("*joined*");
        settings.MyPlayerName = "Someone";
        settings.Chat.HideSystemLines = false;

        Assert.Equal(["Spammer"], snapshot.MutedPlayers);
        Assert.Equal(["*resigned*"], snapshot.SystemLinePatterns);
        Assert.Equal("Hoplite", snapshot.MyPlayerName);
        Assert.True(snapshot.HideSystemLines);
    }

    [Fact]
    public void From_copies_every_chat_setting()
    {
        var settings = new AppSettings { MyLanguage = "de", Channels = ChannelFilter.TeamOnly };
        settings.Translation.SkipMyLanguage = false;
        settings.Translation.UseSlangGlossary = false;
        settings.Capture.MinCharacters = 5;
        settings.Chat.StitchWrappedLines = false;
        settings.Chat.HideSystemLines = false;
        settings.Chat.DetectSystemLinesByPlayerName = true;
        settings.Chat.CleanSymbols = false;
        var profile = ChatFormatCatalog.BuiltIn.Generic;

        var o = ChatPipelineOptions.From(settings, profile);

        Assert.Equal(("de", false, false, 5, ChannelFilter.TeamOnly), (o.MyLanguage, o.SkipMyLanguage, o.UseSlangGlossary, o.MinCharacters, o.Channels));
        Assert.Equal((false, false, true, false), (o.StitchWrappedLines, o.HideSystemLines, o.DetectSystemLinesByPlayerName, o.CleanSymbols));
        Assert.Same(profile, o.Format);

        var defaults = ChatPipelineOptions.From(new AppSettings(), null);
        Assert.Equal(new ChatPipelineOptions() with { MutedPlayers = defaults.MutedPlayers, SystemLinePatterns = defaults.SystemLinePatterns }, defaults);
    }

    [Fact]
    public async Task Options_swap_mid_capture_uses_one_snapshot()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var swapped = false;
        p.BeforeAnalyze = () =>
        {
            if (swapped) return;
            swapped = true;
            p.Options = p.Options with { MyPlayerName = "Bob", HideSystemLines = false, SystemLinePatterns = ["*"] };
        };

        // The capture read its snapshot before the swap: Bob is not "me" and nothing is a notice yet.
        var first = Assert.Single(await p.ProcessAsync(OcrLine.FromTexts(["Bob: 你好"], 18)));
        Assert.Equal(ChatLineKind.Chat, first.Chat.Kind);

        // The next capture sees the new snapshot.
        var second = Assert.Single(await p.ProcessAsync(OcrLine.FromTexts(["Kim: 안녕하세요"], 18)));
        Assert.Equal(ChatLineKind.System, second.Chat.Kind);
        Assert.Equal("Bob", p.Options.MyPlayerName);
    }

    [Fact]
    public void Pattern_errors_follow_the_options()
    {
        var p = new ChatPipeline(new FakeTranslator(), new ChatPipelineOptions { SystemLinePatterns = ["*ok*", "re:(["] });
        Assert.Equal(1, Assert.Single(p.SystemPatternErrors).Index);
        p.Options = p.Options with { SystemLinePatterns = ["*ok*"] };
        Assert.Empty(p.SystemPatternErrors);
    }
}
