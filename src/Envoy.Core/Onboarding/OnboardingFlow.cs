namespace Envoy.Core.Onboarding;

/// <summary>The setup guide's steps, in their fixed order (REQ-OUXA-1).</summary>
public enum OnboardingStep
{
    Language,
    Game,
    ChatArea,
    OcrLanguages,
    TestTranslation,
    Hotkeys,
}

/// <summary>How a step looks in the guide's step list (REQ-OUXA-11).</summary>
public enum StepStatus
{
    NotVisited,
    Done,
    NeedsAttention,
    Skipped,
}

/// <summary>What <see cref="OnboardingFlow.Evaluate"/> needs to know about the user's setup.</summary>
/// <param name="TestTranslationSucceeded">null = the test translation was not run.</param>
/// <param name="HotkeyProblems">Hotkeys that are taken or not a valid combination.</param>
public sealed record OnboardingFacts(bool PrivacyAcknowledged, DisplayModeVerdict DisplayMode, bool HasRegion,
                                     int MissingOcrCount, bool? TestTranslationSucceeded, int HotkeyProblems);

/// <summary>
/// Navigation through the setup guide's steps (REQ-OUXA-1). Pure and UI-thread only. Takes the list of built steps, so
/// dropping a SHOULD step only shortens the list.
/// </summary>
public sealed class OnboardingFlow
{
    /// <summary>Every step, in enum order (07a builds all six).</summary>
    public static IReadOnlyList<OnboardingStep> AllSteps { get; } = Enum.GetValues<OnboardingStep>();

    private readonly HashSet<OnboardingStep> _visited = [];
    private int _index;

    /// <param name="steps">The built steps; sorted into enum order and made distinct. Must not be empty.</param>
    /// <param name="start">The first current step; the first built step when it is not built.</param>
    public OnboardingFlow(IReadOnlyList<OnboardingStep> steps, OnboardingStep start = OnboardingStep.Language)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Steps = [.. steps.Distinct().Order()];
        if (Steps.Count == 0) throw new ArgumentException("The guide needs at least one step.", nameof(steps));
        _index = Math.Max(0, IndexOf(start));
        _visited.Add(Current);
    }

    public IReadOnlyList<OnboardingStep> Steps { get; }
    public OnboardingStep Current => Steps[_index];

    /// <summary>0-based position of <see cref="Current"/>.</summary>
    public int Index => _index;

    public int Count => Steps.Count;
    public bool CanGoBack => _index > 0;
    public bool IsLast => _index == Steps.Count - 1;

    /// <summary>Every step that has been current, including the start.</summary>
    public IReadOnlySet<OnboardingStep> Visited => _visited;

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Moves to the next step; false (and no change) on the last one.</summary>
    public bool Next()
    {
        if (IsLast) return false;
        MoveTo(_index + 1);
        return true;
    }

    /// <summary>Moves to the previous step; false (and no change) on the first one.</summary>
    public bool Back()
    {
        if (!CanGoBack) return false;
        MoveTo(_index - 1);
        return true;
    }

    /// <summary>Makes <paramref name="step"/> current; ignored when it is not built.</summary>
    public void GoTo(OnboardingStep step)
    {
        var i = IndexOf(step);
        if (i >= 0) MoveTo(i);
    }

    /// <summary>
    /// The step list's jump (spec 09a, REQ-HRD-12): like <see cref="GoTo(OnboardingStep)"/>, but a step after
    /// <see cref="OnboardingStep.Language"/> (which holds the privacy disclosure) is refused while the disclosure is not
    /// acknowledged, the same rule Next follows. True when <paramref name="step"/> is now current.
    /// </summary>
    public bool GoTo(OnboardingStep step, bool privacyAcknowledged)
    {
        if (!CanReach(step, privacyAcknowledged) || IndexOf(step) < 0) return false;
        GoTo(step);
        return true;
    }

    /// <summary>Language is always reachable; every later step needs the privacy acknowledgement.</summary>
    public static bool CanReach(OnboardingStep step, bool privacyAcknowledged) =>
        privacyAcknowledged || step <= OnboardingStep.Language;

    /// <summary>
    /// The step list's status (REQ-OUXA-11). NotVisited if not visited. NeedsAttention: Language → privacy not
    /// acknowledged; Game → likely exclusive fullscreen; ChatArea → no region; OcrLanguages → a pack missing;
    /// TestTranslation → failed; Hotkeys → a problem. Skipped: TestTranslation visited but never run. Otherwise Done.
    /// </summary>
    public static StepStatus Evaluate(OnboardingStep step, OnboardingFacts facts, bool visited)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!visited) return StepStatus.NotVisited;
        var attention = step switch
        {
            OnboardingStep.Language => !facts.PrivacyAcknowledged,
            OnboardingStep.Game => facts.DisplayMode == DisplayModeVerdict.LikelyExclusiveFullscreen,
            OnboardingStep.ChatArea => !facts.HasRegion,
            OnboardingStep.OcrLanguages => facts.MissingOcrCount > 0,
            OnboardingStep.TestTranslation => facts.TestTranslationSucceeded == false,
            OnboardingStep.Hotkeys => facts.HotkeyProblems > 0,
            _ => false,
        };
        if (attention) return StepStatus.NeedsAttention;
        if (step == OnboardingStep.TestTranslation && facts.TestTranslationSucceeded is null) return StepStatus.Skipped;
        return StepStatus.Done;
    }

    private int IndexOf(OnboardingStep step)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i] == step) return i;
        }
        return -1;
    }

    private void MoveTo(int index)
    {
        _visited.Add(Steps[index]);
        if (index == _index) return;
        _index = index;
        Changed?.Invoke();
    }
}
