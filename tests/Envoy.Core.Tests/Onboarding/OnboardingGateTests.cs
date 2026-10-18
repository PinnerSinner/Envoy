using Envoy.Core.Onboarding;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Onboarding;

public class OnboardingGateTests
{
    [Fact]
    public void Opens_for_fresh_settings() => Assert.True(OnboardingGate.ShouldAutoOpen(new AppSettings()));

    [Fact]
    public void Opens_after_first_run_without_a_region()
    {
        var s = new AppSettings { FirstRunCompleted = true };
        Assert.True(OnboardingGate.ShouldAutoOpen(s));
    }

    [Fact]
    public void Skips_existing_users_with_region()
    {
        var s = new AppSettings { FirstRunCompleted = true };
        s.Capture.Regions["any"] = new ScreenRegion(0, 0, 100, 50);
        Assert.False(OnboardingGate.ShouldAutoOpen(s));
        s.FirstRunCompleted = false; // a region alone (no completed first run) still opens the guide
        Assert.True(OnboardingGate.ShouldAutoOpen(s));
    }

    [Fact]
    public void Skips_existing_users_with_window_region()
    {
        var s = new AppSettings { FirstRunCompleted = true };
        s.Capture.WindowRegions["aoe2de"] = new WindowRelativeRegion(10, 10, 100, 50, 1920, 1080);
        Assert.False(OnboardingGate.ShouldAutoOpen(s));
    }

    [Fact]
    public void Never_reopens_after_completed_or_skipped()
    {
        var completed = new AppSettings();
        completed.Onboarding.Completed = true;
        Assert.False(OnboardingGate.ShouldAutoOpen(completed));
        var skipped = new AppSettings();
        skipped.Onboarding.Skipped = true;
        Assert.False(OnboardingGate.ShouldAutoOpen(skipped));
    }

    [Fact]
    public void Startup_never_opens_in_safe_mode()
    {
        Assert.False(OnboardingGate.ShouldAutoOpenAtStartup(safeMode: true, minimized: false, new AppSettings()));
        Assert.True(OnboardingGate.ShouldAutoOpenAtStartup(safeMode: false, minimized: false, new AppSettings()));
    }

    [Fact]
    public void Startup_never_opens_when_minimized() =>
        Assert.False(OnboardingGate.ShouldAutoOpenAtStartup(safeMode: false, minimized: true, new AppSettings()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nonsense")]
    [InlineData("1")]
    [InlineData("Language,Game")]
    public void ParseStep_defaults_to_language(string? id) => Assert.Equal(OnboardingStep.Language, OnboardingGate.ParseStep(id));

    [Theory]
    [InlineData("Hotkeys", OnboardingStep.Hotkeys)]
    [InlineData("hotkeys", OnboardingStep.Hotkeys)]
    [InlineData(" OcrLanguages ", OnboardingStep.OcrLanguages)]
    [InlineData("chatarea", OnboardingStep.ChatArea)]
    public void ParseStep_reads_names_case_insensitively(string id, OnboardingStep expected) =>
        Assert.Equal(expected, OnboardingGate.ParseStep(id));

    [Theory]
    [InlineData("Hotkeys", false, OnboardingStep.Language)]
    [InlineData("ChatArea", false, OnboardingStep.Language)]
    [InlineData("Language", false, OnboardingStep.Language)]
    [InlineData(null, false, OnboardingStep.Language)]
    [InlineData("Hotkeys", true, OnboardingStep.Hotkeys)]
    [InlineData("chatarea", true, OnboardingStep.ChatArea)]
    [InlineData("nope", true, OnboardingStep.Language)]
    public void StartStep_stays_on_the_privacy_step_until_acknowledged(string? id, bool acknowledged, OnboardingStep expected) =>
        Assert.Equal(expected, OnboardingGate.StartStep(id, acknowledged));

    [Fact]
    public void ResumeStep_uses_last_step()
    {
        var s = new AppSettings();
        Assert.Equal(OnboardingStep.Language, OnboardingGate.ResumeStep(s));
        s.Onboarding.LastStep = nameof(OnboardingStep.OcrLanguages);
        Assert.Equal(OnboardingStep.OcrLanguages, OnboardingGate.ResumeStep(s));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)] // Skip or close on the auto-opened guide: Envoy is never left invisible
    [InlineData(false, false, false)] // the guide opened from the tray or About: closing it leaves things as they were
    public void War_room_opens_after_the_guide_closes(bool completed, bool autoOpened, bool expected) =>
        Assert.Equal(expected, OnboardingGate.OpensWarRoomAfterClose(completed, autoOpened));
}
