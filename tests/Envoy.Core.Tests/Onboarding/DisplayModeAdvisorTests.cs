using Envoy.Core.Capture;
using Envoy.Core.Onboarding;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Onboarding;

public class DisplayModeAdvisorTests
{
    private static readonly ScreenRegion Monitor = new(0, 0, 1920, 1080);

    private static GameWindowInfo Window(ScreenRegion rect, bool caption = false, bool minimized = false, bool foreground = true, int streak = 0) =>
        new(1, rect, rect, minimized, foreground, false, caption, Monitor, streak);

    public static TheoryData<string, GameWindowInfo?, DisplayModeVerdict> Rows => new()
    {
        { "no game", null, DisplayModeVerdict.NotRunning },
        { "uniform streak", Window(Monitor, streak: BlankFrames.Threshold), DisplayModeVerdict.LikelyExclusiveFullscreen },
        { "uniform streak below threshold", Window(Monitor, streak: BlankFrames.Threshold - 1), DisplayModeVerdict.Borderless },
        { "minimised in background", Window(Monitor, minimized: true, foreground: false), DisplayModeVerdict.LikelyExclusiveFullscreen },
        { "minimised but in front", Window(Monitor, minimized: true, foreground: true), DisplayModeVerdict.Borderless },
        { "caption", Window(new ScreenRegion(100, 100, 1280, 720), caption: true), DisplayModeVerdict.Windowed },
        { "covers monitor", Window(Monitor), DisplayModeVerdict.Borderless },
        { "covers monitor within 2 px", Window(new ScreenRegion(-2, 2, 1922, 1076)), DisplayModeVerdict.Borderless },
        { "3 px short", Window(new ScreenRegion(0, 0, 1917, 1080)), DisplayModeVerdict.Unknown },
        { "partial captionless", Window(new ScreenRegion(200, 100, 1280, 720)), DisplayModeVerdict.Unknown },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Classify(string row, GameWindowInfo? window, DisplayModeVerdict expected) =>
        Assert.True(expected == DisplayModeAdvisor.Classify(window), row);

    [Fact]
    public void Exclusive_fullscreen_advice_is_05s_text() =>
        Assert.Equal(CaptureText.LikelyExclusiveFullscreen, OnboardingText.Describe(DisplayModeVerdict.LikelyExclusiveFullscreen));
}
