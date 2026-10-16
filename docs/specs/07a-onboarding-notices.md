# Onboarding and notices: setup guide, privacy disclosure, advisory notices, War Room layout

Status: In progress — revised against `00-integration.md` (binding). Lands as step 8, after 01, 08a, 02, 03, 05, 04 and
06.

Area id: `07a-onboarding-notices` · Requirement prefix: `OUXA` · Split from `07-onboarding-ux-accessibility.md`.
Requirement numbers keep the original `REQ-OUX-n` numbering (contract preamble), so `REQ-OUXA-13` is the old `REQ-OUX-13`
and the contract §9 budget row "07a" maps directly. The other parts of the old spec are `07b-accessibility.md`
(focus, names, contrast, high contrast) and `07c-reply-overlay-polish.md` (reply box and overlay).

## Context

### What exists today

| Topic | Today | Where |
| --- | --- | --- |
| First run | `App.OnStartup` calls `ShowMain()` when `!Settings.FirstRunCompleted`. The War Room has a static "Get ready" card with four numbered rows: select the chat area, choose a language, enter your in-game name, and a plain-text reminder to use borderless. `MainWindow.OnClosed` sets `FirstRunCompleted = true`. Nothing checks OCR packs, tests the translator or teaches the hotkeys, and nothing can be reopened later. | `App.xaml.cs`, War Room XAML (`Pages/WarRoomPage.xaml` after 01) |
| Privacy | The default provider is Google's unofficial keyless endpoint (`translate_a/single?client=gtx`). Chat is sent there from the first captured line. Nothing tells a new user that their chat leaves the PC. `docs/faq.md` has "What data leaves my PC?", but the app never shows it. | `GoogleFreeTranslator.cs`, `TranslationSettings.Primary` |
| OCR packs | `OcrService.AllLanguageStatuses()` lists installed and missing packs. The Reading Chat page has an Install button per language (01: `InstallOcrCommand` → `IShell.InstallOcrPack` → 08a `ShellLauncher.InstallOcrPack`). A missing pack is visible only on that page. The default `Capture.OcrLanguages` is `zh-CN, ko, ja, ru`, so most new users are missing packs without knowing it. | `Services/OcrService.cs`, `MainViewModel.Capture.cs` |
| Notifications | The tray `NotifyIcon` has a context menu only, no balloons. Provider health is on the Translation page only (03). Updates show in 06's War Room banner. 02 adds problem toasts and the one `NotificationThrottle`. | `App.xaml.cs` (`CreateTray`), 02, 03, 06 |
| Empty states | War Room "Players this match" and "Dispatches" render as empty boxes. A missing chat area shows only as caption text. When `phrases.json` fails to parse, `PhraseBook.LoadWithUserFile` silently uses built-ins (08a adds a 1 MB cap that takes the same silent path). | War Room page, `Phrases/PhraseBook.cs` |

### What earlier steps deliver (contract §1)

- **01:** `Pages/*` and `PageIds` (`Dashboard` is the War Room, `Capture` is Reading Chat), `MainViewModel.*` partials,
  `Envoy.Core.Mvvm`, `IShell` (01 block), `DialogService`, `SettingsAutoApplier`, `CapturePreviewViewModel` with the
  preview lease, `TranslationTestText`, `AppHost.HotkeyResults` (`HotkeyRegistrationResult`, `HotkeyStatus`), and the
  three-phase smoke test. `CLAUDE.md` already requires `AutomationProperties.Name` on every new interactive control.
- **08a:** `ShellLauncher` (`TryOpen`, `InstallOcrPack`), `PhraseLimits` and its 1 MB check in `PhraseBook`,
  `policy.json` (native and network allowlists), analyzers.
- **02:** `ILog`/`LogHub`, `AppHostOptions`, instance `DataFolder`, `NotificationThrottle` on `AppHost.Notifications`
  (`TryShowBalloon(key)`), `ToastService`, safe mode and `StartupPlan`, the `OnStartup`/`ExitApp` skeleton with
  `[07a …]` slots, `DataInventory`, `docs/privacy.md` with its "What Envoy stores on your PC" section, the About page
  Troubleshooting section.
- **03:** `TranslationRuntime`, `AppHost.TranslationHealth`, `AppHost.ProviderHealthChanged(ProviderHealthSnapshot)`
  on the UI thread, `ProviderState`, `TranslationErrorKind.Configuration` (not counted by the breaker).
- **05:** `AppHost.GameWindow` (`GameWindowInfo`) and `GameWindowChanged` on the UI thread, `WatcherState.Paused`,
  `PauseReason`, `CaptureText` (including `Paused(reason)` and `LikelyExclusiveFullscreen`), `Capture.WindowRegions`.
- **04:** nothing this area consumes directly.
- **06:** `UpdateCoordinator` (`Host.Updates`), `UpdateView` (`Headline`, `Primary`) rebuilt on the UI thread by
  `AppHost.RefreshUpdateView`, the War Room update banner, `Services/UpdateActions.Run(UpdateAction, AppHost, IShell,
  ILog)`, `IShell.RestartToUpdate`, `AppHost.AppVersion`. 06 fixes the meaning of `Updates.LastAnnouncedVersion` but,
  under the arrival rule, leaves adding the property to this step.

### What is weak or missing for production

1. A stranger who downloads Envoy lands on a dense settings window. The two most common silent failures (a missing OCR
   pack for the opponent's script, and exclusive fullscreen) are neither detected nor explained.
2. Chat text goes to a third party by default with no disclosure. For software given to strangers, this is the most
   serious gap (critique "missing" item 1; contract §8.1).
3. Problems that appear later (an OCR pack still missing, a provider failing, a new version) are visible only on pages
   a player won't open mid-game.
4. The War Room collects banners and callouts from four areas with no agreed order (contract §8.6).

## Goals / Non-goals

**Goals**

- A reopenable **setup guide** window that opens automatically for new users only. Its first step shows **what leaves
  your PC**, and translation stays off until the user acknowledges it.
- **Advisory notices** for three events (OCR pack missing, provider failing, update available), shown as tray balloons
  through 02's single throttle with contract §2.2's in-game policy, and listed in the War Room.
- **One War Room layout** (contract §8.6) and the Appearance and About section orders.
- `docs/privacy.md` data flows and a FAQ entry on ranked and anti-cheat games.
- All decisions in `Envoy.Core` with unit tests. Every new window and state has a smoke section placed per contract §6.

**Non-goals**

- Accessibility work (focus ring, names lint, contrast, high contrast). That is 07b.
- Reply box and overlay polish. That is 07c.
- Problem toasts, the throttle itself, and the safe-mode banner. Those are 02. This area adds **advisories** only.
- Update download, install and the update banner (06). This area only *announces* an update in a balloon.
- Native Windows toasts (AppUserModelID, Start-menu shortcut). `NotifyIcon.ShowBalloonTip` shows as a toast on
  Windows 10/11 without them.
- Localisation. Strings live in Core `*Text` classes so it is possible later (contract §8.7).
- Any new P/Invoke or game-window probing. Display-mode facts come from 05's `AppHost.GameWindow`.

## Requirements

### Setup guide

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUXA-1 | MUST | `SetupWizardWindow` presents the steps of `OnboardingFlow.Steps` in fixed order: Language & privacy, Game & display, Chat area, Text recognition, then (when built) Test translation and Hotkeys. Back, Next (Finish on the last step) and Skip setup are always visible, with a "Step n of N" indicator where N is `Steps.Count`. Every edit in the guide writes the same `AppSettings` properties the settings pages use, through a `SettingsAutoApplier` into `AppHost.ApplySettings()`. The Game & display step shows the detected game and the static borderless advice. *(Absorbs REQ-OUX-4.)* |
| REQ-OUXA-2 | MUST | At start-up the guide opens instead of `MainWindow` only when `OnboardingGate.ShouldAutoOpen(settings)` is true (not completed, not skipped, and not an existing user: `FirstRunCompleted` with at least one saved region in `Capture.Regions` or `Capture.WindowRegions`) **and** the contract §4 step 11 window choice reaches the guide branch: never in safe mode, never with `--minimized`. |
| REQ-OUXA-3 | MUST | The guide can be reopened from a tray item "Setup guide…", an About-page "Setup guide" section button, and the War Room setup callout. `IShell.OpenSetupWizard(string? stepId = null)` is single-instance: a second call activates the open window and moves to `stepId` if given. |
| REQ-OUXA-4 | — | Folded into REQ-OUXA-1. |
| REQ-OUXA-5 | SHOULD | The Game & display step shows `DisplayModeAdvisor.Classify(Host.GameWindow)` (NotRunning, Windowed, Borderless, LikelyExclusiveFullscreen, Unknown) with its advice and a "Check again" button, refreshed on `GameWindowChanged`. It never blocks progress. The exclusive-fullscreen advice is 05's `CaptureText.LikelyExclusiveFullscreen`. |
| REQ-OUXA-6 | MUST | The Chat area step runs `IShell.SelectRegion()` (the guide hides while selecting and comes back) and shows the region text and a live preview from its **own** `CapturePreviewViewModel`, whose `IsActive` is true only while this step is current. |
| REQ-OUXA-7 | MUST | The Text recognition step lists `OcrReadiness.Recommend(...)`: your language first, then wanted and common opponent languages, each Installed or Missing. Each missing one has an Install button that calls `IShell.InstallOcrPack(code)`. "Check again" re-queries Windows and calls `ApplySettings()`. |
| REQ-OUXA-8 | SHOULD | A Test translation step translates `TranslationTestText.SampleInput` (editable) through `Host.Translator`. Before the user presses Translate it names the provider the text will go to; afterwards it shows the result or 03's error text. |
| REQ-OUXA-9 | SHOULD | A Hotkeys step shows `HotkeyCheatSheet.Build(Host.HotkeyResults)`: every action with its combination or its status text (Not set, Taken by another app, Not a valid combination, Off in safe mode). A tray item "Hotkey cheat sheet…" opens the guide at this step. |
| REQ-OUXA-10 | MUST | Finish sets `Onboarding.Completed = true` and `FirstRunCompleted = true`, saves, closes the guide and opens `MainWindow` on the War Room. Skip setup, Esc or the window's close button sets `Onboarding.Skipped = true` and keeps `Onboarding.LastStep`. |
| REQ-OUXA-11 | SHOULD | The step list shows each step's `StepStatus` (Not visited, Done, Needs attention, Skipped) from `OnboardingFlow.Evaluate`. The last step summarises steps that need attention, with a "Go to step" link for each. |
| REQ-OUXA-12 | SHOULD | The War Room setup callout (REQ-OUXA-48) reopens the guide at `Onboarding.LastStep` rather than the first step. |

### Privacy disclosure (contract §8.1)

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUXA-47 | MUST | The guide's Language & privacy step and the War Room setup callout show "What leaves your PC" from `PrivacyText`: chat lines and replies go to the chosen provider chain (by name; the default is Google's unofficial keyless endpoint), update checks go to GitHub, and OCR and speech stay on the PC. Next on that step is enabled only after "I understand" is ticked, which sets `Onboarding.PrivacyAcknowledgedVersion = PrivacyText.Version`. Until `PrivacyAcknowledgedVersion >= PrivacyText.Version`, `AppHost` sends no text to any provider: `PrivacyGatedTranslator` wraps the chat and compose translators outermost and fails every call with `TranslationErrorKind.Configuration` and `PrivacyText.TranslationOff` before any inner translator runs. `docs/privacy.md` gains the data-flow sections, and `docs/faq.md` gains "Is Envoy allowed in ranked/anti-cheat games?" (docs only, no claim of approval). |

