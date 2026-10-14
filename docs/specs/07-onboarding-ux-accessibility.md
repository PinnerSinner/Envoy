# Onboarding, UX polish & accessibility: setup guide, notices, keyboard and screen-reader support, reply and overlay polish

Status: Proposed

Area id: `07-onboarding-ux-accessibility` · Requirement prefix: `OUX` · Depends on **01-ui-foundation**: pages as
`UserControl`s under `src/Envoy.App/Pages/`, `IShell`, the `Envoy.Core.Mvvm` command toolkit, `TranslationTestText`,
`OcrPackInstall`, `CapturePreviewViewModel`, `PageIds`. If **02-reliability-observability** and
**03-translation-resilience** merge first, this area reuses their pieces at the integration points named below. It
never duplicates them.

## Context

### What exists today

| Topic | Today | Where |
| --- | --- | --- |
| First run | `App.OnStartup` calls `ShowMain()` when `!Settings.FirstRunCompleted`. The War Room has a static "Get ready" card with four numbered rows: select the chat area, choose a language, enter your in-game name, and a plain-text reminder to use borderless. `MainWindow.OnClosed` sets `FirstRunCompleted = true`. Nothing checks OCR packs, tests the translator or teaches the hotkeys, and nothing can be re-opened later. | `App.xaml.cs`, `Windows/MainWindow.xaml` lines 105–167, `MainWindow.xaml.cs` |
| OCR packs | `OcrService.AllLanguageStatuses()` lists installed and missing packs. The Reading Chat page has a per-language **Install** button (elevated PowerShell; UIF moves it to `InstallOcrCommand` and `OcrPackInstall`). A missing pack is only visible if the user opens that page. The default `Capture.OcrLanguages` is `zh-CN, ko, ja, ru`, so most new users are missing several packs without knowing it. | `Services/OcrService.cs`, `ViewModels/MainViewModel.cs` (`LoadOcrLanguages`) |
| Notifications | The tray `NotifyIcon` has a context menu only. It never shows a balloon. Provider failures go to `AppHost.ProviderStatus`, which appears on the Translation page only. An available update shows as a banner on the War Room only. | `App.xaml.cs` (`CreateTray`), `AppHost.cs` (`BuildTranslator`, `CheckForUpdatesAsync`) |
| Focus visuals | `Controls.xaml` sets `FocusVisualStyle={x:Null}` on `Button.Base` (all buttons), `NavItem`, `ThemeCard`, `CheckBox` and `Slider`. Only `Button.Base`, `TextBox` and `PasswordBox` have an `IsKeyboardFocused` trigger, and on buttons that trigger is just a 1 px border colour change from `Accent.Dim` to `Accent`. Keyboard focus on nav items, toggles, theme cards and sliders is invisible. | `Themes/Controls.xaml` lines 119, 192, 226, 389, 450 |
| Screen readers | The app sets no `AutomationProperties` anywhere. Icon-only buttons (`Button.Chrome`: minimise, maximise and close glyphs `&#xE8BB;` and so on) announce as private-use characters. Inputs in `SettingRow` grids have a sibling `TextBlock` label that UI Automation does not associate with them. The phrase chips, theme cards and OCR checkboxes have composite content, so they have no accessible name. | all `Windows/*.xaml`, `Controls/HotkeyBox.cs` |
| High contrast | Not handled. The five palettes are fixed colours, so Windows high-contrast themes are ignored. | `Services/ThemeManager.cs`, `Themes/Palette.*.xaml` |
| Contrast | Measured from the palette files (WCAG 2.x ratio, worst case over `Bg.Window`/`Bg.Panel`/`Bg.PanelAlt`/`Bg.Input`): `Text.Muted` (all `Text.Caption` text, 12 px) is 2.45:1 in parchment and 3.6–4.1:1 in the others. `Danger` (error text) is 2.65–3.7:1. Both should be at least 4.5:1. The overlay card background alpha is user-controlled (`Overlay.BackgroundOpacity`, 0–1). Below about 0.63–0.68, depending on the theme, `Overlay.Text` over a white game backdrop drops below 4.5:1. | `Themes/Palette.*.xaml`, `Windows/OverlayWindow.xaml(.cs)` |
| Hard-coded colours | `OverlayWindow.xaml` uses `#33000000` (edit frame) and `#99000000` (empty hint). This breaks the palette rule and ignores high contrast. | `Windows/OverlayWindow.xaml` |
| Empty and error states | War Room "Players this match" and "Dispatches" render as empty boxes when there is no data. A missing chat area shows only as caption text. The Reading Chat live view is a blank image. When `phrases.json` fails to parse, `PhraseBook.LoadWithUserFile` silently falls back to built-ins. When the overlay is hidden, no page says so. | `MainWindow.xaml` pages, `Phrases/PhraseBook.cs` |
| Reply box | Single-line input with no history: you cannot recall a reply you sent a moment ago. There is no length feedback. Quick-phrase chips are always in phrasebook order whatever the target language, so phrases with no translation in the target language sit among the useful ones. The clipboard retry helper is private to `ComposeWindow`. | `Windows/ComposeWindow.xaml(.cs)`, `Core/Phrases/PhraseBook.cs` |
| Overlay | One layout only (two to four lines per message). Click-through means you cannot select or copy a message. Edit mode (`Ctrl+Shift+E`) is the only interactive state, and it only moves and resizes. | `Windows/OverlayWindow.xaml(.cs)`, `Windows/OverlayItem.cs` |

### What is weak or missing for production

1. A stranger who downloads `Envoy.exe` lands on a dense settings window. The two most common silent failures are a
   missing OCR pack for the opponent's script and exclusive fullscreen. Nothing detects either, and nothing tells the
   user about them.
2. Important problems that appear later (an OCR pack still missing, the translator failing repeatedly, a new
   version) are only visible inside pages the player won't open mid-game.
3. Keyboard-only and screen-reader users cannot tell where focus is or what a control is. Windows high-contrast
   users get a dark gold theme that ignores their system colours. Some text is below the WCAG AA contrast ratio in
   every theme.
4. Small but frequent annoyances in the two windows used in game: no reply recall, no length feedback, chip order
   that ignores the language you are writing to, an overlay that can't be compacted, and messages that can't be
   copied.

## Goals / Non-goals

**Goals**

- A re-openable, six-step **setup guide** window: language, game and display mode, chat area, OCR packs (status plus
  one-click install), test translation, hotkey cheat sheet. It opens automatically for new users only.
- **Notices** for three actionable events (OCR pack missing, provider failing, update available). They appear as
  tray balloons with per-key rate limiting, a session cap, and quiet deferral while the game has focus. They are
  mirrored in a War Room list where they can be dismissed.
- **Empty and error states** on every settings page, driven by one pure `PageHints` module and one themed `Callout`
  style.
- **Keyboard and screen-reader support**: a visible focus ring on every focusable control, accessible names and help
  text on interactive controls, a lint in `typecheck.sh`, and a runtime audit in the smoke test.
- **High-contrast support** through a sixth palette built from `SystemColors`, chosen automatically when Windows
  high contrast is on. Palette text contrast of at least 4.5:1 is enforced by a unit test, and the overlay background
  has a minimum opacity that keeps text readable.
- Reply box: **history** (Up/Down), a **character counter** with an optional limit, and **per-target-language chip
  ordering** by use.
- Overlay: an optional **compact mode**, and **per-message copy** while in edit mode.
- All new logic lives in `Envoy.Core` with unit tests. Every new window, palette and state is exercised in
  `SmokeTest.cs`.

**Non-goals**

- No localisation of Envoy's own UI strings. The UI stays English. Strings are kept in one place so localisation is
  possible later.
- No native Windows toast API (`Microsoft.Toolkit.Uwp.Notifications`, AppUserModelID or Start-menu shortcut). The
  portable exe has no shortcut to register against. `NotifyIcon.ShowBalloonTip` appears as a toast on Windows 10/11
  without any of that.
- No replacement for 02's error `ToastWindow`. Errors and crashes stay there. This area covers *advisories* only.
- No changes to how updates are downloaded or installed (that is 06-distribution-updates). This area only *announces*
  an `UpdateInfo` that already exists.
- No UI scaling setting, no screen-reader live announcements of incoming chat (Herald mode already speaks it), no
  game-specific chat length limits (they are not known; the limit is user-configurable).
- No game memory access, injection or hooks. The display-mode check uses only window-manager queries
  (`GetWindowRect`, window style, `MonitorFromWindow`/`GetMonitorInfo`, `IsIconic`) on the game's top-level window
  handle, which Envoy already has from `GameDetector`.

## Requirements

### Setup guide (onboarding)

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUX-1 | MUST | A `SetupWizardWindow` presents six steps in this fixed order: Language, Game & display, Chat area, Text recognition (OCR), Test translation, Hotkeys. Back, Next (Finish on the last step) and Skip setup buttons are always visible, along with a "Step n of 6" indicator. |
| REQ-OUX-2 | MUST | On start-up the guide opens automatically, instead of `MainWindow`, only when `OnboardingGate.ShouldAutoOpen(settings)` is true: not completed, not skipped, and not an existing user (`FirstRunCompleted` with at least one saved region). |
| REQ-OUX-3 | MUST | The guide can be reopened at any time from a tray menu item "Setup guide…", an About-page button and a War Room button. Reopening starts at the first step unless a step is requested (for example Hotkeys from "Hotkey cheat sheet…"). |
| REQ-OUX-4 | MUST | Every edit in the guide writes the same `AppSettings` properties the settings pages use and goes through `AppHost.ApplySettings()`, so the guide and the pages can never disagree. |
| REQ-OUX-5 | MUST | The Game & display step shows the detected game and a `DisplayModeVerdict` (NotRunning, Windowed, Borderless, LikelyExclusiveFullscreen, Unknown) computed by `DisplayModeAdvisor.Classify`, with a "Check again" button. It never blocks progress. |
| REQ-OUX-6 | MUST | The Chat area step runs the existing region selector (the guide hides itself while selecting and comes back afterwards) and shows the saved region and the live capture preview (`CapturePreviewViewModel`, active only while this step is visible). |
| REQ-OUX-7 | MUST | The OCR step lists `OcrReadiness.Recommend(...)`: your language first, then wanted and common opponent languages, each Installed or Missing. Each missing one has an Install button that runs the UIF `InstallOcrCommand`. A "Check again" button re-queries Windows and re-applies settings. |
| REQ-OUX-8 | MUST | The Test translation step translates `TranslationTestText.SampleInput` (editable) with the configured provider stack. It shows the result or the error, and the name of the provider the text was sent to, before the user presses Translate. |
| REQ-OUX-9 | MUST | The Hotkeys step shows `HotkeyCheatSheet.Build(...)`: every action, its combination or "Not set", and "Taken by another app" for combinations that failed to register. |
| REQ-OUX-10 | MUST | Finish sets `Onboarding.Completed = true` and `FirstRunCompleted = true`, saves, closes the guide and opens `MainWindow` on the War Room. Skip setup or closing the window sets `Onboarding.Skipped = true` and remembers `Onboarding.LastStep`. |
| REQ-OUX-11 | SHOULD | The step list in the guide header shows each step's `StepStatus` (Not visited, Done, Needs attention, Skipped) from `OnboardingFlow.Evaluate`. The last step summarises the steps that need attention, with a "Go to step" link for each. |
| REQ-OUX-12 | SHOULD | While `Onboarding.Completed` is false, the War Room shows a "Finish setup" callout that reopens the guide at `Onboarding.LastStep`. |

