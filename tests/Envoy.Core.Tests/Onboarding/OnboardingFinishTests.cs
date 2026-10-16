using Envoy.Core.Onboarding;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Onboarding;

public class OnboardingFinishTests
{
    [Fact]
    public void Finish_sets_completed_and_first_run()
    {
        var s = new AppSettings();
        OnboardingOutcome.Apply(s, completed: true, OnboardingStep.Hotkeys);
        Assert.True(s.Onboarding.Completed);
        Assert.True(s.FirstRunCompleted);
        Assert.False(s.Onboarding.Skipped);
        Assert.Equal("Hotkeys", s.Onboarding.LastStep);
        Assert.False(OnboardingGate.ShouldAutoOpen(s));
    }

    [Fact]
    public void Skip_sets_skipped_and_keeps_last_step()
    {
        var s = new AppSettings();
        OnboardingOutcome.Apply(s, completed: false, OnboardingStep.ChatArea);
        Assert.True(s.Onboarding.Skipped);
        Assert.False(s.Onboarding.Completed);
        Assert.False(s.FirstRunCompleted);
        Assert.Equal(OnboardingStep.ChatArea, OnboardingGate.ResumeStep(s));
        Assert.False(OnboardingGate.ShouldAutoOpen(s));
    }
}
