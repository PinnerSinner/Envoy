# Accessibility: focus ring, accessible names, colour contrast, Windows high contrast

Status: Proposed — revised against `00-integration.md` (binding). Lands as step 9, after 01, 08a, 02, 03, 05, 04, 06
and 07a.

Area id: `07b-accessibility` · Requirement prefix: `OUXB` · Split from `07-onboarding-ux-accessibility.md`.
Requirement numbers keep the original `REQ-OUX-n` numbering (contract preamble), so `REQ-OUXB-24` is the old
`REQ-OUX-24` and the contract §9 budget row "07b" maps directly. The other parts are `07a-onboarding-notices.md` and
`07c-reply-overlay-polish.md`.

## Context

### What exists today

| Topic | Today | Where |
| --- | --- | --- |
| Focus visuals | `Controls.xaml` sets `FocusVisualStyle={x:Null}` on `Button.Base` (all buttons), `NavItem`, `ThemeCard`, `CheckBox` and `Slider` (lines 119, 192, 226, 389, 450). Only `Button.Base`, `TextBox` and `PasswordBox` have an `IsKeyboardFocused` trigger, and on buttons it is a 1 px border change from `Accent.Dim` to `Accent`. Keyboard focus on nav items, toggles, theme cards and sliders is invisible. | `Themes/Controls.xaml` |
| Screen readers | Before 01 the app set no `AutomationProperties`. Icon-only buttons (`Button.Chrome` glyphs such as `&#xE8BB;`) announce as private-use characters. `SettingRow` labels are sibling `TextBlock`s that UI Automation does not associate with the input. Phrase chips, theme cards and OCR checkboxes have composite content and no name. From 01 on, `CLAUDE.md` requires `AutomationProperties.Name` on every **new** interactive control, so 02–07a's controls are named; the controls moved by 01 are not. | `Pages/*.xaml`, `Windows/*.xaml`, `Controls/HotkeyBox.cs` |
| High contrast | Not handled. The five palettes are fixed colours, so Windows contrast themes are ignored. | `Services/ThemeManager.cs`, `Themes/Palette.*.xaml` |
| Contrast | Measured from the palette files (WCAG 2.x, worst case over `Bg.Window`/`Bg.Panel`/`Bg.PanelAlt`/`Bg.Input`): `Text.Muted` (all `Text.Caption` text, 12 px) is 2.45:1 in parchment and 3.6–4.1:1 in the others. `Danger` is 2.65–3.7:1. Both should be at least 4.5:1. `Overlay.BackgroundOpacity` is user-controlled (0–1); below about 0.63–0.68, depending on the theme, `Overlay.Text` over a white game backdrop drops below 4.5:1. | `Themes/Palette.*.xaml`, `Windows/OverlayWindow.xaml(.cs)` |
| Literal colours | `OverlayWindow.xaml` uses `#33000000` (edit frame) and `#99000000` (empty hint). `RegionSelectorWindow.xaml` uses `#01000000` (hit-test background) and `#99000000` (dim). The Appearance page theme swatch uses `Stroke="#40000000"`. All ignore the palette and high contrast. | `Windows/*.xaml`, `Pages/AppearancePage.xaml` |

### What earlier steps deliver (contract §1)