### Notices (tray balloons)

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUX-13 | MUST | `NoticeCenter` (Core) decides Show, Defer or Suppress for three notice kinds: `OcrLanguageMissing` (key `ocr-missing:<sorted codes>`), `ProviderFailing` (key `provider:<id>`) and `UpdateAvailable` (key `update:<tag>`). |
| REQ-OUX-14 | MUST | Rate limits: at least 60 s between any two balloons (a later one is deferred, not dropped); at most 5 balloons per session; each OCR key at most once per session; each provider key at most once per 30 min; each update tag at most once ever (persisted in `Notifications.LastAnnouncedUpdate`). |
| REQ-OUX-15 | MUST | `ProviderFailing` is raised only after 3 failures of the same provider within 2 min (`ProviderFailureMonitor`). It re-arms only after a success from that provider. A success also resolves the notice. |
| REQ-OUX-16 | MUST | Notice text never contains chat text, API keys or raw exception messages. It names the provider or languages, says what happens now, and says where to look. |
| REQ-OUX-17 | MUST | Clicking a balloon runs its action: OCR → `MainWindow` Reading Chat page; provider → Translation page; update → the release page URL from `UpdateInfo.PageUrl`. |
| REQ-OUX-18 | MUST | `Notifications.Enabled` and one toggle per kind (all default on) are editable on the Appearance page. Turning a kind off stops its balloons but still lists it in the War Room. |
| REQ-OUX-19 | SHOULD | With `Notifications.QuietWhileGameFocused` (default on), a notice that would show while the detected game window is in the foreground is deferred. It is released on the next 3 s game-timer tick where the game is not in the foreground, subject to REQ-OUX-14. |
| REQ-OUX-20 | SHOULD | The War Room lists `NoticeCenter.Active` notices with their action button and a "Don't remind me" button that adds the key to `Notifications.Dismissed`. A dismissed key is never shown or listed again. |
| REQ-OUX-21 | MUST | While the setup guide is open, `NoticeCenter.Hold` defers every notice. They are re-evaluated when the guide closes. |

### Empty and error states

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUX-22 | MUST | Every settings page shows a themed callout (`Callout` style: Info, Warning, Error) for its empty or error condition, with text and an optional action from `PageHints`. War Room: no messages, no players, capture not running or failing, provider failing. Translation: credentials missing for a used provider. Reading Chat: no region, OCR unavailable, no preview frame yet. Overlay: overlay hidden. Replying: no reply hotkey or it is taken. Phrasebook: `phrases.json` unreadable. Hotkeys: unset or taken combinations. Appearance: high contrast active. About: none required. |
| REQ-OUX-23 | MUST | `PhraseBook.LoadWithUserFile` reports a user-file parse error through a new `out string? error` overload instead of swallowing it. Existing callers keep their behaviour. |

### Keyboard, focus and screen readers

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUX-24 | MUST | No style or element in `src/Envoy.App` sets `FocusVisualStyle` to `{x:Null}` unless it also sets `Focusable=False`. Focusable controls use a shared `FocusVisual.Ring` style (2 px `Focus.Ring` brush, 2 px outset), enforced by `tools/typecheck/check_a11y.py`. |
| REQ-OUX-25 | MUST | Every palette defines `Focus.Ring.Color`/`Focus.Ring`. The colour has at least 3:1 contrast against `Bg.Window`, `Bg.Panel`, `Bg.PanelAlt` and `Bg.Input` (unit test). |
| REQ-OUX-26 | MUST | Every `Button`, `TextBox`, `PasswordBox`, `ComboBox`, `Slider`, `HotkeyBox`, `SecretBox`, and every `CheckBox`/`RadioButton` without a string `Content`, in `src/Envoy.App/**/*.xaml` outside templates of `Themes/Controls.xaml`, has `AutomationProperties.Name` or `AutomationProperties.LabeledBy` (enforced by `check_a11y.py`). |
| REQ-OUX-27 | MUST | The Windows smoke test walks each window (all `MainWindow` pages, every guide step, compose, lookup, overlay in edit mode). It fails if any visible, enabled, focusable control has an empty UI Automation name. |
| REQ-OUX-28 | SHOULD | Controls whose row has a caption get `AutomationProperties.HelpText` equal to the caption text. |
| REQ-OUX-29 | MUST | In `SetupWizardWindow` and `ComposeWindow`, Tab cycles through every interactive control without leaving the window. Enter activates the default action (Next/Finish; Send) and Esc the cancel action (Skip setup; close). |
| REQ-OUX-30 | SHOULD | In `MainWindow`, Ctrl+Tab and Ctrl+Shift+Tab move to the next and previous page, wrapping around. |
| REQ-OUX-31 | SHOULD | The smoke test checks keyboard reachability: Tab traversal from the first focusable element of the guide and of the compose window visits every visible, enabled, focusable control. |

### High contrast and colour contrast

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUX-32 | MUST | A sixth palette `Palette.highcontrast.xaml` defines every palette key from `SystemColors` (`x:Static`) only. It is applied when `ThemeResolver.Resolve` returns `highcontrast`. |
| REQ-OUX-33 | MUST | `ThemeResolver.Resolve(themeSetting, gameThemeId, systemHighContrast, mode)` returns `highcontrast` when mode is `Always`, or `FollowWindows` with system high contrast on. Otherwise it returns the existing theme choice. `AppHost.ApplyTheme` uses it. |
| REQ-OUX-34 | MUST | When Windows high contrast turns on or off, or the high-contrast theme changes, Envoy re-applies the palette within one dispatcher cycle without a restart (`SystemParameters.StaticPropertyChanged`). |
| REQ-OUX-35 | MUST | In the five game palettes, `Text.Primary`, `Text.Secondary`, `Text.Muted` and `Danger` each have at least 4.5:1 contrast against `Bg.Window`, `Bg.Panel`, `Bg.PanelAlt` and `Bg.Input`. `Overlay.Text` and `Overlay.Sender` have at least 4.5:1 against `Overlay.Bg`. A unit test reads the palette XAML files and enforces this. |
| REQ-OUX-36 | MUST | With `Overlay.EnsureReadableText` (default on), the overlay card background alpha is `max(BackgroundOpacity, ContrastMath.MinimumBackgroundAlpha(Overlay.Text, Overlay.Bg, 4.5))`, computed over worst-case white, mid-grey and black backdrops. In the high-contrast palette it is 1. |
| REQ-OUX-37 | SHOULD | When the minimum raises the user's value, the Overlay page shows "Raised to NN% so text stays readable" under the background-opacity slider. |
| REQ-OUX-38 | MUST | `OverlayWindow.xaml` contains no literal colours. The edit frame and empty hint use palette keys. |

### Reply box

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUX-39 | MUST | `ReplyHistory` (capacity 50, newest first, consecutive and exact duplicates moved to the front, blank ignored) records the user's typed text on each successful send or copy. It is kept in memory only, never written to disk. |
| REQ-OUX-40 | MUST | In the reply box, Up recalls the next older entry (saving the unsent draft on the first press) and Down the next newer one. Moving past the newest restores the draft. Typing while browsing ends browsing and keeps the text. |
| REQ-OUX-41 | MUST | When `Compose.ShowCharacterCount` is on (default), the reply box shows the length of the text that will be sent (after `AppendOriginal`), counted in text elements. It shows "n / limit" in `Danger` when it exceeds `Compose.MaxMessageLength` (0 = no limit, the default). Sending is never blocked. |
| REQ-OUX-42 | MUST | With `Compose.PhraseOrder = MostUsed` (default), chips are ordered as follows: phrases with a translation in the current target language first, then the rest. Within each group they are sorted by use count for that target language, descending, then by phrasebook order. `BookOrder` keeps today's order. |
| REQ-OUX-43 | MUST | A send or copy whose input exactly matches a phrase (`PhraseBook.FindByText`) increments `Compose.PhraseUsage[target][phraseId]`. Counts saturate at 999, and each language keeps at most 100 ids (lowest dropped). |

### Overlay

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUX-44 | MUST | With `Overlay.Compact` on, each message renders on a single wrapped line (badge, sender, translation), with no original, provider or "translating…" line. A pending message shows a trailing "…" and a failed one a trailing "⚠" in `Danger`. Padding is `8,3`, the gap 2 px, and the font size `max(11, FontSize - 2)`. |
| REQ-OUX-45 | MUST | In overlay edit mode each message card shows a Copy button that puts `OverlayCopyText.Format(...)` on the clipboard (sender, translation, and original on a second line when different). For 1.5 s it shows "Copied" or "Couldn't copy". |
| REQ-OUX-46 | MUST | Compact mode is toggled from the Overlay page, applies live, and both layouts render in the smoke test under every palette. |

## Design

### Settings added (`src/Envoy.Core/Settings/AppSettings.cs`, shared file)

```csharp
public sealed class AppSettings
{
    // … existing …
    public OnboardingSettings Onboarding { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public AccessibilitySettings Accessibility { get; set; } = new();
}

public sealed class OnboardingSettings
{
    public bool Completed { get; set; }              // false
    public bool Skipped { get; set; }                // false
    /// <summary>OnboardingStep name to resume at; "" = start.</summary>
    public string LastStep { get; set; } = "";
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool OcrLanguageMissing { get; set; } = true;
    public bool ProviderFailing { get; set; } = true;
    public bool UpdateAvailable { get; set; } = true;
    public bool QuietWhileGameFocused { get; set; } = true;
    /// <summary>Notice keys the user chose "Don't remind me" for.</summary>
    public List<string> Dismissed { get; set; } = [];
    /// <summary>Release tag last announced, so each version is announced once.</summary>
    public string LastAnnouncedUpdate { get; set; } = "";
}

public enum HighContrastMode { FollowWindows, Always, Never }

public sealed class AccessibilitySettings
{
    public HighContrastMode HighContrast { get; set; } = HighContrastMode.FollowWindows;
}

// OverlaySettings additions
public bool Compact { get; set; }                       // false
public bool EnsureReadableText { get; set; } = true;

// ComposeSettings additions
public enum PhraseOrder { MostUsed, BookOrder }
public PhraseOrder PhraseOrder { get; set; } = PhraseOrder.MostUsed;
/// <summary>target language code → phrase id → times sent. Phrase ids only, never free text.</summary>
public Dictionary<string, Dictionary<string, int>> PhraseUsage { get; set; } = new();
public int MaxMessageLength { get; set; }               // 0 = no limit known
public bool ShowCharacterCount { get; set; } = true;
```

These are all additive with defaults, so existing `settings.json` files load unchanged. If 02's `SettingsMigrations`
exists, no migration step is needed. Existing users are not shown the guide (REQ-OUX-2): `FirstRunCompleted` is
true and they have a region.

### Core: onboarding (`src/Envoy.Core/Onboarding/`, new)

