using Envoy.Core.Capture;
using Envoy.Core.Text;

namespace Envoy.Core.Onboarding;

/// <summary>Setup-guide sentences built in code (contract §8.7). Never names game menu paths.</summary>
public static class OnboardingText
{
    /// <summary>{index} = 1-based step number; {count} = number of steps.</summary>
    public const string StepCounter = "Step {index} of {count}";

    public const string BorderlessAdvice = "Borderless looks the same as fullscreen and lets Envoy see the chat. Choose it in the game's display or graphics settings.";

    /// <summary>{provider} = the first provider's display name.</summary>
    public const string TestTranslationGoesTo = "This sends the sentence to {provider}.";

    public const string FinishSetup = "Finish setting up Envoy.";

    public const string FinishSetupDetail = "The setup guide checks your chat area, text recognition and hotkeys in a few steps.";

    public const string NextLabel = "Next";
    public const string FinishLabel = "Finish";

    public const string NotRunning = "No Age game is running. Start one to check how it is displayed.";
    public const string Windowed = "The game runs in a window. Envoy can read the chat.";
    public const string Borderless = "The game runs in borderless mode. Envoy can read the chat.";
    public const string Unknown = "Envoy can't tell how the game is displayed. If the chat area stays blank, switch to borderless.";

    public const string AllDone = "Everything looks ready.";

    /// <summary>{count} = steps that need attention.</summary>
    public const string NeedsAttentionSummary = "{count} step(s) still need attention. You can finish now and come back later.";

    public const string StatusNotVisited = "not visited";
    public const string StatusDone = "done";
    public const string StatusNeedsAttention = "needs attention";
    public const string StatusSkipped = "skipped";

    /// <summary>{title} = the step title; {status} = its status text. The step list's accessible name.</summary>
    public const string StepItemNameFormat = "{title}, {status}";

    public static string TitleOf(OnboardingStep step) => step switch
    {
        OnboardingStep.Language => "Language & privacy",
        OnboardingStep.Game => "Game & display",
        OnboardingStep.ChatArea => "Chat area",
        OnboardingStep.OcrLanguages => "Text recognition",
        OnboardingStep.TestTranslation => "Test translation",
        OnboardingStep.Hotkeys => "Hotkeys",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    public static string StatusOf(StepStatus status) => status switch
    {
        StepStatus.NotVisited => StatusNotVisited,
        StepStatus.Done => StatusDone,
        StepStatus.NeedsAttention => StatusNeedsAttention,
        StepStatus.Skipped => StatusSkipped,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>"Step 2 of 6".</summary>
    public static string Counter(int index, int count) =>
        TextTemplate.Format(StepCounter, null, ("index", index + 1), ("count", count));

    public static string StepItemName(OnboardingStep step, StepStatus status) =>
        TextTemplate.Format(StepItemNameFormat, null, ("title", TitleOf(step)), ("status", StatusOf(status)));

    /// <summary>The Game &amp; display step's advice; exclusive fullscreen reuses 05's <see cref="CaptureText.LikelyExclusiveFullscreen"/>.</summary>
    public static string Describe(DisplayModeVerdict verdict) => verdict switch
    {
        DisplayModeVerdict.NotRunning => NotRunning,
        DisplayModeVerdict.Windowed => Windowed,
        DisplayModeVerdict.Borderless => Borderless,
        DisplayModeVerdict.LikelyExclusiveFullscreen => CaptureText.LikelyExclusiveFullscreen,
        DisplayModeVerdict.Unknown => Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    public static string TestTranslationTarget(string providerName) =>
        TextTemplate.Format(TestTranslationGoesTo, null, ("provider", providerName));

    /// <summary><see cref="AllDone"/> when nothing needs attention, else <see cref="NeedsAttentionSummary"/>.</summary>
    public static string Summary(int needsAttention) => needsAttention <= 0
        ? AllDone
        : TextTemplate.Format(NeedsAttentionSummary, null, ("count", needsAttention));
}
