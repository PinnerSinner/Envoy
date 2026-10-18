using Envoy.Core.Onboarding;
using Xunit;

namespace Envoy.Core.Tests.Onboarding;

public class OnboardingFlowTests
{
    [Fact]
    public void Next_and_back_walk_steps_in_order()
    {
        var flow = new OnboardingFlow(OnboardingFlow.AllSteps);
        Assert.Equal(OnboardingStep.Language, flow.Current);
        Assert.False(flow.CanGoBack);
        Assert.False(flow.Back());
        var seen = new List<OnboardingStep> { flow.Current };
        while (flow.Next()) seen.Add(flow.Current);
        Assert.Equal(Enum.GetValues<OnboardingStep>(), seen);
        Assert.True(flow.IsLast);
        Assert.Equal(5, flow.Index);
        Assert.False(flow.Next());
        Assert.True(flow.Back());
        Assert.Equal(OnboardingStep.TestTranslation, flow.Current);
        Assert.False(flow.IsLast);
    }

    [Fact]
    public void Count_follows_built_steps()
    {
        // A dropped SHOULD step only shortens the list; order is always the enum order.
        var flow = new OnboardingFlow([OnboardingStep.OcrLanguages, OnboardingStep.Language, OnboardingStep.ChatArea, OnboardingStep.Game, OnboardingStep.Language]);
        Assert.Equal(4, flow.Count);
        Assert.Equal([OnboardingStep.Language, OnboardingStep.Game, OnboardingStep.ChatArea, OnboardingStep.OcrLanguages], flow.Steps);
        Assert.Equal(6, new OnboardingFlow(OnboardingFlow.AllSteps).Count);
        Assert.Throws<ArgumentException>(() => new OnboardingFlow([]));
    }

    [Fact]
    public void Start_step_is_current_and_visited()
    {
        var flow = new OnboardingFlow(OnboardingFlow.AllSteps, OnboardingStep.Hotkeys);
        Assert.Equal(OnboardingStep.Hotkeys, flow.Current);
        Assert.True(flow.IsLast);
        Assert.Equal([OnboardingStep.Hotkeys], flow.Visited);
        // An unbuilt start falls back to the first step.
        Assert.Equal(OnboardingStep.Language, new OnboardingFlow([OnboardingStep.Language, OnboardingStep.Game], OnboardingStep.Hotkeys).Current);
    }

    [Fact]
    public void GoTo_marks_visited_and_raises_changed()
    {
        var flow = new OnboardingFlow(OnboardingFlow.AllSteps);
        var changes = 0;
        flow.Changed += () => changes++;
        flow.GoTo(OnboardingStep.OcrLanguages);
        Assert.Equal(OnboardingStep.OcrLanguages, flow.Current);
        Assert.Equal(3, flow.Index);
        Assert.Equal(1, changes);
        Assert.Contains(OnboardingStep.OcrLanguages, flow.Visited);
        Assert.Contains(OnboardingStep.Language, flow.Visited);
        Assert.DoesNotContain(OnboardingStep.Game, flow.Visited);
        flow.GoTo(OnboardingStep.OcrLanguages);
        Assert.Equal(1, changes); // no change, no event
    }

    [Fact]
    public void Step_list_cannot_skip_the_privacy_step_until_acknowledged()
    {
        var flow = new OnboardingFlow(OnboardingFlow.AllSteps);
        foreach (var step in OnboardingFlow.AllSteps.Where(s => s != OnboardingStep.Language))
        {
            Assert.False(flow.GoTo(step, privacyAcknowledged: false));
            Assert.Equal(OnboardingStep.Language, flow.Current);
            Assert.False(OnboardingFlow.CanReach(step, privacyAcknowledged: false));
        }
        Assert.True(OnboardingFlow.CanReach(OnboardingStep.Language, privacyAcknowledged: false));
        Assert.Equal([OnboardingStep.Language], flow.Visited);

        Assert.True(flow.GoTo(OnboardingStep.Hotkeys, privacyAcknowledged: true));
        Assert.Equal(OnboardingStep.Hotkeys, flow.Current);
        Assert.True(flow.GoTo(OnboardingStep.Language, privacyAcknowledged: false)); // going back to the disclosure is always fine
        Assert.Equal(OnboardingStep.Language, flow.Current);
    }