```csharp
namespace Envoy.Core.Onboarding;

public enum OnboardingStep { Language, Game, ChatArea, OcrLanguages, TestTranslation, Hotkeys }
public enum StepStatus { NotVisited, Done, NeedsAttention, Skipped }

public sealed record OnboardingFacts(
    bool LanguageChosen, DisplayModeVerdict DisplayMode, bool HasRegion,
    int MissingOcrCount, bool? TestTranslationSucceeded, int HotkeyProblems);

public sealed class OnboardingFlow(OnboardingStep start = OnboardingStep.Language)
{
    public static IReadOnlyList<OnboardingStep> Steps { get; }        // enum order
    public static string TitleOf(OnboardingStep step);                // "Your language", "Game & display", "Chat area",
                                                                      // "Text recognition", "Test translation", "Hotkeys"
    public OnboardingStep Current { get; }
    public int Index { get; }                                         // 0-based
    public int Count => 6;
    public bool CanGoBack { get; }                                    // Index > 0
    public bool IsLast { get; }
    public IReadOnlySet<OnboardingStep> Visited { get; }              // includes start
    public bool Next();                                               // false on last
    public bool Back();                                               // false on first
    public void GoTo(OnboardingStep step);
    public event Action? Changed;

    /// NotVisited if !visited; NeedsAttention if: Game → LikelyExclusiveFullscreen; ChatArea → !HasRegion;
    /// OcrLanguages → MissingOcrCount > 0; TestTranslation → TestTranslationSucceeded == false;
    /// Hotkeys → HotkeyProblems > 0; Skipped if visited and TestTranslationSucceeded == null (TestTranslation only);
    /// otherwise Done.
    public static StepStatus Evaluate(OnboardingStep step, OnboardingFacts facts, bool visited);
}

public static class OnboardingGate
{
    public static bool ShouldAutoOpen(AppSettings s) =>
        !s.Onboarding.Completed && !s.Onboarding.Skipped && !(s.FirstRunCompleted && s.Capture.Regions.Count > 0);
    /// Enum.TryParse(LastStep) else Language.
    public static OnboardingStep ResumeStep(AppSettings s);
}

public enum DisplayModeVerdict { NotRunning, Windowed, Borderless, LikelyExclusiveFullscreen, Unknown }

/// <param name="Window">Game top-level window rect, physical px.</param>
/// <param name="Monitor">Bounds of the monitor holding it, physical px.</param>
public sealed record GameWindowFacts(bool GameRunning, bool HasWindow, bool Minimized, bool HasCaption,
                                     ScreenRegion Window, ScreenRegion Monitor);

public static class DisplayModeAdvisor
{
    /// !GameRunning → NotRunning; !HasWindow → Unknown; Minimized → LikelyExclusiveFullscreen
    /// (exclusive-fullscreen games minimise when you alt-tab to Envoy); HasCaption → Windowed;
    /// Window covers Monitor within 2 px on every edge → Borderless; else Unknown.
    public static DisplayModeVerdict Classify(GameWindowFacts f);
    /// One-sentence advice per verdict. It never names game menu paths, only "the game's display/graphics settings".
    public static string Describe(DisplayModeVerdict v);
}
```

`ScreenRegion` is the existing record in `Envoy.Core.Settings`.

### Core: OCR readiness (`src/Envoy.Core/Languages/OcrReadiness.cs`, new)

```csharp
public sealed record OcrRecommendation(string Code, string Name, bool Installed, bool IsMine, bool Wanted);

public static class OcrReadiness
{
    public static IReadOnlyList<string> CommonOpponentLanguages { get; } = ["zh-CN", "ko", "ja", "ru"];

    /// Wanted codes (normalised, distinct, plus myLanguage) whose OCR pack is not installed.
    /// Empty `wanted` means "every installed pack", so the result contains at most myLanguage.
    /// A Latin-script language other than "vi" counts as covered when any Latin-script pack is installed
    /// (mirrors MainViewModel.LoadOcrLanguages).
    public static IReadOnlyList<string> MissingWanted(IEnumerable<string> wanted, string myLanguage, IEnumerable<string> installedCodes);

    /// Your language first, then wanted, then CommonOpponentLanguages, distinct, with installed/missing status.
    public static IReadOnlyList<OcrRecommendation> Recommend(string myLanguage, IEnumerable<string> wanted, IEnumerable<string> installedCodes);
}
```

### Core: hotkey cheat sheet (`src/Envoy.Core/Settings/HotkeyCheatSheet.cs`, new)

```csharp
public enum HotkeyRowStatus { Ok, NotSet, Taken }
public sealed record HotkeyRow(string Action, string Combination, HotkeyRowStatus Status);

public static class HotkeyCheatSheet
{
    /// Fixed order: Reply box, Show/hide overlay, Pause/resume reading, Select chat area, Move overlay (edit mode,
    /// copy messages), Scan & translate. Combination "" → "Not set". `isTaken(combo)` → Taken.
    public static IReadOnlyList<HotkeyRow> Build(HotkeySettings keys, Func<string, bool> isTaken);
    /// Plain text, one "Action — Combination" line per row (for "Copy" on the Hotkeys step; COULD).
    public static string Format(IReadOnlyList<HotkeyRow> rows);
}
```

`App.RegisterHotkeys` additionally records the failed **combinations** in
`public IReadOnlyCollection<string> FailedHotkeyCombos` (shared `App.xaml.cs`). `IShell` gains
`bool IsHotkeyTaken(string combo)`.

### Core: notices (`src/Envoy.Core/Notifications/`, new)

```csharp
namespace Envoy.Core.Notifications;

public enum NoticeKind { OcrLanguageMissing, ProviderFailing, UpdateAvailable }
public enum NoticeDecision { Show, Defer, Suppress }
public static class NoticeActions { public const string OpenCapture = "open-capture", OpenTranslation = "open-translation", OpenUpdate = "open-update"; }

public sealed record Notice(NoticeKind Kind, string Key, string Title, string Text, string ActionId, bool IsWarning);

public static class NoticeFactory
{
    /// null when `missingCodes` is empty. Key = "ocr-missing:" + codes sorted ordinal, comma-joined.
    /// Title "Text recognition pack missing"; Text "Envoy can't read Japanese, Korean chat until Windows' free
    /// text recognition pack is installed. Click to install."
    public static Notice? OcrMissing(IReadOnlyList<string> missingCodes);
    /// Text "{providerName} failed several times in a row. Envoy is using your fallback provider. Click for details."
    /// (no exception text: REQ-OUX-16).
    public static Notice ProviderFailing(string providerId, string providerName);
    /// Text "Envoy {tag} is available (you have v{current}). Click to see what's new."
    public static Notice UpdateAvailable(string tag, string currentVersion);
}

public sealed record NoticePolicy
{
    public TimeSpan GlobalMinInterval { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxPerSession { get; init; } = 5;
    public TimeSpan ProviderCooldown { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>UI-thread only (not thread-safe). Holds a reference to the live NotificationSettings.</summary>
public sealed class NoticeCenter(NotificationSettings settings, TimeProvider? clock = null, NoticePolicy? policy = null)
{
    public IReadOnlyList<Notice> Active { get; }       // latest notice per key, minus dismissed/resolved, newest first
    public bool IsHeld { get; }
    public event Action? Changed;                      // Active changed

    /// Order of checks: dismissed → Suppress (not listed). Otherwise add to Active. Then: !Enabled or kind toggle off →
    /// Suppress. Update tag == LastAnnouncedUpdate → Suppress. Per-key rule (OCR: shown this session; provider:
    /// shown within ProviderCooldown) → Suppress. Session cap reached → Suppress. Held, or (gameInForeground &&
    /// QuietWhileGameFocused), or GlobalMinInterval not elapsed → Defer (queued, one per key, latest wins). Else Show
    /// (records time/key; for updates sets settings.LastAnnouncedUpdate).
    public NoticeDecision Submit(Notice notice, bool gameInForeground);

    /// Next deferred notice that would now be Show (re-runs the Submit rules), or null. Call on a timer.
    public Notice? TakeDeferred(bool gameInForeground);

    public void Hold(bool held);
    public void Resolve(string key);                   // condition cleared: drop from Active and the deferred queue
    public void Dismiss(string key);                   // add to settings.Dismissed (distinct) + Resolve
}

/// <summary>Thread-safe (internal lock): provider failure callbacks arrive on pool threads.</summary>
public sealed class ProviderFailureMonitor(int threshold = 3, TimeSpan? window = null /*2 min*/, TimeProvider? clock = null)
{
    /// true exactly once when `threshold` failures fall within `window`; false afterwards until RecordSuccess.
    public bool RecordFailure(string providerId);
    public void RecordSuccess(string providerId);
}
```

### Core: UX helpers

**`src/Envoy.Core/Ux/PageHints.cs`** (new). Pure text and decision logic for the callouts:

```csharp
namespace Envoy.Core.Ux;

public enum HintSeverity { Info, Warning, Error }
public static class HintActions { public const string SelectRegion = "select-region", ShowSamples = "show-samples",
    ToggleCapture = "toggle-capture", OpenTranslation = "open-translation", OpenCapture = "open-capture",
    ShowOverlay = "show-overlay", OpenHotkeys = "open-hotkeys", EditPhrases = "edit-phrases", OpenSetup = "open-setup"; }
public sealed record PageHint(HintSeverity Severity, string Text, string? ActionLabel = null, string? ActionId = null);

/// Mirrors App's WatcherState without depending on it.
public enum CaptureCondition { Running, Paused, NoRegion, WaitingForGame, Error }

public static class PageHints
{
    public static PageHint? Dispatches(int messageCount, CaptureCondition capture);   // e.g. 0 + Running → Info "No messages yet. They appear here as soon as someone types in chat." + Show sample messages
    public static PageHint? Players(int playerCount);                                 // 0 → Info "Players appear when they write in chat."
    public static PageHint? Capture(CaptureCondition capture, string? error);         // NoRegion → Warning + Select chat area; Error → Error "Can't read chat: {error}"; Paused → Info + Resume
    public static PageHint? OcrAvailability(int statusCount, int missingWanted);      // 0 statuses → Error "Windows text recognition isn't available on this PC."; missing → Warning
    public static PageHint? ProviderCredentials(string providerName, bool hasCredential); // !has → Warning "{name} needs an API key. Until you add one, Envoy uses the next provider."
    public static PageHint? ProviderFailing(string? providerStatusIfFailing);
    public static PageHint? OverlayHidden(bool visible, string toggleHotkey);         // hidden → Info "The overlay is hidden. Press {hotkey} or Show it." + ShowOverlay
    public static PageHint? ReplyHotkey(string composeHotkey, bool taken);            // "" → Warning; taken → Error + OpenHotkeys
    public static PageHint? Phrasebook(string? loadError);                            // error → Error "Your phrases.json couldn't be read ({error}). Built-in phrases are in use." + EditPhrases
    public static PageHint? Hotkeys(int notSet, int taken);
    public static PageHint? Appearance(bool highContrastActive);                      // Info "Windows high contrast is on. Envoy is using your system colours."
    public static PageHint? Setup(bool onboardingCompleted);                          // !completed → Info "Finish setting up Envoy." + OpenSetup
}
```

`App` maps `WatcherState` → `CaptureCondition` in one switch in `MainViewModel` (`Stopped`→Paused, `NoRegion`,
`Running`, `Idle`→WaitingForGame, `Error`).

**`src/Envoy.Core/Ux/ContrastMath.cs`** (new):

```csharp
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string hex);            // "#RRGGBB" or "#AARRGGBB" (alpha ignored)
}
public static class ContrastMath
{
    public static double RelativeLuminance(Rgb c);  // WCAG 2.x sRGB formula
    public static double Ratio(Rgb a, Rgb b);       // (L1+0.05)/(L2+0.05), symmetric, 1..21
    public static Rgb Composite(Rgb top, double alpha, Rgb backdrop);
    /// Smallest alpha in [0,1] (steps of 0.01) such that Ratio(text, Composite(bg, alpha, d)) >= target for every d in
    /// {white, #808080, black}; 1.0 if even an opaque bg can't reach it.
    public static double MinimumBackgroundAlpha(Rgb text, Rgb bg, double target);
    public static double EffectiveBackgroundAlpha(double userAlpha, Rgb text, Rgb bg, bool ensureReadable, double target = 4.5);
}
```