- **01:** `Pages/*`, `PageIds`, `ThemeManager.PaletteIds` (today's five ids; this area appends high contrast), the
  three-phase smoke test with the binding-error listener, the `AutomationProperties.Name` rule in `CLAUDE.md`.
- **08a:** analyzers and `tools/ci/check_policy.py`; nothing here adds native or network code.
- **02:** `ToastWindow` (palette keys only, named buttons), the `OnStartup` skeleton with the `[07b high-contrast
  listeners]` slot at step 8, `ExitApp`.
- **03–06:** their pages and controls, each named per the `CLAUDE.md` rule; 06's Appearance "Startup & updates"
  frame.
- **07a:** `SetupWizardWindow`, the `Callout` style, the Appearance "Notifications" frame (this area inserts
  "Accessibility" before it, contract §8.6), the guide smoke section with an `imperial`-only screenshot list.

### What is weak or missing for production

1. Keyboard-only users cannot see where focus is.
2. Screen-reader users hear glyph code points or nothing for many controls.
3. Windows high-contrast users get a dark gold theme that ignores their system colours.
4. Some caption and error text is below WCAG AA in every theme, and the overlay can become unreadable over bright
   terrain.

## Goals / Non-goals

**Goals**

- A visible focus ring on every focusable control, enforced by a lint.
- Accessible names on every interactive control, enforced by a lint in `typecheck.sh`.
- A sixth palette built from `SystemColors`, chosen automatically when Windows high contrast is on, and switched live.
- Palette text contrast of at least 4.5:1 enforced by a unit test, and an overlay background floor that keeps text
  readable.
- Palette keys instead of literal colours in every window, including `RegionSelectorWindow` (contract §8.8).

**Non-goals**

- Onboarding, notices, page hints (07a). Reply box and overlay layout (07c).
- A UI scale setting, screen-reader live announcement of incoming chat (Herald mode already speaks it), or replacing
  the display font in high contrast (fonts live in `Controls.xaml`, not palettes).
- Localising `AutomationProperties.Name` values. They stay English literals beside the English labels.

## Requirements

### Keyboard, focus and screen readers

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUXB-24 | MUST | No style or element in `src/Envoy.App` sets `FocusVisualStyle` to `{x:Null}` unless it also sets `Focusable=False`. Focusable controls use a shared `FocusVisual.Ring` style (2 px `Focus.Ring` brush, 2 px outset). `CheckBox`, `Slider` and `ComboBox` also get an `IsKeyboardFocused` trigger that uses `Focus.Ring`, so focus stays visible where the adorner is clipped. Enforced by `tools/typecheck/check_a11y.py` rule 1 and the smoke `focus-visual` section. |
| REQ-OUXB-25 | MUST | Every palette defines `Focus.Ring.Color` and `Focus.Ring`. In each game palette the colour has at least 3:1 contrast against `Bg.Window`, `Bg.Panel`, `Bg.PanelAlt` and `Bg.Input` (unit test). |
| REQ-OUXB-26 | MUST | Every `Button`, `ToggleButton`, `TextBox`, `PasswordBox`, `ComboBox`, `Slider`, `HotkeyBox` and `SecretBox`, and every `CheckBox`/`RadioButton` without a string `Content`, in `src/Envoy.App/**/*.xaml` outside `ControlTemplate`s, has `AutomationProperties.Name` or `AutomationProperties.LabeledBy`. A `Button` with letter `Content` that is not `Button.Chrome` is exempt. Enforced by `check_a11y.py` rules 2–3. This step names every control 01 moved without a name. |
| REQ-OUXB-27 | SHOULD | The smoke test walks each window (all `MainWindow` pages, every guide step, compose, lookup, the overlay in edit mode) and fails if any visible, enabled, focusable control has an empty UI Automation name. |
| REQ-OUXB-28 | SHOULD | Controls whose row has a caption get `AutomationProperties.HelpText` equal to the caption text. `HotkeyBox` sets a HelpText explaining how to enter and clear a combination. |
| REQ-OUXB-29 | MUST | In `SetupWizardWindow` and `ComposeWindow`, Tab cycles through every interactive control without leaving the window (`KeyboardNavigation.TabNavigation="Cycle"` on the root). Enter activates the default action (Next/Finish; Send) and Esc the cancel action (Skip setup; close). |
| REQ-OUXB-30 | SHOULD | In `MainWindow`, Ctrl+Tab and Ctrl+Shift+Tab move to the next and previous page, wrapping around. |
| REQ-OUXB-31 | SHOULD | The smoke test checks keyboard reachability: Tab traversal from the first focusable element of each guide step and of the compose window visits every visible, enabled, focusable control. |

### High contrast and colour contrast

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUXB-32 | MUST | A sixth palette `Palette.highcontrast.xaml` defines every palette key. Every colour comes from `SystemColors` (`x:Static`) except the two functional alpha keys `Overlay.HitTest` and `Overlay.Dim`, which are the same literals in every palette. `ThemeManager.PaletteIds` gains `highcontrast`; the theme picker still lists `ThemeManager.Themes` only. |
| REQ-OUXB-33 | MUST | `ThemeResolver.Resolve(themeSetting, gameThemeId, systemHighContrast, mode, knownThemeIds)` returns `highcontrast` when `Accessibility.HighContrast` is `Always`, or `FollowWindows` with Windows high contrast on; otherwise the existing theme choice. `AppHost.ApplyTheme` uses it at `ApplySettings` position 9. When Windows high contrast turns on or off, or the contrast theme changes, Envoy re-applies the palette within one dispatcher cycle, without a restart. *(Absorbs REQ-OUX-34.)* |
| REQ-OUXB-34 | — | Folded into REQ-OUXB-33. |
| REQ-OUXB-35 | MUST | In every game palette, `Text.Primary`, `Text.Secondary`, `Text.Muted` and `Danger` have at least 4.5:1 contrast against `Bg.Window`, `Bg.Panel`, `Bg.PanelAlt` and `Bg.Input`, and `Overlay.Text` and `Overlay.Sender` have at least 4.5:1 against `Overlay.Bg` (unit test reading the palette XAML). No `Windows/*.xaml`, `Pages/*.xaml` or `Controls/*.xaml` file contains a literal colour: `OverlayWindow`'s edit frame and empty hint use `Overlay.Bg`, and `RegionSelectorWindow` uses `Overlay.HitTest` and `Overlay.Dim` (contract §8.8). Enforced by `check_a11y.py` rule 4. *(Absorbs REQ-OUX-38.)* |
| REQ-OUXB-36 | MUST | With `Overlay.EnsureReadableText` (default on), the overlay card background alpha is `max(BackgroundOpacity, ContrastMath.MinimumBackgroundAlpha(Overlay.Text, Overlay.Bg, 4.5))`, computed over worst-case white, mid-grey and black backdrops. In the high-contrast palette it is 1. An Overlay-page checkbox "Keep text readable" controls it. |
| REQ-OUXB-37 | SHOULD | When the floor raises the user's value, the Overlay page shows "Raised to NN% so text stays readable" under the background-opacity slider. |
| REQ-OUXB-38 | — | Folded into REQ-OUXB-35. |

**MUST count: 8** (REQ-OUXB-24, 25, 26, 29, 32, 33, 35, 36), matching contract §9.

## Design

### Settings (`src/Envoy.Core/Settings/AppSettings.cs`, shared file)

```csharp
public sealed class AppSettings { /* … */ public AccessibilitySettings Accessibility { get; set; } = new(); }

public enum HighContrastMode { FollowWindows, Always, Never }
public sealed class AccessibilitySettings
{
    public HighContrastMode HighContrast { get; set; } = HighContrastMode.FollowWindows;
}

// OverlaySettings addition
public bool EnsureReadableText { get; set; } = true;
```

Contract §7: additive with defaults. `FollowWindows` reproduces today's look for everyone not using a Windows contrast
theme. `EnsureReadableText = true` changes the overlay for users who set the background below about 0.65; this is
the deliberate accessibility default (see Risks). No marker is needed and no data file is added. Only the UI thread
reads these.

### Core (`src/Envoy.Core/Ux/`, new)

```csharp
namespace Envoy.Core.Ux;

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
    /// {white, #808080, black}; 1.0 if even an opaque bg cannot reach it.
    public static double MinimumBackgroundAlpha(Rgb text, Rgb bg, double target);
    public static double EffectiveBackgroundAlpha(double userAlpha, Rgb text, Rgb bg, bool ensureReadable, double target = 4.5);
}

public static class ThemeResolver
{
    public const string HighContrast = "highcontrast", MatchGame = "match-game", Default = "imperial";
    /// Always → HighContrast; FollowWindows && systemHighContrast → HighContrast; else themeSetting == MatchGame ?
    /// (gameThemeId ?? Default) : themeSetting; an id not in knownThemeIds → Default.
    public static string Resolve(string themeSetting, string? gameThemeId, bool systemHighContrast,
                                 HighContrastMode mode, IReadOnlyCollection<string> knownThemeIds);
}

public static class AccessibilityText                // contract §8.7
{
    public const string OpacityRaised = "Raised to {percent}% so text stays readable.";
    public const string HotkeyBoxHelp = "Press the key combination you want. Backspace clears it.";
    public static string Raised(double alpha);       // CultureInfo.CurrentCulture
}
```

`ThemeManager.MatchGame` becomes `public const string MatchGame = ThemeResolver.MatchGame;`, so there is one literal.

### Palettes (`src/Envoy.App/Themes/`)

- **All five game palettes:** add `<Color x:Key="Focus.Ring.Color">` (the palette's `Accent.Bright` value, which
  measures at least 3.03:1 on all four backgrounds today) and `<SolidColorBrush x:Key="Focus.Ring"/>`; add
  `Overlay.HitTest.Color` `#01000000` and `Overlay.Dim.Color` `#99000000` with their brushes; then fix the failing
  text colours. The values below are suggestions; the unit test is authoritative:

  | Palette | `Text.Muted` | `Danger` |
  | --- | --- | --- |
  | classic | `#8E7C62` → `#9F8E74` | `#C4553C` → `#D07965` |
  | frontier | `#7A8A84` → `#8D9991` | `#B94B3B` → `#CE8176` |
  | imperial | `#7C8597` → `#858D9B` | `#C9573F` → `#D2725E` |
  | olympian | `#7D8EA2` → `#8595A7` | `#C65645` → `#D37D70` |
  | parchment | `#87704F` → about `#5A4830` (see Risks) | `#A1301F` → `#8A291B` |

  Each value is the smallest mix toward `Text.Primary` (for `Muted`) or white/black (for `Danger`) that reaches 4.5:1
  on all four backgrounds. 02's toasts, 03's health dots and 07a's callouts consume these keys and need no change.
- **`Palette.highcontrast.xaml`** (new): `Color` keys via `<x:Static x:Key="Bg.Window.Color" Member="SystemColors.WindowColor"/>`;
  brush keys as `SolidColorBrush` over them, including the gradient keys.

  | Keys | System colour |
  | --- | --- |
  | `Bg.*`, `Overlay.Bg` | `WindowColor` |
  | `Text.Primary`, `Text.Secondary`, `Overlay.Text`, `Overlay.Sender`, `Danger`, `Success`, `Border.Subtle`, `Accent.Dim` | `WindowTextColor` |
  | `Text.Muted`, `Overlay.Original` | `GrayTextColor` |
  | `Accent`, `Accent.Bright`, `Focus.Ring` | `HighlightColor` |
  | `Text.OnAccent` | `HighlightTextColor` |
  | `Overlay.HitTest`, `Overlay.Dim` | literals `#01000000`, `#99000000` (functional, not decorative) |

  Every key added by 02–07a (for example toast and health keys) is mapped in the same table style during
  implementation; `check_resources.py` already fails on any key set difference.

### `Themes/Controls.xaml`

- **`FocusVisual.Ring`:** a `Control` template holding a `Rectangle` with `Stroke={DynamicResource Focus.Ring}`,
  `StrokeThickness=2`, `Margin=-2`, `RadiusX/Y=2`, `SnapsToDevicePixels=True`.
- Replace the five `FocusVisualStyle={x:Null}` setters with `{StaticResource FocusVisual.Ring}`.
- `IsKeyboardFocused` triggers on `CheckBox` (track `BorderBrush` = `Focus.Ring`), `Slider` (thumb stroke =
  `Focus.Ring`, thickness 2) and `ComboBox` (toggle border).

### Names sweep (REQ-OUXB-26, 28)

Every interactive control in `Pages/*.xaml`, `MainWindow.xaml`, `ComposeWindow.xaml`, `LookupWindow.xaml`,
`OverlayWindow.xaml`, `RegionSelectorWindow.xaml` and `SetupWizardWindow.xaml` that is still unnamed gets
`AutomationProperties.Name` (the visible label text) and, where a caption exists (SHOULD), `HelpText`. Templated items
bind it: phrase chips `Name="{Binding Label}"`; OCR rows `Name="{Binding Name}"` and
`Name="{Binding Name, StringFormat='Install {0} text recognition'}"`; theme cards `Name="{Binding Name}"`; title-bar
glyph buttons "Minimise", "Maximise", "Close". `Controls/HotkeyBox.cs` sets `HelpText = AccessibilityText.HotkeyBoxHelp`
and shows a `Focus.Ring` border while focused. `Pages/AppearancePage.xaml`'s swatch stroke becomes
`{DynamicResource Border.Subtle}`.

### `tools/typecheck/check_a11y.py` (new, about 100 lines, stdlib only)

`typecheck.sh` runs it after `check_resources.py`. It parses every `*.xaml` under `src/Envoy.App` with `ElementTree`
(line numbers from a light re-scan) and fails with `path:line: message` on:

1. A `Setter Property="FocusVisualStyle" Value="{x:Null}"` in a `Style` without `Setter Property="Focusable"
   Value="False"`, or a `FocusVisualStyle="{x:Null}"` attribute on an element without `Focusable="False"`.
2. A `Button`, `ToggleButton`, `TextBox`, `PasswordBox`, `ComboBox`, `Slider` or `{clr}HotkeyBox`/`SecretBox` outside a
   `ControlTemplate` without `AutomationProperties.Name` or `LabeledBy`. A `Button` is exempt when its `Content`
   attribute contains a letter outside U+E000–U+F8FF and its `Style` is not `Button.Chrome`.
3. A `CheckBox`/`RadioButton` without a `Content` attribute and without `Name`/`LabeledBy`.
4. A `Color`, `Background`, `Foreground`, `BorderBrush`, `Fill` or `Stroke` attribute starting with `#` in
   `Windows/*.xaml`, `Pages/*.xaml` or `Controls/*.xaml`. `DropShadowEffect Color="Black"` is allowed.
5. A root `Window` named `SetupWizardWindow` or `ComposeWindow` without `KeyboardNavigation.TabNavigation="Cycle"`
   (REQ-OUXB-29).

`tools/typecheck/test_check_a11y.py` (unittest, run by `typecheck.sh`) has one passing and one failing fixture per rule
under `tools/typecheck/fixtures/a11y/`.

### App: high contrast and theme resolution

- **`Services/ThemeManager.cs`:** `PaletteIds => [.. Themes.Select(t => t.Id), ThemeResolver.HighContrast]`;
  `Apply(string themeId, bool force = false)` accepts any `PaletteIds` entry; `force` reloads even when
  `themeId == CurrentId`, because `x:Static` system colours are read at load time.
- **`AppHost.ApplyTheme()`** (`ApplySettings` position 9, and `Start()`):
  `var id = ThemeResolver.Resolve(Settings.Theme, GameCatalog.Find(CurrentGameId)?.ThemeId, SystemParameters.HighContrast, Settings.Accessibility.HighContrast, ThemeManager.Themes.Select(t => t.Id).ToList());
  ThemeManager.Apply(id, force: id == ThemeResolver.HighContrast);`. It runs on the UI thread.
- **`App.xaml.cs` `OnStartup` step 8** (also in safe mode): `SystemParameters.StaticPropertyChanged` (property
  `HighContrast`) → `Dispatcher.BeginInvoke(Host.ApplyTheme)`; `Microsoft.Win32.SystemEvents.UserPreferenceChanged`
  (category `Color`, system thread) → `Dispatcher.BeginInvoke(Host.ApplyTheme)`. Both are unsubscribed in `ExitApp`
  step 5.
- **`Pages/AppearancePage.xaml`:** an "Accessibility" frame (`x:Name="AccessibilityFrame"`) between Theme and 07a's
  Notifications (contract §8.6): a "High contrast" `ComboBox` (Follow Windows / Always / Never) →
  `Accessibility.HighContrast`, with a caption that says when Windows high contrast is active.

### App: overlay readability (REQ-OUXB-36, 37)

- **`OverlayWindow.xaml`:** `EditFrame` `Background="{DynamicResource Overlay.Bg}" Opacity="0.2"`; `EmptyHint`
  `Background="{DynamicResource Overlay.Bg}"`. 07c later adds the compact template; it uses the same card brush.
- **`OverlayWindow.xaml.cs`:** card background alpha =
  `ContrastMath.EffectiveBackgroundAlpha(S.BackgroundOpacity, text, bg, S.EnsureReadableText)`, or 1 when
  `ThemeManager.CurrentId == ThemeResolver.HighContrast`; `text` and `bg` come from `TryFindResource("Overlay.Text.Color")`
  and `"Overlay.Bg.Color"`. Recomputed in `ApplySettings` and after a palette change.
- **`RegionSelectorWindow.xaml`:** `Background="{DynamicResource Overlay.HitTest}"`, dim `Fill="{DynamicResource Overlay.Dim}"`.
- **`Pages/OverlayPage.xaml`:** "Keep text readable" → `Overlay.EnsureReadableText`; (SHOULD) the caption bound to
  `MainViewModel.OverlayOpacityNote`, computed with `ContrastMath` from the current palette colours.

### App: keyboard (REQ-OUXB-29, 30)

- **`SetupWizardWindow.xaml`** (07a) already has `IsDefault` on Next/Finish and `IsCancel` on Skip; this step adds
  `TabNavigation="Cycle"` on the root if 07a did not.
- **`ComposeWindow.xaml`:** `KeyboardNavigation.TabNavigation="Cycle"` on the root; tab order Input (0), phrase chips
  (1), Copy (2), Send (3), language (4), Close (5); a `Label` "Reply _in" with `Target=TargetLanguage`. Enter is
  already handled in the input (Send); Esc already closes. `SendButton.IsDefault` stays off.
- **`MainWindow.xaml.cs`** (SHOULD): `PreviewKeyDown` Ctrl+Tab / Ctrl+Shift+Tab →
  `Navigate(PageIds.All[(i ± 1 + n) % n])`.

### App: smoke helper (SHOULD, `Services/AccessibilityAudit.cs`, new, about 70 lines)

- `FindUnnamed(DependencyObject root)`: visual-tree walk; for each visible, enabled, focusable `ButtonBase`,
  `TextBoxBase`, `PasswordBox`, `ComboBox` or `Slider`, `UIElementAutomationPeer.CreatePeerForElement(c)?.GetName()`;
  returns `"{Type} '{x:Name or nearest named ancestor}'"` for empty names.
- `FindUnreachable(Window w)`: focus the first focusable element, `MoveFocus(Next)` up to 200 times, and return the
  eligible controls never visited.

### Allowlists and policy (contract §5)

- **P/Invoke additions:** none. High contrast uses `SystemParameters` and `SystemEvents`, which are managed APIs.
- **Network allowlist:** no change. **Process launching:** none.

### Threading

- `ApplyTheme`, palette loads and overlay alpha run on the UI thread. `UserPreferenceChanged` fires on a system thread
  and is marshalled; `StaticPropertyChanged` is marshalled with `BeginInvoke` to stay re-entrancy-safe.
- `check_a11y.py` and `ContrastMath` have no threading concerns.

### Failure behaviour

| Situation | Behaviour |
| --- | --- |
| High-contrast palette fails to load | `ThemeManager.Apply` falls back to `imperial` (existing behaviour); the smoke loop over `PaletteIds` catches it in CI. |
| A contrast-theme change event is missed | Colours update on the next `ApplySettings()` or restart. |
| A system colour pair has low contrast (user's own theme) | Envoy follows the user's choice; the overlay alpha is 1 in high contrast. |
| Palette XAML not found by the unit test | The test fails (it walks up to `Envoy.sln`; a missing file is an error, not a skip). |

## Acceptance criteria

| Id | REQ | Verified by |
| --- | --- | --- |
| AC-OUXB-1 | 24 | Typecheck: `check_a11y.py` rule 1 passes on the tree; `test_check_a11y.py` rule-1 fixtures. Smoke `focus-visual({palette})` (MainWindow-dependent, every palette): the resolved `Button`, `CheckBox`, `Slider` and `NavItem` styles have a non-null `FocusVisualStyle`, and `FindResource("Focus.Ring")` is a `SolidColorBrush`. |
| AC-OUXB-2 | 25 | Unit `PaletteContrastTests.Focus_ring_meets_3_to_1` (Theory over game palettes × 4 backgrounds), `All_palettes_define_focus_ring` (includes `highcontrast`). |
| AC-OUXB-3 | 26 | Typecheck: `check_a11y.py` rules 2–3 pass on the tree and the fixtures behave. |
| AC-OUXB-4 | 29 | Typecheck: `check_a11y.py` rule 5. Smoke `keyboard` (first palette, other-windows phase): the guide's Next has `IsDefault` and Skip has `IsCancel`; compose's root has `TabNavigation == Cycle`. M-OUXB-1. |
| AC-OUXB-5 | 32 | Typecheck: `check_resources.py` passes with the sixth palette. Smoke: the per-palette loop includes `highcontrast` (pages, binding listener and screenshots `highcontrast-settings-*.png`; the teardown count is `PaletteIds.Count × PageIds.All.Count`); the guide screenshot list gains `highcontrast`. |
| AC-OUXB-6 | 33 | Unit `ThemeResolverTests.Follow_windows_uses_high_contrast_when_system_on`, `Never_ignores_system`, `Always_forces`, `Match_game_uses_game_theme_else_default`, `Unknown_theme_falls_back_to_default`. M-OUXB-2 (live switching). |
| AC-OUXB-7 | 35 | Unit `PaletteContrastTests.Text_colours_meet_4_5_to_1` (Theory: every game palette × 4 text keys × 4 backgrounds), `Overlay_text_meets_4_5_on_opaque_bg`. Typecheck: `check_a11y.py` rule 4. |
| AC-OUXB-8 | 36 | Unit `ContrastMathTests.Minimum_alpha_meets_target_over_white_grey_black`, `Minimum_alpha_is_one_when_unreachable`, `Effective_alpha_never_lowers_user_choice`, `Effective_alpha_ignores_floor_when_disabled`. Smoke `overlay-readable` (other-windows phase, every palette): with `BackgroundOpacity = 0.2` and the floor on, the card background alpha is ≥ `MinimumBackgroundAlpha` for the palette's colours (1 for `highcontrast`); with the floor off it is 0.2. M-OUXB-3. |

SHOULD items: smoke `a11y-audit` (first palette; REQ-OUXB-27 and 31, `FindUnnamed` and `FindUnreachable`, log
`a11y: ok (N controls)`), M-OUXB-4 (Narrator, REQ-OUXB-28), M-OUXB-5 (Ctrl+Tab, REQ-OUXB-30), and
`AccessibilityTextTests.Raised_formats_percent` (REQ-OUXB-37).

## Test plan

### Unit tests (`tests/Envoy.Core.Tests/Ux/`)

| File | Tests |
| --- | --- |
| `ContrastMathTests.cs` | `Black_white_is_21`, `Ratio_symmetric`, `Parse_accepts_argb`, `Minimum_alpha_meets_target_over_white_grey_black`, `Minimum_alpha_is_one_when_unreachable`, `Effective_alpha_never_lowers_user_choice`, `Effective_alpha_ignores_floor_when_disabled` |
| `PaletteContrastTests.cs` | `Text_colours_meet_4_5_to_1`, `Focus_ring_meets_3_to_1`, `Overlay_text_meets_4_5_on_opaque_bg`, `All_palettes_define_focus_ring`. Reads `src/Envoy.App/Themes/Palette.<id>.xaml` with `XDocument` (`<Color>` elements only), found by walking up from `AppContext.BaseDirectory` to `Envoy.sln`. Game palettes = every `Palette.*.xaml` except `highcontrast`, so a new palette is covered automatically. |
| `ThemeResolverTests.cs` | the five tests in AC-OUXB-6 |
| `AccessibilityTextTests.cs` (SHOULD) | `Raised_formats_percent` |

### Typecheck

- `check_a11y.py` and `test_check_a11y.py` run in `typecheck.sh` after `check_resources.py`.

### Smoke-test sections (contract §6 placement)

1. **Per palette, step 1:** for `highcontrast`, set `Accessibility.HighContrast = Always` and call
   `ThemeManager.Apply(id, force: true)` instead of setting `Settings.Theme`; restore `FollowWindows` after the
   palette.
2. **Per palette, MainWindow-dependent checks:** `focus-visual({palette})` (after 07a's checks); `a11y-audit`
   (SHOULD, first palette, `FindUnnamed` on every page).
3. **Per palette, other windows:** `keyboard` (first palette); `overlay-readable({palette})`; `a11y-audit` continues
   on each guide step, compose, lookup and the overlay in edit mode (SHOULD); guide screenshots for `highcontrast`.

Estimated added time: one more palette (about the cost of one existing palette, under 30 s) plus about 1 s.

### Manual pass (07b appends to `docs/releasing.md` under "Accessibility"; 08b consolidates)

1. **M-OUXB-1** (REQ 24, 29). Keyboard only: Tab through the guide (Enter = Next, Esc = Skip) and the reply box (Tab
   wraps, Enter sends, Esc closes). The focus ring is visible on every control in each palette.
2. **M-OUXB-2** (REQ 33). Windows Settings → Accessibility → Contrast themes → Night sky while Envoy is open: the
   settings window, overlay and reply box switch within a second; switch to Desert: they update again; turn it off:
   the game theme returns. Set "High contrast" to Never: Envoy keeps the game theme under Night sky.
3. **M-OUXB-3** (REQ 36). Overlay background at 20% over a bright map: with "Keep text readable" on, text stays
   readable; off, the card is as transparent as chosen.
4. **M-OUXB-4** (SHOULD). Narrator (Win+Ctrl+Enter): title-bar buttons, hotkey boxes, OCR rows, phrase chips and the
   guide step list announce meaningful names.
5. **M-OUXB-5** (SHOULD). Ctrl+Tab cycles the nine pages, wrapping at both ends.

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Settings/AppSettings.cs` | `Accessibility` section, `HighContrastMode`, `Overlay.EnsureReadableText` | **yes** |
| `src/Envoy.Core/Ux/{ContrastMath,ThemeResolver,AccessibilityText}.cs` | new | |
| `tests/Envoy.Core.Tests/Ux/*` | tests above | |
| `src/Envoy.App/AppHost.cs` | `ApplyTheme` via `ThemeResolver` (`ApplySettings` position 9) | **yes** |
| `src/Envoy.App/App.xaml.cs` | high-contrast listeners at step 8, unsubscribed in `ExitApp` | **yes** |
| `src/Envoy.App/Services/ThemeManager.cs` | `PaletteIds` + `highcontrast`, `Apply(id, force)`, `MatchGame` from `ThemeResolver` | yes (01) |
| `src/Envoy.App/Services/AccessibilityAudit.cs` | new (SHOULD) | |
| `src/Envoy.App/Themes/Controls.xaml` | `FocusVisual.Ring`, five `{x:Null}` replaced, focus triggers | |
| `src/Envoy.App/Themes/Palette.{classic,frontier,imperial,olympian,parchment}.xaml` | `Focus.Ring`, `Overlay.HitTest`, `Overlay.Dim`; adjusted `Text.Muted`, `Danger` | |
| `src/Envoy.App/Themes/Palette.highcontrast.xaml` | new | |
| `src/Envoy.App/Windows/{MainWindow,ComposeWindow,LookupWindow,OverlayWindow,RegionSelectorWindow,SetupWizardWindow}.xaml` | names; tab cycle; palette keys | yes (07a, 07c) |
| `src/Envoy.App/Windows/OverlayWindow.xaml.cs` | readable alpha | yes (05, 07c) |
| `src/Envoy.App/Windows/MainWindow.xaml.cs` | Ctrl+Tab (SHOULD) | yes |
| `src/Envoy.App/Pages/*.xaml` | names sweep; Appearance "Accessibility" frame and swatch stroke; Overlay "Keep text readable" | yes |
| `src/Envoy.App/ViewModels/MainViewModel.*.cs` | `OverlayOpacityNote` (SHOULD) | yes |
| `src/Envoy.App/Controls/HotkeyBox.cs` | HelpText, focus border | |
| `src/Envoy.App/SmokeTest.cs` | sections above | **yes** |
| `tools/typecheck/check_a11y.py`, `test_check_a11y.py`, `fixtures/a11y/*.xaml`, `typecheck.sh` | new; run them | |
| `docs/theming.md`, `docs/releasing.md`, `CLAUDE.md`, `CHANGELOG.md` | `Focus.Ring`, `Overlay.HitTest`/`Overlay.Dim` and the contrast rule; M-OUXB-1…5; "`check_a11y.py` enforces names and no literal colours"; changelog | yes |

Estimated size: Core about 150 lines plus tests about 200; App about 350 (mostly XAML attributes); Python about 160.

## Risks & open questions

- **Parchment `Text.Muted` change is large.** Reaching 4.5:1 against `Bg.Window` needs about `#5A4830`, which narrows
  the gap to `Text.Secondary`. Alternative: measure captions only against the backgrounds they sit on (`Bg.Panel`,
  `Bg.PanelAlt`). Keep the four-background rule unless the parchment screenshot looks wrong; if relaxing, record the
  exact pairs in `PaletteContrastTests` and under Deviations.
- **The readable-overlay floor changes visuals** for anyone who set the background below about 0.65. That is intended,
  explained by the SHOULD caption, and can be turned off.
- **High contrast keeps the display font and decorative frames.** Fonts live in `Controls.xaml`; deferred.
- **`x:Static` system colours are read once at load.** Mitigated by `force` reloads on both change events.
- **Names drift from labels.** The lint checks presence, not equality. Acceptable for 1.0.
- **Late lint.** `check_a11y.py` lands after 02–07a. Their controls were named under 01's `CLAUDE.md` rule, so the
  sweep should be small; anything left is fixed in this step, never exempted, except through the documented rules.

## Revision notes

Changes made when splitting `07-onboarding-ux-accessibility.md` and revising against `00-integration.md` and the set
critique:

- **Split (critique major: scope).** Accessibility is its own step, landing after 02–07a as the critique and contract
  §1 recommend, so `check_a11y.py` does not fail on other areas' controls. Ids keep their numbers with the `OUXB`
  prefix.
- **`check_a11y.py` against other areas' code (critique major).** 01's `CLAUDE.md` naming rule covers 02–07a; this step
  names 01's moved controls. `RegionSelectorWindow`'s `#01000000` and `#99000000` become palette keys
  `Overlay.HitTest` and `Overlay.Dim` (contract §8.8). These are the only literal colours in the high-contrast palette,
  and they are documented as functional. Rule 4 now also covers `Stroke`, and the Appearance swatch is fixed. Rule 2
  covers `ToggleButton`.
- **Smoke structure (contract §6).** `PaletteIds` gains `highcontrast` here, not in 01. The focus-visual check sits
  among the MainWindow-dependent checks. Acceptance criteria name `PaletteIds`, never "six palettes". The runtime audit
  is SHOULD and runs on the first palette only.
- **Start-up and settings order (contract §3, §4).** Theme resolution is `ApplySettings` position 9. The high-contrast
  listeners are at `OnStartup` step 8 (also in safe mode) and are removed in `ExitApp` step 5.
- **Scope (contract §9).** MUSTs cut from 11 in this part of the old spec to 8. REQ-OUX-34 folded into 33; 38 into 35;
  27, 28, 30, 31 and 37 are SHOULD. REQ-OUXB-29 gained a lint rule and a smoke check, so it is verifiable without a
  manual-only criterion. Live switching keeps a numbered manual step.
- **Overlay readability placement.** `EnsureReadableText` and its checkbox moved here from the old overlay section,
  because REQ-OUXB-36 is a 07b MUST in contract §9. 07c's compact template uses the same card brush.
- **UI strings (contract §8.7).** `AccessibilityText` holds the caption and HotkeyBox help.
- **Allowlists.** No P/Invoke, network or process-launch additions.