### Notices

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUXA-13 | MUST | `NoticeCenter` (Core, UI thread only) decides Show, Defer or Suppress for `OcrLanguageMissing` (key `ocr-missing:<sorted codes>`), `ProviderFailing` (key `provider:<id>`) and `UpdateAvailable` (key `update:<version>`) with the contract §2.2 policy: it asks 02's `NotificationThrottle.TryShowBalloon` before every balloon and keeps no counters of its own; while `GameWindow.GameForeground` is true, or while `Hold(true)` is set (the guide is open), every advisory is deferred (one per key, latest wins), never dropped; a refused throttle also defers; `TakeDeferred` re-offers on `GameWindowChanged`, on game exit (`RefreshGame` position 4), on `Hold(false)` and on a 5 s timer. `Notifications.Enabled = false` (an Appearance-page checkbox) stops balloons but keeps the War Room list. A key already announced and still active is not ballooned again until it is resolved. In safe mode `NoticeCenter` and `TrayNotifier` are not created. *(Absorbs REQ-OUX-14 and 21.)* |
| REQ-OUXA-14 | — | Folded into REQ-OUXA-13 (the throttle owns the limits). |
| REQ-OUXA-15 | — | Removed. 03's `ProviderHealthRegistry` is the single provider-health source (contract §2.1). |
| REQ-OUXA-16 | MUST | Notice text comes only from `NoticeText` and 06's `UpdateView.Headline`. It never contains chat text, API keys or exception messages. It names the provider or the languages, says what Envoy does now, and says where to look. |
| REQ-OUXA-17 | MUST | Sources and actions: `ProviderFailing` is submitted when a `ProviderHealthChanged` snapshot's state becomes `AuthFailed`, `QuotaExceeded` or `CoolingDown`, and resolved when it returns to `Healthy`; its click opens the Translation page. `OcrLanguageMissing` is computed at `ApplySettings` position 8 and in `Start()`; installing a pack resolves the old key; its click opens the Reading Chat page. `UpdateAvailable` is submitted when 06's `UpdateCoordinator` state becomes `Available` or `ReadyToRestart`, unless its version equals `Updates.LastAnnouncedVersion`; its text is `UpdateView.Headline`; showing it sets `LastAnnouncedVersion`; it is balloon-only (the War Room shows updates through 06's banner); its click opens the War Room and runs `UpdateView.Primary` through 06's `UpdateActions.Run` (Restart to update for an installed copy, otherwise the release page or download). |
| REQ-OUXA-18 | SHOULD | One checkbox per kind (`Notifications.OcrLanguageMissing`, `ProviderFailing`, `UpdateAvailable`; all default on) under the master switch. Turning a kind off stops its balloons but keeps it in the War Room list. |
| REQ-OUXA-19 | — | Removed. The in-game policy is fixed by contract §2.2; there is no `QuietWhileGameFocused` setting. |
| REQ-OUXA-20 | SHOULD | Each War Room notice row has a "Don't remind me" button that adds the key to `Notifications.Dismissed`. A dismissed key is never shown or listed again. |
| REQ-OUXA-21 | — | Folded into REQ-OUXA-13 (`Hold` while the guide is open). |

### Layout, empty and error states

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUXA-22 | SHOULD | Every settings page other than the War Room shows one themed `Callout` (Info, Warning, Error) for its empty or error condition, with text and an optional action from `PageHints`. Translation: credentials missing for a used provider. Reading Chat: no region, OCR unavailable, no preview frame yet. Overlay: overlay hidden. Replying: no reply hotkey or it is taken. Phrasebook: `phrases.json` unreadable. Hotkeys: unset or taken combinations. Appearance and About: none. The War Room "Players" and "Dispatches" frames show their empty-state text. |
| REQ-OUXA-23 | MUST | `PhraseBook.LoadWithUserFile(string userFile, out string? error)` reports a user-file problem (invalid JSON, or 08a's "larger than 1 MB") instead of swallowing it. The one-argument overload keeps today's behaviour. `AppHost.PhrasesLoadError` holds the last error and the Phrasebook page shows it (through `PageHints.Phrasebook`, which this requirement makes MUST for that page only). |
| REQ-OUXA-48 | MUST | War Room order, top to bottom (contract §8.6): 02's safe-mode banner (MainWindow-level), 06's update banner, the notice list (at most 3, then "Show all (n)"), the hint slot, the existing content. Each slot shows at most one item except the notice list. The hint slot shows, in priority order: the privacy callout while unacknowledged; the "Finish setting up Envoy" callout while `Onboarding.Completed` is false; the War Room capture hint from `PageHints.Capture`. `PageHints.Capture` maps 05's `WatcherState.Paused` to `CaptureCondition.AutoPaused(reason)`, which shows `CaptureText.Paused(reason)` with **no** Resume action, except "Select chat area again" for `RegionOffScreen`. Appearance is ordered Theme → Accessibility (07b) → Notifications → Startup & updates (06). About is ordered Version & updates (06) → Setup guide → Troubleshooting (02) → Privacy → licences. |

**MUST count: 12** (REQ-OUXA-1, 2, 3, 6, 7, 10, 13, 16, 17, 23, 47, 48), matching contract §9.

## Design

### Settings (`src/Envoy.Core/Settings/AppSettings.cs`, shared file)

```csharp
public sealed class AppSettings
{
    // … existing …
    public OnboardingSettings Onboarding { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
}

public sealed class OnboardingSettings
{
    public bool Completed { get; set; }                       // false
    public bool Skipped { get; set; }                         // false
    /// <summary>OnboardingStep name to resume at; "" = start.</summary>
    public string LastStep { get; set; } = "";
    /// <summary>PrivacyText.Version the user acknowledged; 0 = never. Translation is off below PrivacyText.Version.</summary>
    public int PrivacyAcknowledgedVersion { get; set; }
}

// UpdateSettings (06's class) addition — 06 fixed its meaning; 07a adds it because the balloon is its only user.
/// <summary>Version last announced in a balloon ("" = none). Only stops a second balloon for the same version.</summary>
public string LastAnnouncedVersion { get; set; } = "";

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool OcrLanguageMissing { get; set; } = true;      // SHOULD (REQ-OUXA-18)
    public bool ProviderFailing { get; set; } = true;         // SHOULD
    public bool UpdateAvailable { get; set; } = true;         // SHOULD
    /// <summary>Notice keys the user chose "Don't remind me" for (SHOULD, REQ-OUXA-20). Keys hold codes and ids only.</summary>
    public List<string> Dismissed { get; set; } = [];
}
```

Contract §7 rules:

- Additive with defaults. **One deliberate exception:** `PrivacyAcknowledgedVersion = 0` turns translation off for
  every existing settings file until the user acknowledges. There is no installed base (06 found no releases), and
  the overlay and War Room both say why (REQ-OUXA-47). `AppSettings.CurrentVersion` stays 1.
- No new property holds names, chat text or positions, so no `[Personal]`/`[Location]` marker is needed. `Dismissed`
  holds keys such as `ocr-missing:ja,ko` and `provider:deepl`.
- No new file in the data folder, so `DataInventory` is unchanged.
- The update "announced" field is `Updates.LastAnnouncedVersion`, in 06's `UpdateSettings` with 06's semantics; this
  step adds the property (06 §Settings) and is its only reader and writer (UI thread). The old
  `Notifications.LastAnnouncedUpdate` and `QuietWhileGameFocused` are gone.
- Background code never reads these settings. The privacy flag reaches the translator through a volatile field
  published at `ApplySettings` position 2 (see AppHost below).

### Core: onboarding (`src/Envoy.Core/Onboarding/`, new)

```csharp
namespace Envoy.Core.Onboarding;

public enum OnboardingStep { Language, Game, ChatArea, OcrLanguages, TestTranslation, Hotkeys }
public enum StepStatus { NotVisited, Done, NeedsAttention, Skipped }

public sealed record OnboardingFacts(bool PrivacyAcknowledged, DisplayModeVerdict DisplayMode, bool HasRegion,
                                     int MissingOcrCount, bool? TestTranslationSucceeded, int HotkeyProblems);

public sealed class OnboardingFlow
{
    /// steps = the built steps in enum order. 07a builds all six; dropping a SHOULD step only shortens the list.
    public OnboardingFlow(IReadOnlyList<OnboardingStep> steps, OnboardingStep start = OnboardingStep.Language);
    public IReadOnlyList<OnboardingStep> Steps { get; }
    public OnboardingStep Current { get; }
    public int Index { get; }                                  // 0-based
    public int Count => Steps.Count;
    public bool CanGoBack { get; }
    public bool IsLast { get; }
    public IReadOnlySet<OnboardingStep> Visited { get; }       // includes start
    public bool Next();                                        // false on last
    public bool Back();                                        // false on first
    public void GoTo(OnboardingStep step);                     // ignored if not in Steps
    public event Action? Changed;

    /// SHOULD (REQ-OUXA-11). NotVisited if !visited. NeedsAttention: Language → !PrivacyAcknowledged;
    /// Game → LikelyExclusiveFullscreen; ChatArea → !HasRegion; OcrLanguages → MissingOcrCount > 0;
    /// TestTranslation → false; Hotkeys → HotkeyProblems > 0. Skipped: TestTranslation visited with null. Else Done.
    public static StepStatus Evaluate(OnboardingStep step, OnboardingFacts facts, bool visited);
}

public static class OnboardingGate
{
    public static bool ShouldAutoOpen(AppSettings s) =>
        !s.Onboarding.Completed && !s.Onboarding.Skipped &&
        !(s.FirstRunCompleted && (s.Capture.Regions.Count > 0 || s.Capture.WindowRegions.Count > 0));
    /// Enum.TryParse(ignoreCase) of LastStep or a stepId from IShell; anything else → Language.
    public static OnboardingStep ParseStep(string? stepId);
    public static OnboardingStep ResumeStep(AppSettings s) => ParseStep(s.Onboarding.LastStep);
    /// Contract §4 step 11: false in safe mode and with --minimized; else ShouldAutoOpen(s).
    public static bool ShouldAutoOpenAtStartup(bool safeMode, bool minimized, AppSettings s) =>
        !safeMode && !minimized && ShouldAutoOpen(s);
}

public static class OnboardingOutcome
{
    /// completed → Completed = true, FirstRunCompleted = true. Otherwise Skipped = true. Both set LastStep = lastStep.
    public static void Apply(AppSettings s, bool completed, OnboardingStep lastStep);
}

public enum DisplayModeVerdict { NotRunning, Windowed, Borderless, LikelyExclusiveFullscreen, Unknown }

public static class DisplayModeAdvisor                        // SHOULD (REQ-OUXA-5); contract §2.4
{
    /// null → NotRunning. UniformFrameStreak >= BlankFrames.Threshold → LikelyExclusiveFullscreen.
    /// Minimized && !GameForeground → LikelyExclusiveFullscreen (exclusive games minimise on alt-tab).
    /// HasCaption → Windowed. WindowRect covers MonitorBounds within 2 px on every edge → Borderless. Else Unknown.
    public static DisplayModeVerdict Classify(GameWindowInfo? window);
}

public static class OnboardingText                            // contract §8.7
{
    public static string TitleOf(OnboardingStep step);        // "Language & privacy", "Game & display", "Chat area",
                                                              // "Text recognition", "Test translation", "Hotkeys"
    public const string StepCounter = "Step {index} of {count}";
    public const string BorderlessAdvice = "Borderless looks the same as fullscreen and lets Envoy see the chat. Choose it in the game's display or graphics settings.";
    public static string Describe(DisplayModeVerdict v);      // LikelyExclusiveFullscreen → CaptureText.LikelyExclusiveFullscreen
    public const string TestTranslationGoesTo = "This sends the sentence to {provider}.";
    public const string FinishSetup = "Finish setting up Envoy.";
}
```

`GameWindowInfo` and `BlankFrames` are 05's Core types. `Describe` never names game menu paths.

### Core: privacy (`src/Envoy.Core/Privacy/`, new)

```csharp
namespace Envoy.Core.Privacy;

public static class PrivacyText
{
    /// Bump when the data flows change; every user is asked again.
    public const int Version = 1;
    public const string Heading = "What leaves your PC";
    /// {providers} = provider chain display names joined with " then ", e.g. "Google Translate (free) then Microsoft Edge (free)".
    public const string ChatGoesTo = "Chat lines Envoy reads, and replies you translate, are sent to {providers} to be translated.";
    public const string KeylessNote = "The free Google and Microsoft options use unofficial endpoints without an account. You can choose another provider on the Translation page.";
    public const string UpdatesGoTo = "Update checks go to GitHub.";
    public const string StaysLocal = "Text recognition (OCR) and speech run on your PC and send nothing.";
    public const string Acknowledge = "I understand what leaves my PC";
    public const string TranslationOff = "Translation is off until you confirm what leaves your PC. Open Envoy to turn it on.";
    public const string CalloutText = "Envoy sends chat to {providers} to translate it. Translation stays off until you confirm.";
    public const string DocUrl = "https://github.com/PinnerSinner/Envoy/blob/main/docs/privacy.md";
    public static string ChatGoesToFor(IReadOnlyList<string> providerNames);
    public static bool IsAcknowledged(int acknowledgedVersion) => acknowledgedVersion >= Version;
}
```

```csharp
namespace Envoy.Core.Translation;

/// Outermost decorator (contract §8.1). Never calls inner while closed; no network, no health change, no cache.
public sealed class PrivacyGatedTranslator(ITranslator inner, Func<bool> allowed) : ITranslator
{
    public string Id => inner.Id;
    /// !allowed() → throw new TranslationException(PrivacyText.TranslationOff, kind: TranslationErrorKind.Configuration).
    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default);
}
```

`Configuration` is not counted by 03's breaker. The gate sits outside 03's runtime and fallback, so a closed gate
never reaches them. 04's pipeline ends the message as `Failed` with this text, and the overlay shows the original line
(contract §2.6). The `ITranslator` member list is copied from today's interface; if 03 adds members, the gate forwards
them.

### Core: OCR readiness (`src/Envoy.Core/Languages/OcrReadiness.cs`, new)

```csharp
public sealed record OcrRecommendation(string Code, string Name, bool Installed, bool IsMine, bool Wanted);

public static class OcrReadiness
{
    public static IReadOnlyList<string> CommonOpponentLanguages { get; } = ["zh-CN", "ko", "ja", "ru"];
    /// Wanted codes (normalised, distinct, plus myLanguage) whose OCR pack is not installed. Empty `wanted` means
    /// "every installed pack", so only myLanguage can be missing. A Latin-script language other than "vi" counts as
    /// covered when any Latin-script pack is installed (mirrors MainViewModel.LoadOcrLanguages).
    public static IReadOnlyList<string> MissingWanted(IEnumerable<string> wanted, string myLanguage, IEnumerable<string> installedCodes);
    /// Your language first, then wanted, then CommonOpponentLanguages, distinct, with status.
    public static IReadOnlyList<OcrRecommendation> Recommend(string myLanguage, IEnumerable<string> wanted, IEnumerable<string> installedCodes);
}
```

### Core: hotkey cheat sheet (SHOULD, `src/Envoy.Core/Hotkeys/HotkeyCheatSheet.cs`, new)

```csharp
namespace Envoy.Core.Hotkeys;
public sealed record HotkeyRow(string Action, string Display, HotkeyStatus Status);
public static class HotkeyCheatSheet
{
    /// One row per 01 HotkeyRegistrationResult, in its order. Display: Registered → Combo; NotSet → "Not set";
    /// Taken → "{combo} · taken by another app"; Invalid → "{combo} · not a valid combination";
    /// SkippedSafeMode → "{combo} · off in safe mode".
    public static IReadOnlyList<HotkeyRow> Build(IReadOnlyList<HotkeyRegistrationResult> results);
}
```

There is no `FailedHotkeyCombos` and no `IShell.IsHotkeyTaken`; `AppHost.HotkeyResults` is the only source (contract
§2.3).

### Core: notices (`src/Envoy.Core/Notifications/`, next to 02's throttle)

```csharp
namespace Envoy.Core.Notifications;

public enum NoticeKind { OcrLanguageMissing, ProviderFailing, UpdateAvailable }
public enum NoticeDecision { Show, Defer, Suppress }
public static class NoticeActions { public const string OpenCapture = "open-capture", OpenTranslation = "open-translation", UpdatePrimary = "update-primary"; }

public sealed record Notice(NoticeKind Kind, string Key, string Title, string Text, string ActionId, bool IsWarning);

public static class NoticeText                                 // contract §8.7
{
    public const string OcrMissingTitle = "Text recognition pack missing";
    public const string OcrMissing = "Envoy can't read {languages} chat until Windows' free text recognition pack is installed. Click to install.";
    public const string ProviderTitle = "Translation provider problem";
    public const string ProviderAuthFailed = "{provider} rejected your key. Envoy is using your other providers. Click to check the key.";
    public const string ProviderQuota = "{provider} has reached its limit. Envoy is using your other providers until it resets.";
    public const string ProviderCoolingDown = "{provider} keeps failing. Envoy is using your other providers and will try it again shortly.";
    public const string UpdateTitle = "Envoy update";
}

public static class NoticeFactory
{
    /// null when empty. Key = "ocr-missing:" + codes sorted ordinal, comma-joined. Text uses language names.
    public static Notice? OcrMissing(IReadOnlyList<string> missingCodes);
    /// null unless state is AuthFailed, QuotaExceeded or CoolingDown. Never includes LastError.
    public static Notice? ProviderFailing(string providerId, string providerName, ProviderState state);
    /// null unless snapshot.State is Available or ReadyToRestart. Key = "update:" + version; Text = view.Headline.
    public static Notice? UpdateAvailable(UpdateSnapshot snapshot, UpdateView view);
}

/// UI thread only. Keeps Active, the dismissed keys (in settings) and the deferral queue; no counters (contract §2.2).
public sealed class NoticeCenter(AppSettings settings, NotificationThrottle throttle)
{
    public IReadOnlyList<Notice> Active { get; }   // newest first; never UpdateAvailable
    public bool IsHeld { get; }
    public event Action? Changed;                  // Active changed

    /// Order: (1) key in Notifications.Dismissed → Suppress, not listed. (2) UpdateAvailable whose version equals
    /// Updates.LastAnnouncedVersion → Suppress. (3) Non-update: add or replace in Active. (4) Key already announced and
    /// still active → Suppress. (5) !Enabled or kind toggle off → Suppress (stays in Active). (6) Held or
    /// gameForeground → Defer. (7) !throttle.TryShowBalloon(key) → Defer. (8) Show: mark announced; for updates set
    /// Updates.LastAnnouncedVersion. Deferred entries are one per key, latest wins.
    public NoticeDecision Submit(Notice notice, bool gameForeground);
    /// Re-runs steps 1–2 and 4–8 over the queue (FIFO). Suppressed entries leave the queue. Returns the first Show, or null.
    public Notice? TakeDeferred(bool gameForeground);
    public void Hold(bool held);
    public void Resolve(string key);               // drop from Active and the queue; clears "announced", so it re-arms
    public void ResolveWhere(Func<string, bool> keyMatch);
    public void Dismiss(string key);               // SHOULD: add to Dismissed (distinct) + Resolve
}
```

### Core: page hints (`src/Envoy.Core/Ux/PageHints.cs`, new)

```csharp
namespace Envoy.Core.Ux;

public enum HintSeverity { Info, Warning, Error }
public static class HintActions { public const string SelectRegion = "select-region", ToggleCapture = "toggle-capture",
    OpenTranslation = "open-translation", OpenCapture = "open-capture", ShowOverlay = "show-overlay",
    OpenHotkeys = "open-hotkeys", EditPhrases = "edit-phrases", OpenSetup = "open-setup"; }
public sealed record PageHint(HintSeverity Severity, string Text, string? ActionLabel = null, string? ActionId = null);

public enum CaptureConditionKind { Running, Stopped, NoRegion, WaitingForGame, AutoPaused, Error }
public readonly record struct CaptureCondition(CaptureConditionKind Kind, PauseReason Reason = PauseReason.None, string? Error = null)
{
    public static CaptureCondition AutoPaused(PauseReason reason) => new(CaptureConditionKind.AutoPaused, reason);
}

public static class PageHints
{
    /// MUST (War Room). Stopped → Info CaptureText.Stopped + "Start reading" (ToggleCapture). NoRegion → Warning +
    /// "Select chat area". WaitingForGame, Running → null. AutoPaused(r): NoRegion/NoGame map as above;
    /// RegionOffScreen → Warning CaptureText.Paused(r) + "Select chat area again"; any other r → Info
    /// CaptureText.Paused(r) with no action. Error → Error with the 05 sentence in Error.
    public static PageHint? Capture(CaptureCondition capture);
    public static PageHint? Phrasebook(string? loadError);                 // MUST (REQ-OUXA-23)
    // SHOULD (REQ-OUXA-22):
    public static PageHint? Dispatches(int messageCount);
    public static PageHint? Players(int playerCount);
    public static PageHint? OcrAvailability(int statusCount, int missingWanted);
    public static PageHint? ProviderCredentials(string providerName, bool hasCredential);
    public static PageHint? OverlayHidden(bool visible, string toggleHotkey);
    public static PageHint? ReplyHotkey(HotkeyRegistrationResult? compose);
    public static PageHint? Hotkeys(IReadOnlyList<HotkeyRegistrationResult> results);
}
```

Hint sentences live in `PageHintText` (contract §8.7). `MainViewModel` maps `WatcherState` in one switch: `Stopped` →
Stopped, `NoRegion` → NoRegion, `Running` → Running, `Idle` → WaitingForGame, `Paused` →
`AutoPaused(Watcher.PauseReason)`, `Error` → `Error(Watcher.LastError)`.

```csharp
public enum HintSlotKind { None, Privacy, FinishSetup, PageHint }
public static class WarRoomSlots
{
    /// Privacy if !privacyAcknowledged; else FinishSetup if !onboardingCompleted; else PageHint if hint != null; else None.
    public static HintSlotKind Choose(bool privacyAcknowledged, bool onboardingCompleted, PageHint? hint);
}
```

### Core: phrase book (`src/Envoy.Core/Phrases/PhraseBook.cs`, changed)

`public static PhraseBook LoadWithUserFile(string userFile, out string? error)`. A missing file gives `error = null`.
Invalid JSON gives `"it isn't valid JSON (line {n})"`. 08a's size check gives `"it is larger than 1 MB"`. The
existing one-argument overload calls it and discards `error`. `AppHost.ReloadPhrases` and the constructor set
`PhrasesLoadError`.

### App: setup guide

**`Windows/SetupWizardWindow.xaml(.cs)`** (new, about 260 lines of XAML):

- About 760×560, `WindowStyle=None` with `MainWindow`'s title-bar chrome (the close button has
  `AutomationProperties.Name="Close setup"`), `Bg.Window` background, `ResizeMode=CanMinimize`.
- Left column: the step list (`ItemsControl` of `StepItem {Title, Status, IsCurrent}`), each item named
  "{Title}, {status}". Right column: one panel per built step, only the current one visible. Footer: Skip setup
  (`IsCancel=True`), Back, Next/Finish (`IsDefault=True`, `Button.Primary`, disabled on the first step until the
  privacy box is ticked).
- Steps:
  1. **Language & privacy:** `ComboBox` → `Settings.MyLanguage`; `TextBox` → `Settings.MyPlayerName`; the
     `PrivacyText` block (heading, `ChatGoesToFor(names)`, `KeylessNote`, `UpdatesGoTo`, `StaysLocal`), a "Privacy
     details" link (`IShell.OpenUrl(PrivacyText.DocUrl)`), and the `Acknowledge` checkbox.
  2. **Game & display:** `Host.GameStatus`, `OnboardingText.BorderlessAdvice`, and (SHOULD) the verdict text and
     "Check again".
  3. **Chat area:** "Select chat area", `RegionText`, the preview `Image` and text from the step's
     `CapturePreviewViewModel`.
  4. **Text recognition:** `OcrRecommendation` rows (name, Installed/Missing, Install button for missing ones), and
     "Check again".
  5. **Test translation** (SHOULD): input, Translate, result, and `OnboardingText.TestTranslationGoesTo`.
  6. **Hotkeys** (SHOULD): a two-column grid of `HotkeyRow`s and an "Open Hotkeys settings" link
     (`IShell.ShowPage(PageIds.Hotkeys)`).
- `public SetupWizardWindow(AppHost host, IShell shell, OnboardingStep start)`; `public SetupWizardViewModel ViewModel`.
- `OnClosed` calls `ViewModel.Dispose()`, which disposes the preview view model and so releases its lease.

**`ViewModels/SetupWizardViewModel.cs`** (new, about 200 lines; `ObservableObject`, `IDisposable`):

- Navigation: `NextCommand`, `BackCommand`, `SkipCommand`, `GoToStepCommand` (`RelayCommand<OnboardingStep>`),
  `CurrentTitle`, `StepText`, `NextLabel`, `CanGoNext`, `Steps`.
- Per step: `PrivacyAcknowledged` (setter writes `Onboarding.PrivacyAcknowledgedVersion = PrivacyText.Version` or 0
  and calls `Host.ApplySettings()` at once, not through the 400 ms applier, so the gate is open before the Test
  translation step can run),
  `PrivacyLines`, `SelectRegionCommand` (`_shell.SelectRegion()`), `RegionText`, `CapturePreview` (its own
  `new CapturePreviewViewModel(host, dispatcher)`), `OcrRows`, `InstallOcrCommand` (`_shell.InstallOcrPack(code)`),
  `RecheckOcrCommand`, and the SHOULD members `DisplayVerdictText`, `RecheckDisplayCommand`, `TestInput`,
  `TestOutput`, `TestTranslateCommand` (`AsyncRelayCommand` with 01's required `onError`), `HotkeyRows`.
- Settings writes go through its own `SettingsAutoApplier` (01) into `Host.ApplySettings()`.
- Each step change sets `Onboarding.LastStep`, refreshes statuses (SHOULD), and sets
  `CapturePreview.IsActive = Current == OnboardingStep.ChatArea`.
- Finish and Skip raise `event Action<bool /*completed*/>? Finished`.
- No `App.Current`, `Process.Start` or `MessageBox` (01's rule, 08a's banned list).

**`App.xaml.cs`** (shared) adds the 07a `IShell` block and fills the `[07a …]` slots:

- `public void OpenSetupWizard(string? stepId = null)`: if `_wizard` is open, activate it and `GoTo(ParseStep(stepId))`
  when `stepId` is given. Otherwise create it at `stepId is null ? Language : ParseStep(stepId)`, call
  `Host.Notices?.Hold(true)`, and subscribe `Finished`. On `Finished(completed)`:
  `OnboardingOutcome.Apply(Host.Settings, completed, ViewModel.Current)`, `Host.SaveNow()`, `Host.Notices?.Hold(false)`,
  `Host.ReleaseDeferredNotices()`, and when completed `ShowPage(PageIds.Dashboard)`. Closing with X or Esc counts as
  Skip.
- `public void ShowPage(string pageId)`: `ShowMain(); _main.Navigate(pageId);`.
- `SelectRegion()` also hides and re-shows `_wizard`, the same way it does `_main`.
- **`OnStartup` step 11** (contract §4): `safe → ShowMain()`; else `--minimized → tray only`; else
  `OnboardingGate.ShouldAutoOpenAtStartup(safe, minimized, Host.Settings) → OpenSetupWizard(Host.Settings.Onboarding.LastStep)`;
  else 02's `plan.ShowMainWindow` rule.
- **Tray menu** (after "Open Envoy"): "Setup guide…" → `OpenSetupWizard()`; (SHOULD) "Hotkey cheat sheet…" →
  `OpenSetupWizard(nameof(OnboardingStep.Hotkeys))`.

### App: notices

**`AppHost.cs`** (shared), at the contract §3 positions:

- **Constructor position 9:** `Notices = IsSafeMode ? null : new NoticeCenter(Settings, Notifications);`. When
  non-null: subscribe `ProviderHealthChanged` (03, UI thread), hook 06's UI-thread `RefreshUpdateView` (below),
  `GameWindowChanged` (05), and start `_noticeTimer` (`DispatcherTimer`, 5 s) → `ReleaseDeferredNotices()`.
- Members: `public NoticeCenter? Notices { get; }`, `public event Action<Notice>? NoticeReady` (UI thread),
  `public string? PhrasesLoadError { get; private set; }`, `public void ReleaseDeferredNotices()`.
- `private void Submit(Notice? n)`: if `n` and `Notices` are non-null, `d = Notices.Submit(n, GameWindow?.GameForeground == true)`;
  log `notice {kind} {key} → {d}` at Info under category `Notices`; on `Show` raise `NoticeReady`.
- `ReleaseDeferredNotices()`: `while (Notices?.TakeDeferred(GameWindow?.GameForeground == true) is { } n) { log; NoticeReady?.Invoke(n); }`.
  The throttle's 60 s spacing makes this release at most one per call.
- **`ApplySettings` position 2:** `Volatile.Write(ref _privacyAcknowledged, PrivacyText.IsAcknowledged(Settings.Onboarding.PrivacyAcknowledgedVersion))`.
- **`ApplySettings` position 4 (`BuildTranslator`, 03's slot):** wrap the chat translator and `ComposeTranslator` in
  `new PrivacyGatedTranslator(inner, () => Volatile.Read(ref _privacyAcknowledged))`. This is one line per
  translator inside 03's method.
- **`ApplySettings` position 8 and `Start()`:** `CheckOcrReadiness()` computes
  `OcrReadiness.MissingWanted(Settings.Capture.OcrLanguages, Settings.MyLanguage, OcrService.InstalledLanguages().Select(l => l.Code))`,
  calls `Notices.ResolveWhere(k => k.StartsWith("ocr-missing:", Ordinal) && k != newKey)`, then `Submit(NoticeFactory.OcrMissing(missing))`.
  It is wrapped in `try/catch (Exception ex)` that logs (OCR is unavailable on some Windows editions; `CLAUDE.md`
  catch rule).
- **`ProviderHealthChanged(s)`:** `Healthy` → `Notices.Resolve("provider:" + s.ProviderId)`; otherwise
  `Submit(NoticeFactory.ProviderFailing(s.ProviderId, name, s.State))`.
- **Update handler:** one line at the end of 06's `RefreshUpdateView` (already on the UI thread after
  `Updates.Changed`): `Submit(NoticeFactory.UpdateAvailable(Updates.Snapshot, UpdateView))`. Showing it writes
  `Settings.Updates.LastAnnouncedVersion` on the UI thread, followed by `ScheduleSave()`.
- **`GameWindowChanged`** and **`RefreshGame` position 4** (on game exit) call `ReleaseDeferredNotices()`.
- `Dispose()` stops `_noticeTimer` and unsubscribes.

**`Services/TrayNotifier.cs`** (new, about 50 lines):

```csharp
internal sealed class TrayNotifier(Action<string> runAction, ILog log) : IDisposable
{
    /// Step 11, after CreateTray(). Shows the held notice, if any.
    public void Attach(System.Windows.Forms.NotifyIcon tray);
    /// ShowBalloonTip(8000, Title, Text, IsWarning ? Warning : Info); remembers ActionId for BalloonTipClicked;
    /// BalloonTipClosed clears it. Before Attach, holds the latest notice. Never throws: exceptions are logged at Warning.
    public void Show(Notice notice);
    public void Dispose();                       // unsubscribes
}
```

**`App.xaml.cs` `OnStartup` step 8** (skipped in safe mode): `_notifier = new TrayNotifier(RunNoticeAction, _hub.For("Notices"));
Host.NoticeReady += _notifier.Show;`. The tray itself is created at step 11 (02's skeleton), so **step 11** calls
`_notifier?.Attach(_tray)` right after `CreateTray()`. A notice shown before `Attach` (for example the OCR check in
`Host.Start()` at step 9) is held, latest wins, and shown on `Attach`, so no announced notice is lost.
`RunNoticeAction`: `open-capture` → `ShowPage(PageIds.Capture)`; `open-translation` → `ShowPage(PageIds.Translation)`;
`update-primary` → `ShowPage(PageIds.Dashboard)` then `UpdateActions.Run(Host.UpdateView.Primary, Host, this, log)`
(06), so there is one dispatcher for `UpdateAction` shared by the banner, the tray item and the balloon. `ExitApp` step 5 disposes `_notifier` before the tray.

### App: War Room, pages and docs

- **`Themes/Controls.xaml`:** a `Callout` style (`ContentControl`, `ContentTemplate` over `PageHint`): `Bg.PanelAlt`
  border with a 3 px left edge in `Accent` (Info) or `Danger` (Warning, Error), a glyph in `Font.Icons`, the text in
  `Text.Label`, and an optional `Button.Ghost` bound to the page view model's `RunHintActionCommand` with
  `AutomationProperties.Name="{Binding ActionLabel}"`. It collapses when `Content` is null
  (`NullToCollapsedConverter`, new in `Controls/Converters.cs`). Palette keys only.
- **`Pages/WarRoomPage.xaml`:** after 06's banner, the notice list (`ItemsControl` over `VisibleNotices`, at most 3,
  plus a "Show all (n)" toggle), then the hint slot (a `ContentControl` switching on `HintSlot`: a privacy callout
  with `PrivacyText.CalloutText`, a "Turn on translation" button (`AcknowledgePrivacyCommand`) and a "Privacy details"
  link; or the Finish-setup callout with "Open setup guide"; or the capture `Callout`), then the existing content.
  Each slot element has an `x:Name` (`NoticeList`, `HintSlot`) for the smoke order check.
- **`ViewModels/MainViewModel.Hints.cs`** (new partial): `HintSlot`, `CaptureHint`, `PhrasebookHint`, the SHOULD
  hints, `Notices`/`VisibleNotices`/`ShowAllNotices`, `RunHintActionCommand` (`RelayCommand<string>` mapping each
  `HintActions` id to an existing command or `IShell` call), `RunNoticeActionCommand`, `DismissNoticeCommand` (SHOULD),
  `AcknowledgePrivacyCommand` (sets the version and calls `Host.ApplySettings()` at once), `OpenSetupGuideCommand`. `RefreshStatus()` (1 s timer and after each apply) notifies
  them; `Host.Notices.Changed` refreshes the list.
- **`Pages/AppearancePage.xaml`:** a "Notifications" frame (`x:Name="NotificationsFrame"`) before 06's Startup &
  updates frame: "Show tips in the notification area" → `Notifications.Enabled` and (SHOULD) the three kind toggles.
- **`Pages/AboutPage.xaml`:** a "Setup guide" frame after 06's Version & updates ("Run setup guide again" →
  `OpenSetupGuideCommand`), and a "Privacy" frame after 02's Troubleshooting (`PrivacyText.Heading` lines plus the
  "Privacy details" link).