**`src/Envoy.Core/Ux/ThemeResolver.cs`** (new):

```csharp
public static class ThemeResolver
{
    public const string HighContrast = "highcontrast", MatchGame = "match-game", Default = "imperial";
    /// Always → HighContrast; FollowWindows && systemHighContrast → HighContrast; else themeSetting == MatchGame ?
    /// (gameThemeId ?? Default) : themeSetting; an id not in knownThemeIds → Default.
    public static string Resolve(string themeSetting, string? gameThemeId, bool systemHighContrast,
                                 HighContrastMode mode, IReadOnlyCollection<string> knownThemeIds);
}
```

### Core: compose and overlay helpers

**`src/Envoy.Core/Compose/ReplyHistory.cs`** (new):

```csharp
namespace Envoy.Core.Compose;
public sealed class ReplyHistory(int capacity = 50)
{
    public IReadOnlyList<string> Entries { get; }    // newest first
    public bool IsBrowsing { get; }
    public void Add(string text);                    // trim; ignore blank; remove equal (Ordinal) entry; insert at 0; cap; ends browsing
    public string? Older(string currentText);        // first call saves currentText as draft; null at oldest (or empty history)
    public string? Newer();                          // newer entry; past newest → draft and ends browsing; null if not browsing
    public void EndBrowsing();                       // keeps nothing; called when the user types
}
```

**`src/Envoy.Core/Compose/ReplyLength.cs`** (new):

```csharp
public sealed record ReplyLengthInfo(int Count, int Limit, bool OverLimit, string Text);   // Text "42" or "42 / 120"
public static class ReplyLength
{
    /// Count = StringInfo.LengthInTextElements (an emoji or a CJK character = 1). Limit <= 0 → never OverLimit.
    public static ReplyLengthInfo Measure(string toSend, int limit);
}
```

**`src/Envoy.Core/Compose/PhraseOrdering.cs`** (new):

```csharp
public static class PhraseOrdering
{
    public const int MaxCount = 999, MaxIdsPerLanguage = 100;
    public static IReadOnlyList<Phrase> Order(IReadOnlyList<Phrase> phrases, string targetLanguage,
        IReadOnlyDictionary<string, Dictionary<string, int>> usage, PhraseOrder mode);
    /// Increments usage[Normalize(target)][phraseId], saturating at MaxCount; trims the lowest counts beyond MaxIdsPerLanguage.
    public static void RecordUse(Dictionary<string, Dictionary<string, int>> usage, string targetLanguage, string phraseId);
}
```

**`src/Envoy.Core/Pipeline/OverlayCopyText.cs`** (new):

```csharp
public static class OverlayCopyText
{
    /// "{Sender}: {DisplayText}" (no prefix without a sender), plus "\n{Chat.Text}" when the status is Translated and
    /// the original differs; for Pending/Failed just the original.
    public static string Format(IncomingMessage message);
}
```

**`src/Envoy.Core/Phrases/PhraseBook.cs`** (changed): it adds
`public static PhraseBook LoadWithUserFile(string userFile, out string? error)`. The existing one-argument overload
calls it and discards `error`. `AppHost.ReloadPhrases` and the constructor store `PhrasesLoadError` (string?).

### App: setup guide

**`src/Envoy.App/Windows/SetupWizardWindow.xaml(.cs)`** (new, about 280 lines of XAML):

- **Window:** about 760×560, `WindowStyle=None` with the same title-bar chrome as `MainWindow` (the `Button.Chrome`
  close button has `AutomationProperties.Name="Close setup"`), `Bg.Window` background, `ResizeMode=CanMinimize`,
  `KeyboardNavigation.TabNavigation="Cycle"` on the root.
- **Layout:** a left column with the step list (an `ItemsControl` of `StepItem {Title, Status, IsCurrent}`). Each
  item shows a status glyph, and `AutomationProperties.Name` is "{Title}, {status}". The right column holds the six
  step panels; only the current one is visible (`BoolToVisibility` on `IsStep_<Name>` properties). The footer has
  Skip setup (`IsCancel=True`), Back and Next/Finish (`IsDefault=True`, `Button.Primary`).
- **Steps:**
  1. **Language:** `ComboBox` → `Settings.MyLanguage`; `TextBox` → `Settings.MyPlayerName`.
  2. **Game & display:** the detected game name (`Host.GameStatus`), the verdict text from `DisplayModeAdvisor.Describe`,
     a "Check again" button, and the caption "Borderless looks the same as fullscreen and lets Envoy see the chat."
  3. **Chat area:** a "Select chat area" button, `RegionText`, and the live preview `Image` plus the read text from
     `CapturePreviewViewModel`.
  4. **Text recognition:** a list of `OcrRecommendation` rows (name, Installed/Missing badge, an Install button for
     missing ones) and a "Check again" button.
  5. **Test translation:** an input, a Translate button, the result text, and the caption "This sends the sentence to
     {primary provider name}."
  6. **Hotkeys:** a two-column grid of `HotkeyRow`s, an "Open Hotkeys settings" link, and an attention summary.
- **API:** `public SetupWizardWindow(AppHost host, IShell shell, OnboardingStep start)`;
  `public SetupWizardViewModel ViewModel { get; }`.
- **Close:** `OnClosed` calls `ViewModel.Dispose()` (stops the capture preview).

**`src/Envoy.App/ViewModels/SetupWizardViewModel.cs`** (new, about 220 lines). It derives from `ObservableObject` and
implements `IDisposable`. It wraps an `OnboardingFlow` and exposes:

- **Navigation:** `NextCommand`, `BackCommand`, `SkipCommand`, `GoToStepCommand` (`RelayCommand<OnboardingStep>`),
  `CurrentTitle`, `StepText` ("Step 3 of 6"), `NextLabel` ("Next" or "Finish"), `Steps` (`ObservableCollection<StepItem>`).
- **Per step:** `DisplayVerdictText`, `RecheckDisplayCommand`, `SelectRegionCommand`, `RegionText`, `CapturePreview`,
  `OcrRows`, `InstallOcrCommand` (delegates to the UIF implementation, shared as a static helper in
  `ViewModels/OcrInstaller.cs` that both view models call), `RecheckOcrCommand`, `TestInput`, `TestOutput`,
  `TestTranslateCommand` (`AsyncRelayCommand`, uses `TranslationTestText`), `HotkeyRows`.
- **Settings writes:** they go through a `SettingsAutoApplier` (UIF), so edits debounce into `Host.ApplySettings()`.
- **Step changes:** each one sets `Settings.Onboarding.LastStep`, refreshes `Steps` statuses from `OnboardingFacts`,
  and sets `CapturePreview.IsActive = Current == ChatArea`.
- **Finish/Skip:** they raise `public event Action<bool /*completed*/>? Finished`. `App` handles it (see below).

**`src/Envoy.App/Services/DisplayModeProbe.cs`** (new, about 40 lines). It provides
`static GameWindowFacts Read(RunningGame? game)`, which uses `Native.IsWindow`, `IsIconic`, `GetWindowRect`,
`GetWindowLongPtr(GWL_STYLE) & WS_CAPTION`, `MonitorFromWindow` + `GetMonitorInfo`. The last two are new in `Native.cs`.
02 adds `MonitorFromWindow` too; the integrator keeps one declaration. Any Win32 failure → `HasWindow=false` → Unknown.

**`App.xaml.cs`** (shared) gains:

- `private SetupWizardWindow? _wizard;`
- `public void OpenSetupWizard(OnboardingStep? step = null)`: single instance. It activates the existing window and
  calls `GoTo(step)` if one was given. Otherwise it creates the window with `step ?? OnboardingStep.Language`, calls
  `Host.Notices.Hold(true)` and subscribes to `Finished`. On `Finished(completed)` it sets
  `Onboarding.Completed`/`Skipped`, sets `FirstRunCompleted = true` when completed, calls `SaveNow()` and
  `Hold(false)`, and when completed also `ShowMain()` + `Navigate(PageIds.Dashboard)`. Closing with the window's X
  counts as Skip.
- **`OnStartup`:** after `Host.Start()`, `if (OnboardingGate.ShouldAutoOpen(Host.Settings)) OpenSetupWizard(OnboardingGate.ResumeStep(Host.Settings)); else if (!minimized || !FirstRunCompleted) ShowMain();`.
- **`SelectRegion()`:** also hides and re-shows `_wizard`, the same way it does `_main`.
- **Tray menu:** two new items after "Open Envoy": "Setup guide…" → `OpenSetupWizard()` and "Hotkey cheat sheet…" →
  `OpenSetupWizard(OnboardingStep.Hotkeys)`.
- **`IShell`** gains `void OpenSetupWizard(OnboardingStep? step = null)`, `void ShowPage(string pageId)`
  (`ShowMain()` + `_main.Navigate(pageId)`) and `bool IsHotkeyTaken(string combo)`.

### App: notices

**`src/Envoy.App/Services/TrayNotifier.cs`** (new, about 50 lines):

```csharp
internal sealed class TrayNotifier : IDisposable
{
    public TrayNotifier(System.Windows.Forms.NotifyIcon tray, Action<string> runAction);
    /// ShowBalloonTip(8000, title, text, IsWarning ? Warning : Info); remembers ActionId for BalloonTipClicked.
    /// BalloonTipClosed clears it. Never throws (logs via Debug.WriteLine / 02's ILog if present).
    public void Show(Notice notice);
    public void Dispose();                       // unsubscribes
}
```

**`AppHost.cs`** (shared) gains:

- `public NoticeCenter Notices { get; }`, constructed with `Settings.Notifications`.
- `public event Action<Notice>? NoticeReady` (raised on the UI thread).
- `private readonly ProviderFailureMonitor _providerFailures = new();`
- `public ReplyHistory ReplyHistory { get; } = new();`
- `public string? PhrasesLoadError { get; private set; }`
- `public bool IsGameInForeground => _game is { MainWindow: var h } && h != 0 && Native.GetForegroundWindow() == h;`
- `private void Submit(Notice n) { if (Notices.Submit(n, IsGameInForeground) == NoticeDecision.Show) NoticeReady?.Invoke(n); }`
- **OCR check** `CheckOcrReadiness()`: after `ConfigureOcr()` in `Start()` and in `ApplySettings()`, compute
  `OcrReadiness.MissingWanted(Settings.Capture.OcrLanguages, Settings.MyLanguage, OcrService.InstalledLanguages().Select(l => l.Code))`.
  If it is non-empty, `Submit(NoticeFactory.OcrMissing(missing))`. Then `Resolve` every *other* active `ocr-missing:`
  key, so installing one pack replaces the notice. Wrapped in `try/catch (Exception)` because OCR is unavailable on
  some Windows editions.
- **`BuildTranslator`** failure callback (pool thread): `if (_providerFailures.RecordFailure(id)) _ui.BeginInvoke(() => Submit(NoticeFactory.ProviderFailing(id, name)));`.
- **`OnMessage`:** when the status is `Translated` and `ProviderId` is a catalog provider,
  `_providerFailures.RecordSuccess(id); Notices.Resolve("provider:" + id);`.
- **`CheckForUpdatesAsync`:** when `info is not null`,
  `_ui.BeginInvoke(() => Submit(NoticeFactory.UpdateAvailable(info.Tag, CurrentVersion.ToString(3))))`.