    [Fact]
    public void Step_list_jump_to_an_unbuilt_step_is_refused()
    {
        var flow = new OnboardingFlow([OnboardingStep.Language, OnboardingStep.Game]);
        Assert.False(flow.GoTo(OnboardingStep.Hotkeys, privacyAcknowledged: true));
        Assert.Equal(OnboardingStep.Language, flow.Current);
    }

    [Fact]
    public void GoTo_unknown_step_is_ignored()
    {
        var flow = new OnboardingFlow([OnboardingStep.Language, OnboardingStep.Game, OnboardingStep.ChatArea, OnboardingStep.OcrLanguages]);
        var changes = 0;
        flow.Changed += () => changes++;
        flow.GoTo(OnboardingStep.Hotkeys);
        Assert.Equal(OnboardingStep.Language, flow.Current);
        Assert.Equal(0, changes);
        Assert.DoesNotContain(OnboardingStep.Hotkeys, flow.Visited);
    }

    private static readonly OnboardingFacts Good = new(true, DisplayModeVerdict.Borderless, true, 0, true, 0);

    public static TheoryData<OnboardingStep, OnboardingFacts, bool, StepStatus> EvaluateRows => new()
    {
        { OnboardingStep.Language, Good, false, StepStatus.NotVisited },
        { OnboardingStep.Language, Good, true, StepStatus.Done },
        { OnboardingStep.Language, Good with { PrivacyAcknowledged = false }, true, StepStatus.NeedsAttention },
        { OnboardingStep.Language, Good with { PrivacyAcknowledged = false }, false, StepStatus.NotVisited },
        { OnboardingStep.Game, Good, true, StepStatus.Done },
        { OnboardingStep.Game, Good with { DisplayMode = DisplayModeVerdict.NotRunning }, true, StepStatus.Done },
        { OnboardingStep.Game, Good with { DisplayMode = DisplayModeVerdict.LikelyExclusiveFullscreen }, true, StepStatus.NeedsAttention },
        { OnboardingStep.ChatArea, Good, true, StepStatus.Done },
        { OnboardingStep.ChatArea, Good with { HasRegion = false }, true, StepStatus.NeedsAttention },
        { OnboardingStep.OcrLanguages, Good, true, StepStatus.Done },
        { OnboardingStep.OcrLanguages, Good with { MissingOcrCount = 2 }, true, StepStatus.NeedsAttention },
        { OnboardingStep.TestTranslation, Good, true, StepStatus.Done },
        { OnboardingStep.TestTranslation, Good with { TestTranslationSucceeded = false }, true, StepStatus.NeedsAttention },
        { OnboardingStep.TestTranslation, Good with { TestTranslationSucceeded = null }, true, StepStatus.Skipped },
        { OnboardingStep.TestTranslation, Good with { TestTranslationSucceeded = null }, false, StepStatus.NotVisited },
        { OnboardingStep.Hotkeys, Good, true, StepStatus.Done },
        { OnboardingStep.Hotkeys, Good with { HotkeyProblems = 1 }, true, StepStatus.NeedsAttention },
    };

    [Theory]
    [MemberData(nameof(EvaluateRows))]
    public void Evaluate_rules(OnboardingStep step, OnboardingFacts facts, bool visited, StepStatus expected) =>
        Assert.Equal(expected, OnboardingFlow.Evaluate(step, facts, visited));

    [Fact]
    public void Text_covers_every_step_and_status()
    {
        foreach (var step in OnboardingFlow.AllSteps) Assert.NotEmpty(OnboardingText.TitleOf(step));
        foreach (var status in Enum.GetValues<StepStatus>()) Assert.NotEmpty(OnboardingText.StatusOf(status));
        foreach (var verdict in Enum.GetValues<DisplayModeVerdict>()) Assert.NotEmpty(OnboardingText.Describe(verdict));
        Assert.Equal("Step 2 of 6", OnboardingText.Counter(1, 6));
        Assert.Equal("Chat area, needs attention", OnboardingText.StepItemName(OnboardingStep.ChatArea, StepStatus.NeedsAttention));
        Assert.Equal("This sends the sentence to DeepL.", OnboardingText.TestTranslationTarget("DeepL"));
        Assert.Equal(OnboardingText.AllDone, OnboardingText.Summary(0));
        Assert.Contains("2", OnboardingText.Summary(2), StringComparison.Ordinal);
    }
}