- **`Pages/PhrasebookPage.xaml`:** the Phrasebook callout (MUST). Other pages get their SHOULD callouts.
- **Docs:** `docs/privacy.md` gains "What leaves your PC" (provider chain and the keyless endpoints, GitHub update
  checks, local OCR and speech) and "How to turn translation off" sections above 02's stored-data section.
  `docs/faq.md` gains "Is Envoy allowed in ranked/anti-cheat games?": what Envoy does (reads pixels, types like a
  keyboard) and does not do (no memory reads, injection or hooks); that no publisher has endorsed it; links to each
  publisher's third-party-tool policy where one is published, otherwise "no published policy found" with the date
  checked. `docs/getting-started.md` describes the guide.

### Allowlists and policy (contract §5)

- **P/Invoke additions:** none. `Native.cs` and `policy.json → native` are unchanged. The display verdict uses
  `AppHost.GameWindow` (05). The old `DisplayModeProbe`, `MonitorFromWindow` and `GetMonitorInfo` are gone.
- **Network allowlist:** no new path and no new host. `PrivacyText.DocUrl` is on `github.com`, which 08a seeds
  because today's code already names it. It is opened through `IShell.OpenUrl` → `ShellLauncher.TryOpen`.
- **Process launching:** only through `IShell.InstallOcrPack` and `IShell.OpenUrl` (08a's `ShellLauncher`).
- **Modal boxes:** none. Notices never use `DialogService` or `ToastWindow`.

### Threading

- `NoticeCenter`, `OnboardingFlow` and all view models are UI-thread only. Every source event already arrives on the
  UI thread (03's `ProviderHealthChanged`, 05's `GameWindowChanged`, 06's marshalled update handler).
- `PrivacyGatedTranslator` runs on pool threads and reads only a volatile `bool` (contract §7).
- `TrayNotifier.Show` runs on the UI thread, where the WinForms `NotifyIcon` was created.
- `OcrService.InstalledLanguages()` is a fast synchronous WinRT query; "Check again" runs it on the UI thread.

### Failure behaviour

| Situation | Behaviour |
| --- | --- |
| OCR API unavailable | The OCR step lists every language as Missing; no notice (the check logs and skips). |
| Elevation declined | `ShellLauncher.InstallOcrPack` falls back to `ms-settings:regionlanguage` (01/08a). The step stays Missing. |
| Test translation fails | 03's error text is shown; the step is Needs attention; the guide can still finish. |
| Privacy not acknowledged | Overlay lines end `Failed` with `PrivacyText.TranslationOff`; the War Room shows the privacy callout. No request is made. |
| `ShowBalloonTip` throws, Focus Assist hides balloons | Logged. The War Room list still has the notice. |
| Game in foreground for a whole match | Advisories wait in the queue, one per key, and appear after the game loses focus or exits. |
| `phrases.json` invalid or over 1 MB | Built-ins are used; the Phrasebook callout shows the reason. |
| Safe mode | No `NoticeCenter`, no balloons, no auto-open guide. The guide can still be opened by hand. |

## Acceptance criteria

| Id | REQ | Verified by |
| --- | --- | --- |
| AC-OUXA-1 | 1 | Unit `OnboardingFlowTests.Next_and_back_walk_steps_in_order`, `Count_follows_built_steps`, `GoTo_unknown_step_is_ignored`. Smoke `guide({palette})`: every step visited with `GoTo`, exactly one visible panel, "Step n of N" text. Typecheck: a `typecheck.sh` grep fails if `SetupWizardViewModel.cs` contains `new AppSettings` or `SaveNow` (edits go only through `Host.Settings` and the applier). |
| AC-OUXA-2 | 2 | Unit `OnboardingGateTests.Opens_for_fresh_settings`, `Skips_existing_users_with_region`, `Skips_existing_users_with_window_region`, `Never_reopens_after_completed_or_skipped`, `Startup_never_opens_in_safe_mode`, `Startup_never_opens_when_minimized`. |
| AC-OUXA-3 | 3 | Smoke `guide-entry` (setup phase): the tray menu has "Setup guide…"; `OpenSetupWizard("Hotkeys")` opens at Hotkeys; a second `OpenSetupWizard("OcrLanguages")` activates the same instance and moves it; `Hold` is true while open and false after `Close()`. |
| AC-OUXA-4 | 6 | Smoke `guide({palette})`: the step's `CapturePreview.IsActive` is true only on ChatArea, and `host.Watcher.IsPreviewing` is false after `Close()`. M-OUXA-1. |
| AC-OUXA-5 | 7 | Unit `OcrReadinessTests.*`. M-OUXA-2. |
| AC-OUXA-6 | 10 | Unit `OnboardingFinishTests` over a pure `OnboardingOutcome.Apply(settings, completed, lastStep)`: Finish sets `Completed` and `FirstRunCompleted`; Skip sets `Skipped` and keeps `LastStep`. M-OUXA-1. |
| AC-OUXA-7 | 13 | Unit `NoticeCenterTests` with a real `NotificationThrottle` on `FakeTimeProvider`: `Shows_first_notice`, `Defers_while_game_foreground_and_releases_after`, `Hold_defers_everything`, `Throttle_refusal_defers_not_drops`, `Latest_wins_per_key_in_queue`, `Announced_key_not_reballooned_until_resolved`, `Resolve_rearms`, `Master_switch_off_keeps_active_and_suppresses_balloon`, `Update_not_listed_in_active`, `Update_version_announced_once_across_instances`. Smoke `notices({palette})` (MainWindow open): a seeded OCR notice is in the War Room list. |
| AC-OUXA-8 | 16 | Unit `NoticeFactoryTests.Provider_notice_has_no_error_text` (snapshot with `LastError = "secret-key-123 boom"` → text contains neither), `Ocr_notice_lists_language_names_not_codes`, `Ocr_key_is_order_independent`, `Returns_null_for_non_notice_states`. |
| AC-OUXA-9 | 17 | Unit `NoticeFactoryTests.Update_uses_headline_and_primary_action`, `Update_only_for_available_or_ready`. Unit `ProviderNoticeRulesTests` (a pure `ProviderNoticeRules.For(ProviderState)` → Submit/Resolve/None) over every `ProviderState`. Smoke `notice-actions` (first palette): `RunNoticeAction("open-translation")` leaves `MainWindow` on Translation; `"open-capture"` on Capture. M-OUXA-3, M-OUXA-4. |
| AC-OUXA-10 | 23 | Unit `PhraseBookTests.Load_reports_invalid_json`, `Load_reports_oversize`, `Missing_file_has_no_error`, `One_argument_overload_unchanged`; `PageHintsTests.Phrasebook`. |
| AC-OUXA-11 | 47 | Unit `TranslationGateTests.Closed_gate_never_calls_inner`, `Closed_gate_throws_configuration_kind`, `Open_gate_forwards`, `Version_bump_closes_gate`; `PrivacyTextTests.Names_every_provider_in_chain`, `Mentions_github_and_local_ocr`. Smoke `privacy-gate` (setup phase, before 01's warm-up): on the fresh main host, `host.Translator.TranslateAsync` throws `TranslationException` with `Kind == Configuration`; then the test sets the acknowledgement and calls `ApplySettings()`. Every other smoke host sets the acknowledgement right after construction. M-OUXA-1 (the box gates Next). |
| AC-OUXA-12 | 48 | Unit `PageHintsTests.Capture` (Theory over every `CaptureConditionKind` and every `PauseReason`: only `RegionOffScreen` has an action, and it is `SelectRegion`; no AutoPaused row has `ToggleCapture`); `WarRoomSlotsTests.Priority`. Smoke `war-room-layout({palette})`: with a seeded notice and `Completed = false`, the visible elements among `SafeModeBanner` (02), `UpdateBanner` (06), `NoticeList` and `HintSlot` have increasing `TranslatePoint` Y, and `NoticeList` and `HintSlot` are visible; the hint slot shows FinishSetup; first palette only, with the acknowledgement cleared it shows Privacy, and four seeded notices show 3 rows plus "Show all (4)". Smoke `page-order` (first palette): Appearance frames and About frames are in the §8.6 order by Y. |

SHOULD items are verified by: `OnboardingFlowTests.Evaluate_rules` (11), `DisplayModeAdvisorTests.Classify` (5),
`HotkeyCheatSheetTests` (9), `NoticeCenterTests.Disabled_kind_*` and `Dismissed_key_never_shows_or_lists` (18, 20),
`PageHintsTests.*` (22), and the smoke `guide` section's Test translation run (8).

## Test plan

### Unit tests (`tests/Envoy.Core.Tests/`, xUnit, `FakeTimeProvider`)

| File | Tests |
| --- | --- |
| `Onboarding/OnboardingFlowTests.cs` | `Next_and_back_walk_steps_in_order`, `Count_follows_built_steps`, `GoTo_marks_visited_and_raises_changed`, `GoTo_unknown_step_is_ignored`, `Evaluate_rules` (Theory, SHOULD) |
| `Onboarding/OnboardingGateTests.cs` | gate truth table (6 tests in AC-OUXA-2), `ParseStep_defaults_to_language`, `ResumeStep_uses_last_step` |
| `Onboarding/OnboardingFinishTests.cs` | `Finish_sets_completed_and_first_run`, `Skip_sets_skipped_and_keeps_last_step` |
| `Onboarding/DisplayModeAdvisorTests.cs` (SHOULD) | `Classify` (Theory: null; uniform streak; minimised in background; caption; covers monitor ±2 px; partial) |
| `Privacy/PrivacyTextTests.cs` | `Names_every_provider_in_chain`, `Mentions_github_and_local_ocr`, `IsAcknowledged_compares_versions` |
| `Translation/TranslationGateTests.cs` | the four tests in AC-OUXA-11 (inner fake counts calls) |
| `Languages/OcrReadinessTests.cs` | `Missing_excludes_installed_and_normalises_codes`, `Latin_language_covered_by_any_latin_pack_except_vi`, `Empty_wanted_means_only_my_language_can_be_missing`, `Recommend_orders_mine_then_wanted_then_common_distinct` |
| `Hotkeys/HotkeyCheatSheetTests.cs` (SHOULD) | one row per result in order; display text per `HotkeyStatus` |
| `Notifications/NoticeCenterTests.cs` | AC-OUXA-7 list, plus `Disabled_kind_suppresses_balloon_but_keeps_active` and `Dismissed_key_never_shows_or_lists` (SHOULD) |
| `Notifications/NoticeFactoryTests.cs` | AC-OUXA-8 and AC-OUXA-9 lists |
| `Notifications/ProviderNoticeRulesTests.cs` | every `ProviderState` |
| `Ux/PageHintsTests.cs` | `Capture` Theory (AC-OUXA-12), `Phrasebook`, one Theory per SHOULD method |
| `Ux/WarRoomSlotsTests.cs` | `Priority` (all 8 combinations) |
| `Phrases/PhraseBookTests.cs` | AC-OUXA-10 list (temp files) |

### Typecheck

- `tools/typecheck/typecheck.sh`: no new script. `gen_stubs.py` picks up the new App files. The `new AppSettings` grep
  of AC-OUXA-1 is one line in `typecheck.sh`.

### Smoke-test sections (`src/Envoy.App/SmokeTest.cs`, contract §6 placement)

1. **Setup phase** (once): `privacy-gate` (before 01's provider warm-up); `guide-entry`; the main host gets
   `host.Notices!.Submit(NoticeFactory.OcrMissing(["ja"])!, false)` so the War Room list renders.
2. **Per palette, MainWindow-dependent checks** (after 06's update states): `notices({palette})`,
   `war-room-layout({palette})`, `notice-actions` and `page-order` (first palette only).
3. **Per palette, other windows:** `guide({palette})`: `new SetupWizardWindow(host, (IShell)app, OnboardingStep.Language)`,
   `Show()`, then for each built step `ViewModel.GoTo(step)` and pump; assert one visible panel and the preview flag;
   with `--screenshots`, save `{palette}-setup-{step}.png` for `imperial` only (07b adds `highcontrast`). On the first
   palette only, run `TestTranslateCommand` with a 5 s cap and assert non-empty output (SHOULD). `Close()`. Then, on
   the first palette only, `tray-notifier`: a `TrayNotifier`, `Show` of a seeded notice before `Attach` (held), then
   `Attach` to a `NotifyIcon` (`Visible = true`) and assert the held notice was shown (no exception, held slot empty),
   dispose.

Every section logs `[smoke] <section> ok elapsed_ms=<n>`. Estimated added time: about 1 s per palette for the guide,
plus about 1 s once.

### Manual pass (07a appends to `docs/releasing.md` under "Onboarding and notices"; 08b consolidates)

1. **M-OUXA-1** (REQ 1, 6, 10, 47). Fresh profile (`%APPDATA%\Envoy` renamed). The guide opens. Next is disabled on
   step 1 until "I understand" is ticked. Walk every step with AoE II DE or AoE IV in borderless: Select chat area
   hides the guide and returns with a live preview. Finish lands on the War Room. Repeat with Skip setup: restart opens
   `MainWindow`; the War Room shows the setup callout; the tray "Setup guide…" reopens it.
2. **M-OUXA-2** (REQ 7). On a PC without the Japanese pack, the OCR step shows Japanese Missing. Install → UAC →
   after completion, Check again shows Installed and the OCR balloon does not reappear.
3. **M-OUXA-3** (REQ 13, 17). DeepL primary with a bogus key, receive chat: one balloon appears; clicking opens the
   Translation page. With the game focused, force the failure again after fixing and re-breaking the key: no balloon
   until you alt-tab out, then it appears within about 5 s.
4. **M-OUXA-4** (REQ 17). Install an older release with `Setup.exe` while a newer one exists on its channel, then
   start it with a game focused: no balloon. Close the game: one "Envoy update" balloon; clicking it opens the War Room and runs
   the banner's primary action. Restart Envoy: no second balloon for the same version.
5. **M-OUXA-5** (REQ 47). With translation not acknowledged, play a match: the overlay shows original lines with no
   translation; Fiddler or Wireshark shows no request to `translate.googleapis.com`.
6. **M-OUXA-6** (docs). The FAQ policy links open, or the "no published policy found" date is current.

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Settings/AppSettings.cs` | `Onboarding`, `Notifications` sections; `Updates.LastAnnouncedVersion` | **yes** |
| `src/Envoy.Core/Onboarding/{OnboardingFlow,OnboardingGate,OnboardingOutcome,DisplayModeAdvisor,OnboardingText}.cs` | new | |
| `src/Envoy.Core/Privacy/PrivacyText.cs`, `src/Envoy.Core/Translation/PrivacyGatedTranslator.cs` | new | |
| `src/Envoy.Core/Languages/OcrReadiness.cs`, `src/Envoy.Core/Hotkeys/HotkeyCheatSheet.cs` | new | |
| `src/Envoy.Core/Notifications/{Notice,NoticeText,NoticeFactory,NoticeCenter,ProviderNoticeRules}.cs` | new (beside 02's throttle) | |
| `src/Envoy.Core/Ux/{PageHints,PageHintText,WarRoomSlots}.cs` | new | |
| `src/Envoy.Core/Phrases/PhraseBook.cs` | `LoadWithUserFile(path, out error)` | yes (08a) |
| `tests/Envoy.Core.Tests/**` | test files above | |
| `src/Envoy.App/AppHost.cs` | position 9 `Notices`; `NoticeReady`; `PhrasesLoadError`; privacy flag (ApplySettings 2) and gate (4); OCR readiness (8); provider, update and game-window subscriptions; `ReleaseDeferredNotices`; `RefreshGame` position 4 | **yes** |
| `src/Envoy.App/App.xaml.cs` | 07a `IShell` block, `OnStartup` step 8 notifier, step 11 `Attach` and guide branch, tray items, `RunNoticeAction`, `SelectRegion` hides the guide, `ExitApp` step 5 | **yes** |
| `src/Envoy.App/IShell.cs` | `OpenSetupWizard(string? stepId = null)`, `ShowPage(string pageId)` | yes (01) |
| `src/Envoy.App/Windows/SetupWizardWindow.xaml(.cs)` | new | |
| `src/Envoy.App/ViewModels/SetupWizardViewModel.cs`, `StepItem.cs` | new | |
| `src/Envoy.App/ViewModels/MainViewModel.Hints.cs` | new partial | |
| `src/Envoy.App/Services/TrayNotifier.cs` | new | |
| `src/Envoy.App/Pages/WarRoomPage.xaml` | notice list, hint slot, empty states | yes (06 banner) |
| `src/Envoy.App/Pages/{AppearancePage,AboutPage,PhrasebookPage}.xaml` | Notifications frame; Setup guide and Privacy frames; Phrasebook callout | yes (02, 06) |
| `src/Envoy.App/Pages/*.xaml` (others) | SHOULD callouts | |
| `src/Envoy.App/Themes/Controls.xaml`, `Controls/Converters.cs` | `Callout` style, `NullToCollapsedConverter` | |
| `src/Envoy.App/SmokeTest.cs` | sections listed above | **yes** |
| `docs/privacy.md`, `docs/faq.md`, `docs/getting-started.md`, `docs/releasing.md`, `docs/architecture.md`, `CHANGELOG.md` | data flows; FAQ entry; guide; M-OUXA-1…6; notices and gate; changelog | yes |

Estimated size: Core about 450 lines plus tests about 450; App about 650 (the guide about 400); docs about 120.

## Risks & open questions

- **Translation is off by default until acknowledged.** A user who skips the guide gets originals only. Mitigation:
  the overlay line text and the War Room callout both say why and offer the one-click fix. This is the contract's
  explicit choice (§8.1).
- **Keyless endpoints' terms.** Disclosure does not make the unofficial Google and Edge endpoints sanctioned. The
  `KeylessNote` says so plainly and points to the Translation page. Changing the default provider is out of scope.
- **Exclusive-fullscreen verdict is a heuristic** (SHOULD). Minimised-on-alt-tab and 05's blank-frame streak are
  strong signals; a captionless full-monitor window in exclusive mode reads as Borderless. The wording says "likely",
  and the step never blocks.
- **Balloon visibility.** Focus Assist and per-app settings can hide balloons, which is why notices are also listed.
  On Windows 11 a balloon shows the exe's product name; 06 sets `Product` to "Envoy".
- **Deferred advisories during long matches.** By design they wait (contract §2.2). Problems still show as
  non-activating toasts (02).
- **FAQ policy links rot.** M-OUXA-6 rechecks them each release.

## Revision notes

Changes made when splitting `07-onboarding-ux-accessibility.md` and revising against `00-integration.md` and the set
critique:

- **Split (critique major: scope).** The old spec covered onboarding, notices, accessibility, reply box and overlay
  with 38 MUSTs. This file keeps the setup guide, notices, empty states, privacy and War Room layout. Ids keep their
  numbers with the `OUXA` prefix.
- **Update notice rewired (critique blocker; contract §2.5).** It no longer touches `CheckForUpdatesAsync`,
  `Host.Update` or `CurrentVersion.ToString(3)`. It hooks 06's UI-thread `RefreshUpdateView` and fires on `Available`
  or `ReadyToRestart`. The text is `UpdateView.Headline`, and the click runs `UpdateView.Primary` through 06's
  `UpdateActions.Run`, so it restarts to update when the copy is installed. `Notifications.LastAnnouncedUpdate` is
  gone, replaced by `Updates.LastAnnouncedVersion`, which 06 defines and this step adds under the arrival rule. Update
  notices are balloon-only. REQ-OUXA-17 is rewritten.
- **One notification policy (critique major; contract §2.2).** `NoticeCenter` asks 02's `NotificationThrottle` and
  keeps no counters. `NoticePolicy`, the 30 min provider cooldown and the once-per-session OCR rule are gone; "already
  announced while active" replaces them. The in-game policy defers every advisory while `GameForeground` is true.
  `QuietWhileGameFocused` and REQ-OUX-19 are removed. REQ-OUX-14 and 21 are folded into REQ-OUXA-13. Safe mode creates
  neither `NoticeCenter` nor `TrayNotifier`.
- **Provider health (critique major; contract §2.1).** `ProviderFailureMonitor` and REQ-OUX-15 are removed.
  `ProviderFailing` follows 03's `ProviderHealthChanged` state edges, so it also fires for breaker-skipped providers
  and re-arms on `Healthy`.
- **Page hints and automatic pauses (critique major).** `CaptureCondition` gained `AutoPaused(PauseReason)`. Automatic
  pauses show 05's text with no Resume action; `RegionOffScreen` offers "Select chat area again". A Theory covers
  every `PauseReason`.
- **Game-window probing (critique minor; contract §2.4).** `DisplayModeProbe`, `GameWindowFacts`, the
  `MonitorFromWindow`/`GetMonitorInfo` P/Invokes and `AppHost.IsGameInForeground` are removed.
  `DisplayModeAdvisor.Classify(GameWindowInfo?)` uses 05's facts and blank-frame streak and reuses
  `CaptureText.LikelyExclusiveFullscreen`. No P/Invoke or network allowlist additions.
- **`IShell` and hotkeys (critique minor; contract §2.3, §3).** The 07a block is exactly
  `OpenSetupWizard(string? stepId = null)` and `ShowPage(string pageId)`. `IsHotkeyTaken` and `FailedHotkeyCombos`
  are gone; the cheat sheet reads `AppHost.HotkeyResults`.
- **Process launching (critique blocker on 08; contract §5).** The OCR step calls `IShell.InstallOcrPack`
  (`ShellLauncher.InstallOcrPack`). The `OcrInstaller` helper is gone.
- **Start-up (critique major; contract §4).** The guide branch sits in step 11: never in safe mode, never with
  `--minimized`. The notifier is created in the notification-services slot, and its disposal is in `ExitApp` step 5.
  The gate also counts 05's `WindowRegions`.
- **Newly assigned (contract §8.1, §8.6).** Added REQ-OUXA-47 (privacy disclosure, acknowledgement and
  `PrivacyGatedTranslator`, `docs/privacy.md` data flows, FAQ anti-cheat entry) and REQ-OUXA-48 (War Room slot order,
  Appearance and About order). The privacy callout takes the setup-callout position while unacknowledged; this is how
  the contract's "privacy callout" and "Finish-setup callout first" rules combine.
- **UI strings (contract §8.7).** Text moved to `OnboardingText`, `PrivacyText`, `NoticeText` and `PageHintText`.
- **Phrase load errors (critique minor on 08).** REQ-OUXA-23 also reports 08a's 1 MB rejection.
- **Scope (contract §9).** MUSTs cut from 19 in this part of the old spec to 12. REQ-OUX-4 folded into 1; 14 and 21
  into 13; 15 and 19 removed; 5, 8, 9, 11, 12, 18, 20 and 22 are SHOULD. Test translation and Hotkeys steps are SHOULD,
  and `OnboardingFlow` takes its step list so they can be dropped cleanly. Every MUST has a unit test, a named smoke
  section or a numbered M-step.
- **Smoke (critique major on budget; contract §6).** Sections are placed in the three phases. The test translation
  and tray balloon run once, not per palette. Guide screenshots are taken for one palette only.