- **`RefreshGame`** (the existing 3 s timer): `while (Notices.TakeDeferred(IsGameInForeground) is { } n) NoticeReady?.Invoke(n);`.
  In practice it releases at most one per tick, because of the global interval.
- **`ApplyTheme()`:** uses `ThemeResolver.Resolve(Settings.Theme, GameCatalog.Find(CurrentGameId)?.ThemeId, SystemParameters.HighContrast, Settings.Accessibility.HighContrast, ThemeManager.Themes.Select(t => t.Id).ToList())`
  and calls `ThemeManager.Apply(id, force: id == ThemeResolver.HighContrast)`.

**Integration notes.**

- **With 03:** if `ProviderHealthRegistry` exists, the integrator may replace `ProviderFailureMonitor` with "state
  became `AuthFailed`, `QuotaExceeded` or `CoolingDown`" on `ProviderHealthChanged`. Keys, texts and policy are
  unchanged.
- **With 02:** `ToastService` keeps error toasts. Notices never use `ToastWindow`, and 02's sources (capture `Error`,
  save failure, crash) are not notices, so nothing is announced twice.
- **With 06:** if 06 owns update UX, it calls `Host.Notices`/`NoticeFactory.UpdateAvailable` instead of duplicating
  the announcement.

**`App.xaml.cs`** creates `_notifier = new TrayNotifier(_tray, RunNoticeAction)` after `CreateTray()` and adds
`Host.NoticeReady += _notifier.Show`. `RunNoticeAction` maps `open-capture` → `ShowPage(PageIds.Capture)`,
`open-translation` → `ShowPage(PageIds.Translation)`, and `open-update` → `OpenUrl(Host.Update?.PageUrl)` (no-op if
null). `ExitApp` disposes `_notifier` before `_tray`.

### App: empty and error states

- **`Themes/Controls.xaml`:** a new `Callout` style (`TargetType=ContentControl`). Its `ContentTemplate` takes a
  `PageHint`:
  - a `Border` with `Bg.PanelAlt` background and a 3 px left border coloured by `DataTrigger` on `Severity`
    (`Accent`, or `Danger` for Warning and Error);
  - a severity glyph in `Font.Icons`;
  - the `Text` in `Text.Label`;
  - an optional `Button.Ghost` showing `ActionLabel`, bound to `DataContext.RunHintActionCommand` of the ancestor
    `UserControl` with `CommandParameter=ActionId`.

  The style collapses when `Content` is null, using a new `NullToCollapsedConverter` in `Controls/Converters.cs`.
  The callout root has `AutomationProperties.LiveSetting="Polite"` so screen readers announce changes.
- **`MainViewModel`:** a new partial file `ViewModels/MainViewModel.Hints.cs` holds the hint properties:
  - `DispatchesHint`, `PlayersHint`, `CaptureHint`, `ProviderHint` and `SetupHint` (War Room);
  - `CredentialsHint` (Translation);
  - `CaptureHint` and `OcrHint` (Reading Chat);
  - `OverlayHint` (Overlay);
  - `ReplyHotkeyHint` (Replying);
  - `PhrasebookHint` (Phrasebook);
  - `HotkeysHint` (Hotkeys);
  - `AppearanceHint` (Appearance).

  `RefreshStatus()` notifies them (it already runs on the 1 s status timer and after each apply). The War Room hints
  also refresh on `Host.History.CollectionChanged` and `Players.CollectionChanged`. `RunHintActionCommand`
  (`RelayCommand<string>`) maps each `HintActions` id to an existing command or `IShell` call.
- **Pages:** each page gets one `<ContentControl Style="{StaticResource Callout}" Content="{Binding XHint}"/>` under
  its intro paragraph. The War Room also gets one inside the Players and Dispatches frames. The Reading Chat live view
  gets a `TextBlock` "No frame yet. The preview appears while Envoy is reading chat." when `CapturePreview.Image` is
  null.
- **War Room notices (REQ-OUX-20):** an `ItemsControl` bound to `Host.Notices.Active` (exposed as
  `ObservableCollection<Notice> Notices` in the VM, refreshed on `Changed`). Each row is a callout with the notice
  action plus a "Don't remind me" `Button.Ghost` → `DismissNoticeCommand(key)`.

### App: focus visuals and accessibility

- **Palettes (all five):** add `<Color x:Key="Focus.Ring.Color">` (the theme's `Accent.Bright` value, which measures at
  least 3.03:1 on all backgrounds today) and `<SolidColorBrush x:Key="Focus.Ring" …/>`. Then fix the failing text
  colours. The values below are suggestions; the unit test is authoritative:

  | Palette | `Text.Muted` | `Danger` |
  | --- | --- | --- |
  | classic | `#8E7C62` → `#9F8E74` | `#C4553C` → `#D07965` |
  | frontier | `#7A8A84` → `#8D9991` | `#B94B3B` → `#CE8176` |
  | imperial | `#7C8597` → `#858D9B` | `#C9573F` → `#D2725E` |
  | olympian | `#7D8EA2` → `#8595A7` | `#C65645` → `#D37D70` |
  | parchment | `#87704F` → about `#5A4830` (see Risks) | `#A1301F` → `#8A291B` |

  Each value is the minimal mix toward `Text.Primary` (for `Muted`) or white/black (for `Danger`) that reaches 4.5:1
  on all four backgrounds.
- **`Themes/Controls.xaml`:**
  - **`FocusVisual.Ring` style:** a `Control` template holding a `Rectangle` with `Stroke={DynamicResource Focus.Ring}`,
    `StrokeThickness=2`, `Margin=-2`, `RadiusX/Y=2`, `SnapsToDevicePixels=True`.
  - **`FocusVisualStyle`:** replace the five `{x:Null}` setters with `{StaticResource FocusVisual.Ring}`.
  - **`IsKeyboardFocused` triggers:** add one to `CheckBox` (Track `BorderBrush` = `Focus.Ring`), `Slider` (Thumb
    `Diamond.Stroke` = `Focus.Ring`, `StrokeThickness` 2) and `ComboBox` (the toggle border), so focus stays visible
    even where the adorner is clipped.
  - **`HotkeyBox`:** when focused it shows the caret-less "press a combination" state with a `Focus.Ring` border
    (`Controls/HotkeyBox.cs`). It also sets `AutomationProperties.HelpText` = "Press the key combination you want.
    Backspace clears it." in its constructor.
- **XAML sweep:** every interactive control in `Pages/*.xaml`, `MainWindow.xaml`, `ComposeWindow.xaml`,
  `LookupWindow.xaml`, `OverlayWindow.xaml`, `RegionSelectorWindow.xaml` and `SetupWizardWindow.xaml` gets
  `AutomationProperties.Name` (the visible label text) and, where a caption exists, `AutomationProperties.HelpText`.
  Templated items bind it:
  - phrase chips: `Name="{Binding Label}" HelpText="{Binding Preview}"`;
  - OCR rows: checkbox `Name="{Binding Name}"`, Install `Name="{Binding Name, StringFormat='Install {0} text recognition'}"`;
  - theme cards: `Name="{Binding Name}"`;
  - overlay copy button: `Name="{Binding Sender, StringFormat='Copy message from {0}'}"`.
- **`MainWindow.xaml.cs`** (shared): a `PreviewKeyDown` handler for Ctrl+Tab / Ctrl+Shift+Tab →
  `Navigate(PageIds.All[(i ± 1) mod 9])` (REQ-OUX-30).
- **`ComposeWindow.xaml`:**
  - `KeyboardNavigation.TabNavigation="Cycle"` on the root.
  - Tab order: Input (0), phrase chips (1), Copy (2), Send (3), language (4), Close (5).
  - A `Label` "Reply _in" with `Target=TargetLanguage`, so Alt+I focuses the language box.
  - `SendButton.IsDefault` stays off, because Enter is handled in the input.
- **`tools/typecheck/check_a11y.py`** (new, about 90 lines, stdlib only; `typecheck.sh` runs it after
  `check_resources.py`). It parses every `*.xaml` under `src/Envoy.App` with `ElementTree` and fails with
  `path:line: message` on:
  1. A `Setter Property="FocusVisualStyle" Value="{x:Null}"` inside a `Style` with no `Setter Property="Focusable" Value="False"`,
     or a `FocusVisualStyle="{x:Null}"` attribute on an element without `Focusable="False"`.
  2. A `Button`, `TextBox`, `PasswordBox`, `ComboBox`, `Slider` or `{clr}HotkeyBox`/`SecretBox` element (not inside
     `ControlTemplate`) without `AutomationProperties.Name` or `AutomationProperties.LabeledBy`. A `Button` is exempt
     when its `Content` attribute contains a letter outside U+E000–U+F8FF and it does not use `Button.Chrome`.
  3. A `CheckBox`/`RadioButton` without a `Content` attribute and without `AutomationProperties.Name`/`LabeledBy`.
  4. Any `Color="#…"`, `Background="#…"`, `Foreground="#…"`, `BorderBrush="#…"` or `Fill="#…"` literal in
     `Windows/*.xaml`, `Pages/*.xaml` or `Controls/*.xaml`. `DropShadowEffect Color="Black"` is allowed: effects
     are not palette colours.
- **`tools/typecheck/test_check_a11y.py`** (SHOULD): unittest fixtures, one per rule (passing and failing).
- **`src/Envoy.App/Services/AccessibilityAudit.cs`** (new, about 70 lines, used by the smoke test):
  - `static IReadOnlyList<string> FindUnnamed(DependencyObject root)` walks the visual tree. For each `Control` that
    is `IsVisible && IsEnabled && Focusable` and is a `ButtonBase`, `TextBoxBase`, `PasswordBox`, `ComboBox` or
    `Slider`, it calls `UIElementAutomationPeer.CreatePeerForElement(c)?.GetName()`. Empty names are returned as
    `"{Type} '{x:Name or nearest named ancestor}'"`.
  - `static IReadOnlyList<string> FindUnreachable(Window w)` focuses the first focusable element, then calls
    `MoveFocus(Next)` up to 200 times, collecting `Keyboard.FocusedElement`. It returns the eligible controls that
    were never visited.

### App: high contrast

- **`Themes/Palette.highcontrast.xaml`** (new) defines every key the other palettes define:
  - `Color` keys via `<x:Static x:Key="Bg.Window.Color" Member="SystemColors.WindowColor"/>`;
  - brush keys as `SolidColorBrush` over those colours, with the three gradient keys also as `SolidColorBrush`.

  | Keys | System colour |
  | --- | --- |
  | `Bg.*`, `Overlay.Bg` | `WindowColor` |
  | `Text.Primary`, `Text.Secondary`, `Overlay.Text`, `Overlay.Sender`, `Danger`, `Success`, `Border.Subtle`, `Accent.Dim` | `WindowTextColor` |
  | `Text.Muted`, `Overlay.Original` | `GrayTextColor` |
  | `Accent`, `Accent.Bright`, `Focus.Ring` | `HighlightColor` |
  | `Text.OnAccent` | `HighlightTextColor` |

  `check_resources.py` already enforces identical key sets.
- **`Services/ThemeManager.cs`:**
  - `public static IReadOnlyList<string> PaletteIds => [.. Themes.Select(t => t.Id), ThemeResolver.HighContrast]`.
  - `Apply(string themeId, bool force = false)` accepts any `PaletteIds` entry. `force` reloads even when
    `themeId == CurrentId`, because system colours are read at load time.
  - The theme picker keeps listing `Themes` only.
