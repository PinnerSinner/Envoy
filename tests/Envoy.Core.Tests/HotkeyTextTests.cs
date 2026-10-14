using Envoy.Core.Hotkeys;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests;

public class HotkeyTextTests
{
    private static HotkeyRegistrationResult R(string id, string combo, HotkeyStatus status) =>
        new(id, HotkeyText.ActionName(id), combo, status);

    [Fact]
    public void StatusLine_SafeMode_SaysHotkeysAreOff()
    {
        var safe = new[] { R("Compose", "Ctrl+Shift+T", HotkeyStatus.SkippedSafeMode), R("ToggleOverlay", "Ctrl+Shift+O", HotkeyStatus.SkippedSafeMode) };
        Assert.Equal(Envoy.Core.Diagnostics.SafeModeText.HotkeysOff, HotkeyText.StatusLine(safe));
        Assert.Equal("", HotkeyText.TakenSummary(safe));

        var taken = new[] { R("Compose", "Ctrl+Shift+T", HotkeyStatus.Taken) };
        Assert.Equal(HotkeyText.TakenSummary(taken), HotkeyText.StatusLine(taken));
        Assert.Equal("", HotkeyText.StatusLine([]));
    }

    [Fact]
    public void TakenSummary_AllRegisteredOrNotSet_Empty()
    {
        var results = new[]
        {
            R("Compose", "Ctrl+Shift+T", HotkeyStatus.Registered),
            R("ToggleOverlay", "", HotkeyStatus.NotSet),
        };
        Assert.Equal("", HotkeyText.TakenSummary(results));
        Assert.Equal("", HotkeyText.TakenSummary([]));
    }

    [Fact]
    public void TakenSummary_MatchesLegacyText()
    {
        var results = new[] { R("Compose", "Ctrl+Shift+T", HotkeyStatus.Taken), R("ToggleCapture", "Ctrl+Shift+P", HotkeyStatus.Registered) };
        Assert.Equal("These hotkeys are taken by another app: Reply (Ctrl+Shift+T)", HotkeyText.TakenSummary(results));
    }

    [Fact]
    public void TakenSummary_IncludesInvalid_InListOrder()
    {
        var results = new[]
        {
            R("Compose", "Ctrl+Shift+T", HotkeyStatus.Registered),
            R("SelectRegion", "Ctrl+Bogus", HotkeyStatus.Invalid),
            R("OverlayEditMode", "", HotkeyStatus.NotSet),
            R("ScanAndTranslate", "Ctrl+Shift+Q", HotkeyStatus.Taken),
        };
        Assert.Equal("These hotkeys are taken by another app: Select chat area (Ctrl+Bogus), Scan & translate (Ctrl+Shift+Q)",
            HotkeyText.TakenSummary(results));
    }

    [Fact]
    public void ActionName_CoversEveryHotkeySettingsProperty()
    {
        var ids = typeof(HotkeySettings).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToList();
        Assert.NotEmpty(ids);
        foreach (var id in ids)
            Assert.False(string.IsNullOrWhiteSpace(HotkeyText.ActionName(id)), id);
    }

    [Fact]
    public void ActionName_MatchesLegacyNames()
    {
        Assert.Equal("Reply", HotkeyText.ActionName(nameof(HotkeySettings.Compose)));
        Assert.Equal("Show/hide overlay", HotkeyText.ActionName(nameof(HotkeySettings.ToggleOverlay)));
        Assert.Equal("Pause/resume", HotkeyText.ActionName(nameof(HotkeySettings.ToggleCapture)));
        Assert.Equal("Select chat area", HotkeyText.ActionName(nameof(HotkeySettings.SelectRegion)));
        Assert.Equal("Move overlay", HotkeyText.ActionName(nameof(HotkeySettings.OverlayEditMode)));
        Assert.Equal("Scan & translate", HotkeyText.ActionName(nameof(HotkeySettings.ScanAndTranslate)));
    }

    [Fact]
    public void ActionName_Unknown_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HotkeyText.ActionName("Nope"));
    }
}
