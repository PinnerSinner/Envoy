using Envoy.Core.Settings;

namespace Envoy.Core.Onboarding;

/// <summary>When the setup guide opens by itself, and where it resumes (REQ-OUXA-2, 12).</summary>
public static class OnboardingGate
{
    /// <summary>
    /// Not completed, not skipped, and not an existing user (<c>FirstRunCompleted</c> with at least one saved region in
    /// <c>Capture.Regions</c> or <c>Capture.WindowRegions</c>).
    /// </summary>
    public static bool ShouldAutoOpen(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return !s.Onboarding.Completed && !s.Onboarding.Skipped
            && !(s.FirstRunCompleted && (s.Capture.Regions.Count > 0 || s.Capture.WindowRegions.Count > 0));
    }

    /// <summary>Case-insensitive step name (a saved <c>LastStep</c> or an <c>IShell</c> step id); anything else → Language.</summary>
    public static OnboardingStep ParseStep(string? stepId)
    {
        var name = stepId?.Trim();
        foreach (var step in OnboardingFlow.AllSteps)
        {
            if (string.Equals(step.ToString(), name, StringComparison.OrdinalIgnoreCase)) return step;
        }
        return OnboardingStep.Language;
    }

    /// <summary>The step the guide resumes at: <c>Onboarding.LastStep</c>.</summary>
    public static OnboardingStep ResumeStep(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return ParseStep(s.Onboarding.LastStep);
    }

    /// <summary>Contract §4 step 11: never in safe mode, never with <c>--minimized</c>; otherwise <see cref="ShouldAutoOpen"/>.</summary>
    public static bool ShouldAutoOpenAtStartup(bool safeMode, bool minimized, AppSettings s) =>
        !safeMode && !minimized && ShouldAutoOpen(s);
}

/// <summary>What closing the guide records (REQ-OUXA-10). Pure, so Finish and Skip are unit-tested.</summary>
public static class OnboardingOutcome
{
    /// <summary>
    /// <paramref name="completed"/> → <c>Completed</c> and <c>FirstRunCompleted</c>; otherwise <c>Skipped</c>. Both set
    /// <c>LastStep</c> to <paramref name="lastStep"/>.
    /// </summary>
    public static void Apply(AppSettings s, bool completed, OnboardingStep lastStep)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (completed)
        {
            s.Onboarding.Completed = true;
            s.FirstRunCompleted = true;
        }
        else
        {
            s.Onboarding.Skipped = true;
        }
        s.Onboarding.LastStep = lastStep.ToString();
    }
}