- **`App.xaml.cs`:**
  `SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(Host.ApplyTheme); };`
  plus `Microsoft.Win32.SystemEvents.UserPreferenceChanged` (category `Color`) → `Host.ApplyTheme()`, which covers
  switching between two high-contrast themes. Both are unsubscribed in `ExitApp`.
- **Appearance page:** a ComboBox "High contrast" → `Settings.Accessibility.HighContrast` (Follow Windows / Always /
  Never) and the `AppearanceHint` callout.

### App: overlay

- **`OverlayWindow.xaml`:**
  - Move the current item `DataTemplate` to `Window.Resources` as `x:Key="FullItem"`, and add `x:Key="CompactItem"`
    (REQ-OUX-44).
  - `ApplySettings` sets `Items.ItemTemplate = (DataTemplate)FindResource(S.Compact ? "CompactItem" : "FullItem")`.
  - Both templates contain the Copy button (`Button.Ghost`, `Font.Icons` glyph `&#xE8C8;`), with `Visibility` bound
    to the window's new `IsEditing` property and `Click="OnCopyItem"`, plus a "Copied" `TextBlock` bound to
    `OverlayItem.CopyFeedback`.
  - `EditFrame` uses `Background="{DynamicResource Overlay.Bg}"` with `Opacity="0.2"`. `EmptyHint` uses
    `Background="{DynamicResource Overlay.Bg}"`.
  - The `EditBar` text becomes "Drag to move · edge to resize · ⧉ copies a message · Done to lock".
- **`OverlayWindow.xaml.cs`:**
  - `IsEditing` (notify), raised from `ToggleEditMode`.
  - `CompactFontSize => Math.Max(11, S.FontSize - 2)`.
  - `CardBackground` alpha = `ContrastMath.EffectiveBackgroundAlpha(S.BackgroundOpacity, text, bg, S.EnsureReadableText)`,
    or 1 when `ThemeManager.CurrentId == ThemeResolver.HighContrast`. `text` and `bg` come from
    `TryFindResource("Overlay.Text.Color")` / `"Overlay.Bg.Color"` converted to `Rgb`.
  - `OnCopyItem` → `ClipboardHelper.TrySetText(OverlayCopyText.Format(item.Message))` →
    `item.ShowCopyFeedback(ok ? "Copied" : "Couldn't copy")` (a 1.5 s `DispatcherTimer` clears it).
- **`OverlayItem.cs`:** add `CopyFeedback` (string, notify) and `ShowCopyFeedback(string)`.
- **`Services/ClipboardHelper.cs`** (new): `public static bool TrySetText(string text)`, the existing 5×40 ms COM
  retry moved from `ComposeWindow`, now returning success.
- **Overlay page:** a "Compact layout" `CheckBox` → `Settings.Overlay.Compact`; "Keep text readable" →
  `Settings.Overlay.EnsureReadableText`; and the REQ-OUX-37 caption bound to `MainViewModel.OverlayOpacityNote`,
  computed with `ContrastMath` from the current palette colours.

### App: reply box (`Windows/ComposeWindow.xaml(.cs)`)

- **History:** `OnInputKey` handles `Key.Up` with
  `if (_host.ReplyHistory.Older(Input.Text) is { } t) SetInputFromHistory(t)` and `Key.Down` with `Newer()`. Both set
  `e.Handled = true`. `SetInputFromHistory` sets `_recalling = true`, sets the text, puts the caret at the end, then
  clears the flag. `OnInputChanged` calls `_host.ReplyHistory.EndBrowsing()` when `!_recalling`. On a successful
  `SendAsync`/`CopyAsync`, before `Hide()`, it calls `_host.ReplyHistory.Add(Input.Text)` and records phrase usage
  (`Phrases.FindByText(Input.Text, MyLanguage)?.Id` → `PhraseOrdering.RecordUse(...)`), then `_host.ScheduleSave()`.
- **Public test hooks** for the smoke test: `internal void RecallOlder()` and `internal void RecallNewer()`.
- **Counter:** a new `TextBlock x:Name="CharCount"` in the footer, left of the buttons, `Text.Caption` style,
  `AutomationProperties.Name="Message length"`. It is updated after each translation and on `AppendOriginal` with
  `ReplyLength.Measure(AppendOriginal(_translated), Compose.MaxMessageLength)`. `Foreground` uses
  `SetResourceReference(ForegroundProperty, info.OverLimit ? "Danger" : "Text.Muted")`, so it stays a
  `DynamicResource`. It is collapsed when `ShowCharacterCount` is off or nothing has been translated yet.
- **Chips:** `FillPhrases` orders with
  `PhraseOrdering.Order(_host.Phrases.All, Target, _host.Settings.Compose.PhraseUsage, _host.Settings.Compose.PhraseOrder)`.
- **Clipboard:** `SetClipboard` → `ClipboardHelper.TrySetText`.
- **Replying page:** a "Show message length" CheckBox; a "Warn above" `TextBox` → `Compose.MaxMessageLength` with the
  caption "0 = no limit. Envoy doesn't know each game's limit; set it if long messages get cut off."; and a "Quick
  phrase order" ComboBox (Most used for this language / Phrasebook order).

### Threading

- `NoticeCenter`, `ReplyHistory`, `OnboardingFlow` and all view models are UI-thread only.
- `ProviderFailureMonitor` is locked internally. Its result is marshalled with `_ui.BeginInvoke` before `Submit`.
- `TrayNotifier.Show` runs on the UI thread (the WinForms `NotifyIcon` was created there).
- `CheckForUpdatesAsync` and `BuildTranslator` callbacks marshal through `_ui.BeginInvoke` as they already do.
- `SystemParameters.StaticPropertyChanged` can fire on the UI thread; `BeginInvoke` keeps it re-entrancy-safe.
  `SystemEvents.UserPreferenceChanged` fires on a system thread, so it marshals.
- `DisplayModeProbe.Read` runs on the UI thread. These are cheap Win32 calls on a window handle, on demand only.
- The OCR install command runs an external elevated process and does not wait. "Check again" re-queries
  synchronously (`OcrEngine.AvailableRecognizerLanguages` is fast).

### Failure behaviour

| Situation | Behaviour |
| --- | --- |
| OCR API unavailable | The OCR step lists every recommended language as Missing and shows `PageHints.OcrAvailability(0, …)` (Error). There is no notice; the check catches and skips. |
| Elevation declined or PowerShell fails | The UIF fallback opens `ms-settings:regionlanguage`. The step stays Needs attention. |
| Test translation throws | `TranslationTestText.Failure(ex)` is shown and the step is marked Needs attention. The guide can still finish. |
| Game window gone or Win32 call fails | Verdict Unknown, with the advice "Start the game and press Check again." |
| `ShowBalloonTip` throws (no shell, tray hidden) | Caught. The notice remains in the War Room list. |
| Notifications disabled by Windows (Focus Assist) | The balloon is silently not shown by Windows. The War Room list still has it. |
| `phrases.json` invalid | Built-ins are used and the Phrasebook callout shows the error. |
| Clipboard locked | "Couldn't copy" feedback on the overlay. In the compose window, behaviour is as today. |
| High-contrast palette fails to load | `ThemeManager.Apply` falls back to `imperial` (existing behaviour for unknown ids), and the smoke test catches it. |
| Settings file from an older version | New sections get their defaults. The guide is not shown to users who already have a region. |

## Acceptance criteria

| Id | REQ | Verified by |
| --- | --- | --- |
| AC-OUX-1 | 1, 11 | Unit `OnboardingFlowTests.Next_and_back_walk_six_steps_in_order`, `Evaluate_rules` (Theory). Smoke: the guide opens under all six palettes and every step is visited with `GoTo`. Log line `wizard: ok`. |
| AC-OUX-2 | 2 | Unit `OnboardingGateTests.Opens_for_fresh_settings`, `Skips_existing_users_with_region`, `Never_reopens_after_completed_or_skipped`. |
| AC-OUX-3 | 3, 10, 12 | Manual (Windows): on a fresh profile the guide opens. Skip setup → restart → `MainWindow` opens and the War Room shows "Finish setting up Envoy". The tray "Setup guide…" opens it again at the remembered step. Finish → `settings.json` has `Onboarding.Completed: true` and the War Room opens. |
| AC-OUX-4 | 4 | Manual: change the language in the guide; the Translation and War Room pages show the new value without a restart. Typecheck: the guide view model writes only via `Settings` plus `SettingsAutoApplier`. |
| AC-OUX-5 | 5 | Unit `DisplayModeAdvisorTests.Classify` (Theory over all five verdicts). Manual, in game: AoE II DE in borderless → "Borderless"; set to fullscreen, alt-tab to the guide → "LikelyExclusiveFullscreen" (if the game minimises). Record the result per game in the PR. |
| AC-OUX-6 | 6 | Manual: the Chat area step → Select chat area → the guide hides, the selector opens, the guide returns with the new region and a live preview. Smoke: `CapturePreview.IsActive` is true only on that step. |
| AC-OUX-7 | 7 | Unit `OcrReadinessTests.*`. Manual: on a PC without the Japanese OCR pack, the step shows Japanese as Missing. Install → UAC → after completion, Check again shows Installed. |
| AC-OUX-8 | 8 | Smoke: in the guide, Translate with the sample input gives a non-empty output or a "⚠" text, without throwing (network may be absent). The provider caption names the primary provider. |
| AC-OUX-9 | 9 | Unit `HotkeyCheatSheetTests.*`. Manual: register `Ctrl+Shift+T` in another app first; the step shows "Taken by another app". |
| AC-OUX-10 | 13, 14 | Unit `NoticeCenterTests.Shows_first_notice`, `Same_ocr_key_once_per_session`, `Provider_cooldown_30_min`, `Global_interval_defers_then_releases_after_60s`, `Session_cap_suppresses_sixth`, `Update_tag_announced_once_ever`. |
| AC-OUX-11 | 15 | Unit `ProviderFailureMonitorTests.Trips_once_at_threshold_within_window`, `Success_rearms`, `Old_failures_expire`. |
| AC-OUX-12 | 16 | Unit `NoticeFactoryTests.Provider_notice_has_no_exception_text`, `Ocr_notice_lists_language_names_not_codes`. |
| AC-OUX-13 | 17 | Manual: set DeepL as primary with a bogus key and receive chat; after 3 failures a balloon appears. Clicking it opens the Translation page. A second balloon for DeepL does not appear within 30 min. |
| AC-OUX-14 | 18, 20 | Unit `NoticeCenterTests.Disabled_kind_suppresses_balloon_but_keeps_active`, `Dismissed_key_never_shows_or_lists`. Smoke: War Room renders a seeded active notice in every palette. |
| AC-OUX-15 | 19 | Unit `NoticeCenterTests.Defers_while_game_in_foreground`. Manual: with the game focused, a forced provider failure produces no balloon; alt-tab out and it appears within about 3 s. |
| AC-OUX-16 | 21 | Unit `NoticeCenterTests.Hold_defers_everything`. Manual: on a fresh profile with missing OCR packs, no balloon appears while the guide is open. |
| AC-OUX-17 | 22, 23 | Unit `PageHintsTests.*` (Theory per function) and `PhraseBookTests.Load_reports_user_file_error`. Smoke: every page renders with its hint (the smoke settings have no region and no messages, so War Room and Reading Chat callouts are visible). Screenshots `{theme}-settings-dashboard.png` show them. |
| AC-OUX-18 | 24, 26 | Typecheck: `tools/typecheck/check_a11y.py` passes on the tree and fails on each fixture in `test_check_a11y.py`. |
| AC-OUX-19 | 25, 35 | Unit `PaletteContrastTests.Text_colours_meet_4_5_to_1` and `Focus_ring_meets_3_to_1` (Theory over the five game palettes, reading `src/Envoy.App/Themes/Palette.*.xaml`), `Overlay_text_meets_4_5_on_opaque_bg`. |
| AC-OUX-20 | 27 | Smoke: `AccessibilityAudit.FindUnnamed` returns empty for all `MainWindow` pages, every guide step, compose, lookup and overlay in edit mode. Log line `a11y: ok (N controls)`; the test fails otherwise. |
| AC-OUX-21 | 29, 31 | Smoke: `AccessibilityAudit.FindUnreachable` returns empty for the guide (each step) and the compose window. Manual: keyboard-only through the guide (Tab, Enter, Esc) and the reply box. The focus ring is visible on every control in each theme. |
| AC-OUX-22 | 30 | Manual: Ctrl+Tab cycles the nine pages, wrapping at both ends. |
| AC-OUX-23 | 32, 33 | Unit `ThemeResolverTests.*`. Typecheck: `check_resources.py` shows the high-contrast palette has the same keys. Smoke: the per-palette loop includes `highcontrast`, producing `highcontrast-settings-*.png`. |
| AC-OUX-24 | 34 | Manual: Windows Settings → Accessibility → Contrast themes → Night sky while Envoy is open. The settings, overlay and reply box switch within a second; switching to Desert updates again; turning it off restores the game theme. |
| AC-OUX-25 | 36, 37, 38 | Unit `ContrastMathTests.Minimum_alpha_meets_target_over_white_grey_black`, `Effective_alpha_never_lowers_user_choice`, `Effective_alpha_ignores_floor_when_disabled`. Typecheck: `check_a11y.py` rule 4. Manual: slider at 20% → caption "Raised to NN%"; the overlay stays readable over a white map area. |
| AC-OUX-26 | 39, 40 | Unit `ReplyHistoryTests.*`. Smoke: `ReplyHistory.Add("gg wp")`, open compose, `RecallOlder()` → `Input.Text == "gg wp"`, `RecallNewer()` → `""`. Manual: in game, send two replies, press the hotkey, then Up, Up, Down. |
| AC-OUX-27 | 41 | Unit `ReplyLengthTests.Counts_text_elements`, `Limit_zero_never_over`, `Over_limit_flagged_with_slash_text`. Manual: set the limit to 10, type a long reply; the counter turns `Danger` and Send still works. |
| AC-OUX-28 | 42, 43 | Unit `PhraseOrderingTests.*`. Manual: send "gg" twice to a Korean target; reopen; the "gg" chip is first for Korean but not for Russian. |
| AC-OUX-29 | 44, 46 | Smoke: overlay with `Compact = true` under every palette, with screenshots `{theme}-overlay-compact.png`. `Items.ItemTemplate` is `CompactItem`. Manual: toggling in the Overlay page applies live. |
| AC-OUX-30 | 45 | Unit `OverlayCopyTextTests.*`. Smoke: in edit mode, the copy buttons are visible (count = items) and `AccessibilityAudit` names them. Manual: Ctrl+Shift+E → click ⧉ on a message → paste into Notepad shows "Sender: translation" and the original line. |

