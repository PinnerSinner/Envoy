using Envoy.Core.Capture;
using Envoy.Core.Hotkeys;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class PageHintsTests
{
    public static TheoryData<CaptureConditionKind, PauseReason> AllConditions()
    {
        var data = new TheoryData<CaptureConditionKind, PauseReason>();
        foreach (var kind in Enum.GetValues<CaptureConditionKind>())
        {
            foreach (var reason in Enum.GetValues<PauseReason>()) data.Add(kind, reason);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllConditions))]
    public void Capture(CaptureConditionKind kind, PauseReason reason)
    {
        var condition = kind == CaptureConditionKind.Error ? new CaptureCondition(kind, reason, CaptureText.OcrTimedOut) : new CaptureCondition(kind, reason);
        var hint = PageHints.Capture(condition);
        switch (kind)
        {
            case CaptureConditionKind.Running or CaptureConditionKind.WaitingForGame:
                Assert.Null(hint);
                break;
            case CaptureConditionKind.Stopped:
                Assert.Equal(HintActions.ToggleCapture, hint!.ActionId);
                Assert.Equal(HintSeverity.Info, hint.Severity);
                break;
            case CaptureConditionKind.NoRegion:
                Assert.Equal(HintActions.SelectRegion, hint!.ActionId);
                Assert.Equal(HintSeverity.Warning, hint.Severity);
                break;
            case CaptureConditionKind.Error:
                Assert.Equal(HintSeverity.Error, hint!.Severity);
                Assert.Equal(CaptureText.OcrTimedOut, hint.Text);
                Assert.Null(hint.ActionId);
                break;
            case CaptureConditionKind.AutoPaused:
                // An automatic pause never offers Resume (ToggleCapture). Only an off-screen area offers "Select chat area
                // again"; a missing area maps to the NoRegion hint; waiting for a game shows nothing.
                Assert.NotEqual(HintActions.ToggleCapture, hint?.ActionId);
                switch (reason)
                {
                    case PauseReason.None or PauseReason.NoGame:
                        Assert.Null(hint);
                        break;
                    case PauseReason.RegionOffScreen:
                        Assert.Equal(HintActions.SelectRegion, hint!.ActionId);
                        Assert.Equal(PageHintText.SelectRegionAgainLabel, hint.ActionLabel);
                        Assert.Equal(CaptureText.Paused(reason), hint.Text);
                        break;
                    case PauseReason.NoRegion:
                        Assert.Equal(PageHints.Capture(new CaptureCondition(CaptureConditionKind.NoRegion)), hint);
                        break;
                    default:
                        Assert.Equal(HintSeverity.Info, hint!.Severity);
                        Assert.Equal(CaptureText.Paused(reason), hint.Text);
                        Assert.Null(hint.ActionId);
                        Assert.False(hint.HasAction);
                        break;
                }
                break;
        }
    }

    [Fact]
    public void Error_without_a_sentence_uses_the_generic_one() =>
        Assert.Equal(CaptureText.CaptureFailed, PageHints.Capture(new CaptureCondition(CaptureConditionKind.Error))!.Text);

    [Fact]
    public void Phrasebook()
    {
        Assert.Null(PageHints.Phrasebook(null));
        Assert.Null(PageHints.Phrasebook(""));
        var hint = PageHints.Phrasebook("it isn't valid JSON (line 3)")!;
        Assert.Equal(HintSeverity.Warning, hint.Severity);
        Assert.Contains("because it isn't valid JSON (line 3).", hint.Text, StringComparison.Ordinal);
        Assert.Equal(HintActions.EditPhrases, hint.ActionId);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Dispatches_and_players_empty_states(int count, bool shown)
    {
        Assert.Equal(shown, PageHints.Dispatches(count) is not null);
        Assert.Equal(shown, PageHints.Players(count) is not null);
    }

    [Theory]
    [InlineData(0, 0, HintSeverity.Error)]
    [InlineData(0, 3, HintSeverity.Error)]
    [InlineData(10, 2, HintSeverity.Warning)]
    [InlineData(10, 0, null)]
    public void OcrAvailability(int statuses, int missing, HintSeverity? expected) =>
        Assert.Equal(expected, PageHints.OcrAvailability(statuses, missing)?.Severity);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ProviderCredentials_hint(bool hasCredential, bool shown)
    {
        var hint = PageHints.ProviderCredentials("DeepL", hasCredential);
        Assert.Equal(shown, hint is not null);
        if (hint is not null) Assert.StartsWith("DeepL needs", hint.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProviderCatalog.GoogleFree, false)]
    [InlineData(ProviderCatalog.DeepL, true)]
    [InlineData(ProviderCatalog.Aws, true)]
    public void ProviderCredentials_IsSet(string id, bool needsSomething)
    {
        var t = new TranslationSettings();
        Assert.Equal(!needsSomething, Envoy.Core.Ux.ProviderCredentials.IsSet(id, t));
        t.DeepLKey = "k";
        t.AwsAccessKeyId = "a";
        t.AwsSecretAccessKey = "s";
        Assert.True(Envoy.Core.Ux.ProviderCredentials.IsSet(id, t));
    }

    [Theory]
    [InlineData(true, "Ctrl+Shift+O", false, false)]
    [InlineData(false, "Ctrl+Shift+O", true, true)]
    [InlineData(false, "", true, false)]
    public void OverlayHidden(bool visible, string hotkey, bool shown, bool namesHotkey)
    {
        var hint = PageHints.OverlayHidden(visible, hotkey);
        Assert.Equal(shown, hint is not null);
        if (hint is null) return;
        Assert.Equal(HintActions.ShowOverlay, hint.ActionId);
        Assert.Equal(namesHotkey, hint.Text.Contains("Ctrl+Shift+O", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HotkeyStatus.Registered, false)]
    [InlineData(HotkeyStatus.SkippedSafeMode, false)]
    [InlineData(HotkeyStatus.NotSet, true)]
    [InlineData(HotkeyStatus.Taken, true)]
    [InlineData(HotkeyStatus.Invalid, true)]
    public void ReplyHotkey(HotkeyStatus status, bool shown)
    {
        var hint = PageHints.ReplyHotkey(new HotkeyRegistrationResult("Compose", "Reply", "Ctrl+Shift+T", status));
        Assert.Equal(shown, hint is not null);
        if (hint is not null) Assert.Equal(HintActions.OpenHotkeys, hint.ActionId);
        Assert.Null(PageHints.ReplyHotkey(null));
    }

    [Theory]
    [InlineData(HotkeyStatus.Registered, null)]
    [InlineData(HotkeyStatus.SkippedSafeMode, null)]
    [InlineData(HotkeyStatus.NotSet, HintSeverity.Info)]
    [InlineData(HotkeyStatus.Taken, HintSeverity.Warning)]
    [InlineData(HotkeyStatus.Invalid, HintSeverity.Warning)]
    public void Hotkeys(HotkeyStatus status, HintSeverity? expected)
    {
        var results = new[] { new HotkeyRegistrationResult("Compose", "Reply", "Ctrl+Shift+T", status), new HotkeyRegistrationResult("ToggleOverlay", "Show/hide overlay", "Ctrl+Shift+O", HotkeyStatus.Registered) };
        var hint = PageHints.Hotkeys(results);
        Assert.Equal(expected, hint?.Severity);
        if (status == HotkeyStatus.NotSet) Assert.Contains("Reply", hint!.Text, StringComparison.Ordinal);
    }
}