## Test plan

### Unit tests (`tests/Envoy.Core.Tests/`, xUnit; `FakeTimeProvider` is already referenced)

| File | Test | Asserts |
| --- | --- | --- |
| `OnboardingTests.cs` | `Next_and_back_walk_six_steps_in_order` | Steps in enum order; `Next` false on Hotkeys; `Back` false on Language; `Visited` accumulates |
| | `GoTo_marks_visited_and_raises_changed` | `Changed` raised once; `Index` updated |
| | `Evaluate_rules` (Theory) | each attention rule from the Design, NotVisited when unvisited, Skipped for an untested translation step |
| | `Gate_opens_for_fresh_settings` / `Skips_existing_users_with_region` / `Never_reopens_after_completed_or_skipped` | `ShouldAutoOpen` truth table |
| | `ResumeStep_parses_last_step_and_defaults_to_language` | "ChatArea" → ChatArea; "" or "bogus" → Language |
| `DisplayModeAdvisorTests.cs` | `Classify` (Theory) | not running; no window; minimised → LikelyExclusiveFullscreen; caption → Windowed; covers monitor ±2 px → Borderless; partial → Unknown |
| | `Describe_mentions_no_game_menu_paths` | every verdict text non-empty and contains no "→" or "Options >" |
| `OcrReadinessTests.cs` | `Missing_excludes_installed_and_normalises_codes` | "zh-cn" installed covers "zh-CN" |
| | `Latin_language_covered_by_any_latin_pack_except_vi` | es is covered by en; vi is not |
| | `Empty_wanted_means_only_my_language_can_be_missing` | |
| | `Recommend_orders_mine_then_wanted_then_common_distinct` | |
| `HotkeyCheatSheetTests.cs` | `Lists_six_actions_in_fixed_order`, `Unset_is_not_set`, `Taken_is_reported`, `Format_one_line_per_row` | |
| `NoticeCenterTests.cs` | `Shows_first_notice` | Show; `Active` has it |
| | `Same_ocr_key_once_per_session` | second Submit → Suppress; a different missing set → Show (after interval) |
| | `Provider_cooldown_30_min` | Suppress at +29 min, Show at +31 min |
| | `Global_interval_defers_then_releases_after_60s` | the second notice is Defer; `TakeDeferred` null at +59 s, returns it at +60 s |
| | `Session_cap_suppresses_sixth` | |
| | `Defers_while_game_in_foreground` / `Shows_in_foreground_when_quiet_disabled` | |
| | `Disabled_kind_suppresses_balloon_but_keeps_active`, `Master_switch_off_suppresses_all` | |
| | `Dismissed_key_never_shows_or_lists` | `Dismiss` adds to settings; later Submit → Suppress and not in `Active` |
| | `Update_tag_announced_once_ever` | uses `settings.LastAnnouncedUpdate` across two `NoticeCenter` instances |
| | `Resolve_removes_active_and_deferred`, `Hold_defers_everything` | |
| `ProviderFailureMonitorTests.cs` | `Trips_once_at_threshold_within_window`, `Success_rearms`, `Old_failures_expire`, `Providers_are_independent` | |
| `NoticeFactoryTests.cs` | `Provider_notice_has_no_exception_text`, `Ocr_notice_lists_language_names_not_codes`, `Ocr_key_is_order_independent`, `Ocr_returns_null_for_empty` | |
| `PageHintsTests.cs` | one Theory per `PageHints` method | severity, action id, null when healthy |
| `ContrastMathTests.cs` | `Black_white_is_21`, `Ratio_symmetric`, `Parse_accepts_argb`, `Minimum_alpha_meets_target_over_white_grey_black`, `Minimum_alpha_is_one_when_unreachable`, `Effective_alpha_never_lowers_user_choice`, `Effective_alpha_ignores_floor_when_disabled` | |
| `PaletteContrastTests.cs` | `Text_colours_meet_4_5_to_1` (Theory: 5 palettes × 4 text keys × 4 backgrounds) | reads `Palette.<id>.xaml` via a repo-root helper (walks up from `AppContext.BaseDirectory` to `Envoy.sln`) with `XDocument`, `<Color>` elements only |
| | `Focus_ring_meets_3_to_1`, `Overlay_text_meets_4_5_on_opaque_bg`, `All_palettes_define_focus_ring` | |
| `ThemeResolverTests.cs` | `Follow_windows_uses_high_contrast_when_system_on`, `Never_ignores_system`, `Always_forces`, `Match_game_uses_game_theme_else_default`, `Unknown_theme_falls_back_to_default` | |
| `ReplyHistoryTests.cs` | `Add_ignores_blank_and_moves_duplicate_to_front`, `Capacity_drops_oldest`, `Older_walks_back_and_stops_at_oldest`, `Newer_past_newest_restores_draft`, `Add_and_EndBrowsing_reset_cursor`, `Newer_without_browsing_returns_null` | |
| `ReplyLengthTests.cs` | `Counts_text_elements` | "👍🏽" = 1; "你好" = 2; "é" = 1 |
| | `Limit_zero_never_over`, `Over_limit_flagged_with_slash_text` | |
| `PhraseOrderingTests.cs` | `Book_order_unchanged`, `Translated_first_then_untranslated`, `Most_used_first_within_group_ties_keep_book_order`, `Usage_is_per_target_language`, `RecordUse_normalises_language_saturates_and_caps` | |
| `OverlayCopyTextTests.cs` | `Translated_includes_sender_translation_and_original`, `No_sender_no_prefix`, `Original_omitted_when_identical`, `Pending_and_failed_use_original` | |
| `PhraseBookTests.cs` (existing tests are in `TranslationTests.cs`; add a new file) | `Load_reports_user_file_error`, `Load_without_user_file_has_no_error` | temp file with invalid JSON |

### Typecheck additions

- `tools/typecheck/check_a11y.py` runs in `typecheck.sh` after `check_resources.py`.
- `tools/typecheck/test_check_a11y.py` (SHOULD) with fixtures under `tools/typecheck/fixtures/a11y/`.
- `TypeCheck.csproj` needs no change unless UIF changed its globbing. New `.cs` files are under `src/Envoy.App` and are
  picked up.

### Smoke-test additions (`src/Envoy.App/SmokeTest.cs`, shared)

1. **Palettes:** the per-theme loop iterates `ThemeManager.PaletteIds` (six) instead of `ThemeManager.Themes`.
   `host.Settings.Theme` is set only for real themes. For `highcontrast` it sets
   `host.Settings.Accessibility.HighContrast = Always`, then restores `FollowWindows`.
2. **Hints:** before the loop, seed `host.Notices.Submit(NoticeFactory.OcrMissing(["ja"]), false)` so the War Room
   notice list renders. The War Room screenshot then shows hints and the notice.
3. **Setup guide:** per palette, `var wizard = new SetupWizardWindow(host, (IShell)app, OnboardingStep.Language);
   wizard.Show();` then for each step `wizard.ViewModel.GoTo(step); await Pump();`. Assert the visible step panel
   count is 1 and `CapturePreview.IsActive == (step == ChatArea)`. With `--screenshots`, save
   `{theme}-setup-{step}.png` for `imperial` and `highcontrast` only (keeps CI under the 180 s limit). On the
   TestTranslation step, `await wizard.ViewModel.TestTranslateCommand.ExecuteAsync()` with a 5 s cap; the output is
   non-empty. Then `wizard.Close()`. Log `wizard: ok`.
4. **Accessibility audit:** for the first palette only, since names do not depend on the theme. Run
   `AccessibilityAudit.FindUnnamed` on `MainWindow` after each `Navigate(page)`, on each guide step, on compose,
   lookup and the overlay in edit mode with demo messages. Run `FindUnreachable` on each guide step and on compose.
   Any result → throw with the list. Log `a11y: ok (N controls)`.
5. **Overlay compact:** per palette, `host.Settings.Overlay.Compact = true; overlay.ApplySettings();`, demo messages,
   assert the template key, with screenshot `{theme}-overlay-compact.png`; then restore `false`. In edit mode, assert
   the visible copy-button count equals the item count.
6. **Reply history:** in the compose block, `host.ReplyHistory.Add("gg wp")` before `Open`. After `Open`,
   `compose.RecallOlder()` → `Input.Text == "gg wp"`; `RecallNewer()` → `""`. `CharCount` exists.
7. **Tray notifier:** after the loop, create a `NotifyIcon` (`Visible = true`, app icon), build a `TrayNotifier`,
   `Show(NoticeFactory.UpdateAvailable("v9.9.9", "1.0.0"))`, then dispose. Log `notice: ok`.
8. **Focus visual check:** for each palette, `((Style)app.FindResource(typeof(Button)))` resolved through `BasedOn`
   has a non-null `FocusVisualStyle` setter, and `FindResource("Focus.Ring")` is a `SolidColorBrush`.

### Manual checks (Windows, record results in the PR)

- **M1:** fresh profile (`%APPDATA%\Envoy` renamed): go through the guide end to end with AoE II DE or AoE IV in
  borderless. Every step works and Finish lands on the War Room.
- **M2:** the same with the game in exclusive fullscreen (where supported): the verdict says LikelyExclusiveFullscreen
  or Unknown, never Borderless.
- **M3:** OCR install from the guide for one missing language (UAC prompt). Check again flips it to Installed.
- **M4:** provider failure balloon (bogus DeepL key), the cooldown, the click action, and deferral while the game is
  focused.
- **M5:** keyboard-only pass over every page, the guide, the reply box, and the overlay in edit mode (Tab to Copy and
  Done). The focus ring is visible in all five themes and in high contrast.
- **M6:** Narrator pass (Win+Ctrl+Enter): title-bar buttons, hotkey boxes, OCR rows, phrase chips and the guide step
  list announce meaningful names.
- **M7:** switch Windows contrast themes live (Night sky → Desert → off).
- **M8:** reply history and the counter in game; compact overlay readability over a bright map at 20% background
  opacity with "Keep text readable" on and off.

## Files touched

| Path | Change | Shared file? |
| --- | --- | --- |
| `src/Envoy.Core/Settings/AppSettings.cs` | add `Onboarding`, `Notifications`, `Accessibility` sections; `HighContrastMode`, `PhraseOrder` enums; `Overlay.Compact`, `Overlay.EnsureReadableText`; `Compose.PhraseOrder`, `PhraseUsage`, `MaxMessageLength`, `ShowCharacterCount` | **yes (AppSettings.cs)** |
| `src/Envoy.Core/Settings/HotkeyCheatSheet.cs` | new | |
| `src/Envoy.Core/Onboarding/{OnboardingFlow,OnboardingGate,DisplayModeAdvisor}.cs` | new | |
| `src/Envoy.Core/Languages/OcrReadiness.cs` | new | |
| `src/Envoy.Core/Notifications/{Notice,NoticeFactory,NoticeCenter,ProviderFailureMonitor}.cs` | new | |
| `src/Envoy.Core/Ux/{PageHints,ContrastMath,ThemeResolver}.cs` | new | |
| `src/Envoy.Core/Compose/{ReplyHistory,ReplyLength,PhraseOrdering}.cs` | new | |
| `src/Envoy.Core/Pipeline/OverlayCopyText.cs` | new | |
| `src/Envoy.Core/Phrases/PhraseBook.cs` | `LoadWithUserFile(path, out error)` overload | |
| `tests/Envoy.Core.Tests/*` | 17 new test files listed above | |
| `src/Envoy.App/AppHost.cs` | `Notices`, `NoticeReady`, `ReplyHistory`, `PhrasesLoadError`, `IsGameInForeground`, OCR readiness check, provider failure monitor hook, update notice, deferred release on the game timer, `ApplyTheme` via `ThemeResolver` | **yes (AppHost.cs)** |
| `src/Envoy.App/App.xaml.cs` | `OpenSetupWizard`, start-up gate, tray items "Setup guide…" / "Hotkey cheat sheet…", `TrayNotifier` wiring + `RunNoticeAction`, high-contrast listeners, wizard hide/show in `SelectRegion`, `FailedHotkeyCombos`, `IShell` additions | **yes (App.xaml.cs)** |
| `src/Envoy.App/IShell.cs` | + `OpenSetupWizard`, `ShowPage`, `IsHotkeyTaken` | (UIF file) |
| `src/Envoy.App/SmokeTest.cs` | items 1–8 of the smoke plan | **yes (SmokeTest.cs)** |
| `src/Envoy.App/Windows/MainWindow.xaml.cs` | Ctrl+Tab page cycling | **yes (MainWindow\*)** |
| `src/Envoy.App/Windows/MainWindow.xaml` | `AutomationProperties.Name` on chrome buttons and nav | **yes (MainWindow\*)** |
| `src/Envoy.App/Windows/SetupWizardWindow.xaml(.cs)` | new | |
| `src/Envoy.App/ViewModels/SetupWizardViewModel.cs`, `StepItem.cs`, `OcrInstaller.cs` | new (`OcrInstaller` is extracted from UIF's `InstallOcrCommand` body) | |
| `src/Envoy.App/ViewModels/MainViewModel.Hints.cs` | new partial: hints, `RunHintActionCommand`, `Notices`, `DismissNoticeCommand`, `OverlayOpacityNote`, `CaptureCondition` mapping | |
| `src/Envoy.App/ViewModels/MainViewModel.cs` | `RefreshStatus` notifies the hints; `InstallOcrCommand` delegates to `OcrInstaller` | (UIF-split file) |
| `src/Envoy.App/Pages/*.xaml` (all nine) | `Callout` per page; `AutomationProperties`; new controls on the Overlay, Replying and Appearance pages; War Room notice list and guide button; About "Run setup guide again" | |
| `src/Envoy.App/Windows/ComposeWindow.xaml(.cs)` | history keys, counter, chip ordering, tab order, label/access key, `ClipboardHelper` | |
| `src/Envoy.App/Windows/OverlayWindow.xaml(.cs)`, `OverlayItem.cs` | compact template, copy in edit mode, readable alpha, no literal colours, `IsEditing` | (05 also edits `Reposition` in the `.cs`; no overlap) |
| `src/Envoy.App/Windows/LookupWindow.xaml`, `RegionSelectorWindow.xaml` | `AutomationProperties` only | |
| `src/Envoy.App/Services/{TrayNotifier,DisplayModeProbe,AccessibilityAudit,ClipboardHelper}.cs` | new | |
| `src/Envoy.App/Services/ThemeManager.cs` | `PaletteIds`, `Apply(id, force)`, accepts `highcontrast` | |
| `src/Envoy.App/Interop/Native.cs` | `MonitorFromWindow`, `GetMonitorInfo`, `GWL_STYLE`/`WS_CAPTION` constants | (02 also adds `MonitorFromWindow`; keep one) |
| `src/Envoy.App/Controls/Converters.cs` | `NullToCollapsedConverter` | |
| `src/Envoy.App/Controls/HotkeyBox.cs` | `AutomationProperties.HelpText`, focus border | |
| `src/Envoy.App/Themes/Controls.xaml` | `FocusVisual.Ring`, five `{x:Null}` replaced, focus triggers, `Callout` style, converter resource | |
| `src/Envoy.App/Themes/Palette.{classic,frontier,imperial,olympian,parchment}.xaml` | + `Focus.Ring(.Color)`; adjusted `Text.Muted`, `Danger` | |
| `src/Envoy.App/Themes/Palette.highcontrast.xaml` | new | |
| `tools/typecheck/check_a11y.py`, `test_check_a11y.py`, `fixtures/a11y/*.xaml` | new | |
| `tools/typecheck/typecheck.sh` | run `check_a11y.py` (and its tests) | |
| `docs/getting-started.md`, `docs/troubleshooting.md`, `docs/theming.md`, `CHANGELOG.md` | setup guide, notices, high contrast, the `Focus.Ring` palette key and contrast rule, reply history | |

Estimated size: Core about 650 lines and tests about 550; App about 900 (the guide XAML and view model about 500,
the rest small edits); Python about 120. That is at the top of the one-sitting budget. If it must be cut, the
SHOULD/COULD items (REQ-OUX-11, 12, 19, 20, 28, 30, 31, 37, `test_check_a11y.py`) can be left for a follow-up
without breaking any MUST.

## Risks & open questions

- **Exclusive-fullscreen detection is a heuristic.** "Minimised while Envoy is focused" is how exclusive-fullscreen
  D3D games behave on alt-tab, but a user can also minimise a borderless game by hand. Some games keep a full-monitor,
  captionless window in exclusive mode too, which would read as "Borderless". Mitigation: the wording says "likely",
  "Check again" exists, and the step never blocks. The manual check M2 records real behaviour per game. If it proves
  unreliable, fall back to showing advice only (verdict Unknown) and drop the classification. Open: does
  `SHQueryUserNotificationState` (`QUNS_RUNNING_D3D_FULL_SCREEN`) give a better signal while the game is in front? It
  can only be queried while the game is foreground, so it doesn't help the guide, but it could help a future
  in-game check.
- **Balloon visibility.** Windows Focus Assist / "Do not disturb" and per-app notification settings can hide
  balloons. Envoy cannot detect this, which is why notices are mirrored in the War Room. On Windows 11 a balloon
  appears under the app name taken from the exe's version info. 06-distribution-updates should make sure
  `AssemblyTitle`/`Product` is "Envoy".
- **Parchment `Text.Muted` change is large.** It goes from 2.45:1 to 4.5:1 against `Bg.Window`, which needs about
  `#5A4830` and narrows the visual gap to `Text.Secondary`. Alternative: measure captions only against the
  backgrounds they actually sit on (`Bg.Panel`, `Bg.PanelAlt`), which needs a smaller change. Decision for the
  implementer: keep the four-background rule unless the parchment screenshot looks wrong, and if relaxing, record the
  exact pairs in `PaletteContrastTests`.
- **The readable-overlay floor changes visuals for some users.** Anyone who set `BackgroundOpacity` below about 0.65
  will see a more opaque card after upgrading. That is intended (it is the accessibility default) and can be turned
  off. The caption explains it.
- **High contrast and fonts.** The display font (Cinzel) and decorative frames remain in high-contrast mode. They are
  legible, but purists may want Segoe UI. Open: should the high-contrast palette also override `Font.Display`? It
  can't, because fonts live in `Controls.xaml`, not the palettes. Deferred.
- **System colours are read at palette load.** `x:Static` captures values once. The palette is reloaded on
  `UserPreferenceChanged(Color)` and on the `HighContrast` property change. If an edge case misses an event, the
  colours update on the next `ApplySettings()` or restart.
- **Smoke-test runtime.** The CI step has a 180 s timeout. Adding a sixth palette, the guide and compact overlay adds
  about 25–35 s, most of it the existing 2.5 s compose wait per palette. If needed, the high-contrast palette skips
  the compose free-provider wait. Changing the workflow timeout belongs to quality-security-ci.
- **Phrase usage in `settings.json`.** Only phrase ids and counts are stored, never free text. That keeps the "no chat
  on disk unless transcripts are on" promise. Reply history is memory-only for the same reason. Open: would players
  want history to persist across sessions? If so, add an opt-in later, off by default.
- **`AutomationProperties.Name` drift.** The literal names duplicate visible labels and can drift when labels change.
  The lint only checks presence, not equality. Acceptable for 1.0. Localisation would replace both.
- **Interaction with UIF ordering.** The design assumes UIF's `Pages/`, `IShell`, `SettingsAutoApplier`,
  `CapturePreviewViewModel` and `TranslationTestText` exist. If 07 must land first, the guide can call the existing
  `App`/`MainWindow` methods directly, but the page callouts would have to go into `MainWindow.xaml`, causing
  conflicts. Recommended order: 01 → (02, 03) → 07.
