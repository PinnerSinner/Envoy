# The Herald's Writ: visual redesign of every window

Status: Implemented (step 12, after 08b). `docs/images/` holds the Windows CI screenshots of this look (Deviation 12). Revised against `00-integration.md` (binding); where this spec adds a
shared concept it names the owner here. Differences from the text below are under Deviations.

Area id: `09a-herald-redesign` · Requirement prefix: `HRD`.

## Context

The app still wears the v0.1 look: flat palettes, Cinzel headings, generic controls. The approved design is "The
Herald's Writ", a lavish illuminated-manuscript style in the spirit of Kingdom Come: Deliverance 2's menus: oak and
leather, vellum charters with knotwork borders, gold leaf, wax seals, banners, ink drawings. Everything is ornamented;
nothing is left as a bare void.

The reference designs live in `docs/design/herald/` (HTML; open them in a browser). They are the source of truth for
layout, colour, type and ornament. Copy measurements and colours from them; do not reinvent.

| Reference | Window |
| --- | --- |
| `WarRoom.dc.html` | `MainWindow` and every settings page (the war table) |
| `Main.dc.html` | `OverlayWindow` (dispatch scrolls) and `ComposeWindow` (the scribe's desk) |
| `Setup.dc.html` | `SetupWizardWindow` (the oath) |
| `Tokens.dc.html` | Palette, type, components and ornaments |
| `Explainer.dc.html` | The Tale window (spec 09b) |

The CSS animations in those files belong to 09b. 09a delivers the static look; 09b makes it move.

## Goals / Non-goals

**Goals**

- Every window matches its reference design, under every palette, with the accessibility guarantees of 07b intact.
- One ornament kit in `Themes/` that every window uses, so the look stays consistent.
- No em dashes and no filler writing anywhere a user can read it.

**Non-goals**

- Animation and the Tale window (09b).
- 3D. The canvas used three.js for a torchlit wall and a spinning seal; WPF gets flat equivalents (09b COULD).
- New features or settings beyond the ones named here.

## Requirements

### Palette, liveries, type

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-HRD-1 | MUST | A new default palette `herald` (`Themes/Palette.herald.xaml`) with the tokens of `Tokens.dc.html`: Gules `#8E2418`, Gules.Dark `#6E1A10`, Or `#D8B04A`, Or.Light `#E9CC7A`, Or.Dark `#8C6516`, Vellum `#EAD8B4` (charter gradient `#F6EBD2` to `#D3B987`), Oak `#3A2414`, Oak.Deep `#2B1A0E`, Ink `#2A1D12`, Ink.Soft `#5C4529`, Leather.Text `#D9C298`. Existing keys keep their names and gain herald values; new keys (`Wax.*`, `Or.*`, `Vellum.*`, `Oak.*`, `Livery.Primary`, `Livery.Secondary`, `Ornament.*`) go into every palette and into `Palette.highcontrast.xaml` as `SystemColors` values. `PaletteContrastTests` pass for every palette. |
| REQ-HRD-2 | MUST | The five game themes become liveries of the herald look: same oak, vellum and ink, different `Livery.Primary`/`Livery.Secondary` and wax: `imperial` (Age of Empires IV) gules and or, `parchment` (Age of Empires II) azure `#2B4C8C` and or, `frontier` (Age of Empires III) vert `#2F5E35` and argent, `olympian` (Age of Mythology) purpure `#5B2D73` and or, `classic` (Age of Empires) sable and or. Ids stay, so saved settings keep working; auto-switch to the running game keeps working. `ThemeManager` descriptions are rewritten without em dashes (e.g. "Age of Empires IV livery: gules and or"). `herald` is the default for new settings files. |
| REQ-HRD-3 | MUST | Embedded OFL fonts with their licence files in `Assets/Fonts/`: UnifrakturMaguntia (wordmark and drop caps only), IM Fell English SC (titles, buttons, tab names, rubrics), IM Fell English italic (originals, asides), EB Garamond regular, italic and semibold (all reading text). Get the TTFs from the google/fonts repository or convert the `@fontsource` woff2 files with `fonttools` (pip). Font families are palette-independent resources (`Font.Wordmark`, `Font.Title`, `Font.Body`, `Font.Aside`) with a CJK and Cyrillic fallback chain ending in `Microsoft YaHei UI, Malgun Gothic, Yu Gothic UI, Segoe UI`. Cinzel is removed. |

### Ornament kit and controls

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-HRD-4 | MUST | `Themes/Ornaments.xaml` (merged by `App.xaml`) and `Controls/` provide: `CharterFrame` (vellum panel, knotwork border band as a tiled `DrawingBrush`, gold edge lines, corner bosses, filigree corners), `Fleuron` (the divider), `DropCap` (gold box, blackletter letter), `WaxSeal` (glyph, colour), `ScrollRods` (rolled ends for dispatches), `Banner` (swallowtail tab shape), `Damask` (lozenge lattice brush for oak and leather). Geometry comes from the SVG paths in the reference files. Colours come only from palette keys (07b rule: no literal colours in `Windows/`, `Pages/`, `Controls/`). |
| REQ-HRD-5 | MUST | `Controls.xaml` restyles every control: primary button as a wax seal (round, or a pill with a seal for long labels), secondary as a gold-edged plaque, `ToggleButton`/`CheckBox` switches as seal switches (plain wax slides and stamps when on), `ComboBox` and `TextBox` as vellum plaques with an ink underline, `Slider` with a gules track, thin gold `ScrollBar`, `Callout` as a dispatch card. Every focusable style keeps `FocusVisual.Ring` (gold, 3:1 on every background). Every interactive control keeps its `AutomationProperties.Name`. |

### Windows

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-HRD-6 | MUST | `MainWindow` is the war table of `WarRoom.dc.html`: an oak and damask background; a left rail with the gold blackletter wordmark, its flourish, swallowtail banner tabs and the current livery's crest; a `CharterFrame` holding the page with a drop-cap header, a one-line lede, a fleuron, the page content and, at 1100 px wide and above, a marginalia column with that page's ink drawing (the nine figures in `WarRoom.dc.html`). Each tab shows a playful name and its plain name: War Room / Home, Interpreters / Translation, Scouts / Reading chat, Heralds / Overlay, Couriers / Replying, Codex / Phrasebook, Seals / Hotkeys, Livery / Appearance, Chronicle / About. The active tab drops out and carries a gold stitch. Page ids and `PageFor` stay as they are. |
| REQ-HRD-7 | MUST | Every settings page uses the kit: section headings as rubrics, rows on ruled vellum with a small drop-cap square, values in plaques, provider health as candles (lit, guttering, snuffed: static here, animated in 09b). No page shows a bare empty area: empty and error states are illustrated `Callout`s (07a `PageHints` text). The War Room order of contract §8.6 is unchanged. |
| REQ-HRD-8 | MUST | `OverlayWindow`: `FullItem` is a dispatch scroll (rods, vellum, a wax seal with the language glyph, sender rubric, translation, italic original); `CompactItem` is a one-line ribbon with a small seal. Seal colour and glyph come from a Core `LanguageSeal.For(languageCode)` with unit tests (中 Chinese, 한 Korean, 日 Japanese, Р Russian, others the first letter of the language name). 07b's alpha and contrast rules for overlay text still hold. |
| REQ-HRD-9 | MUST | `ComposeWindow` is the scribe's desk (oak frame, vellum, drop cap R with "eply to {name}", language ribbons, "Your words" field, "The scribe writes" line, "Means:" line, phrase ribbons, wax "Seal & send"); `SetupWizardWindow` is the oath (seal chain for the steps); `ToastWindow` is a leather plate with a gold edge; `LookupWindow` and `RegionSelectorWindow` use the same frame and plate. |

### Writing

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-HRD-10 | MUST | No em dash (U+2014) and no en dash (U+2013) in any user-facing text: XAML, every Core `*Text` class, `PageHints`, `NoticeText`, `ThemeManager`, and `docs/*.md` and `CHANGELOG.md` (rewrite each sentence; do not swap in a hyphen). Writing is plain and short: no filler words. A new typecheck rule **R15** fails on U+2014 or U+2013 in `src/**/*.xaml`, `src/Envoy.Core/**/*Text.cs`, `src/Envoy.Core/Ux/*.cs`, `docs/*.md`, `CHANGELOG.md` and `README.md`, and on these words in the same files: seamless, seamlessly, leverage, robust, elevate, delve, unleash, empower, cutting-edge, game-changer, effortless. R15 has Python tests. `docs/specs/` and `docs/design/` are exempt (history and reference). |
| REQ-HRD-11 | SHOULD | Medieval flavour lives in names and headings only. Every instruction, error and setting label says plainly what it does. |

### Carry-over from the 07a review

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-HRD-12 | MUST | Fix the five 07a review findings: (1) the guide's step list cannot skip the privacy step while privacy is not acknowledged; (2) Skip or close on the auto-opened guide opens the War Room so Envoy is never invisible; (3) `Host.Notices?.Hold(true)` before `Host.Start()` when the guide will auto-open; (4) `SoakTest` no longer calls `SetPrivacyAcknowledged`; (5) `CheckGuideEntryAsync` resets `FirstRunCompleted` and saves, so close-flush tests something again. |

### Verification

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-HRD-13 | MUST | The smoke test renders every window and page under every palette (including `herald`) with zero binding errors; the screenshots job publishes the new look. `docs/images/` is refreshed from the screenshots branch. Do not edit `README.md` (the maintainer owns it) beyond what R15 forces. |

## Design notes

- Knotwork band: tile of 28 × 18 with two crossing curves and gold pips (see `band()` in the canvas sources); use a
  `DrawingBrush` with `TileMode=Tile` and `ViewportUnits=Absolute`, one horizontal and one vertical variant.
- Wax: radial gradient from a light spot at 35 % / 30 % to the dark wax colour; inner shadow; a 2 px darker drop.
- Drawings: each marginal figure is a handful of stroke paths (`stroke-linecap=round`) in Ink.Soft with the last path
  in Gules. Keep them as `Geometry` resources so 09b can animate `StrokeDashOffset`.
- Sizes: titles 32 to 36 px, body 17 to 19 px, rubrics 13 to 16 px, tab names 18 px; minimum touch target 44 px.

## Acceptance

| Req | Check |
| --- | --- |
| HRD-1, 2 | `PaletteContrastTests`, `ThemeManagerTests` (default `herald`, ids unchanged), smoke per palette |
| HRD-3 | Smoke asserts the four font families resolve (no fallback to Segoe UI for Latin text); OFL files present (policy `required-docs`) |
| HRD-4, 5 | `check_a11y.py` (names, focus visuals, no literal colours); smoke renders a control gallery section `herald-controls({palette})` |
| HRD-6, 7, 9 | Smoke sections `war-table({palette})`, `pages({palette})`, `desk({palette})`, `oath({palette})`; screenshots |
| HRD-8 | `LanguageSealTests`; smoke renders both overlay templates under every palette |
| HRD-10 | typecheck R15 and its tests |
| HRD-12 | The tests and smoke assertions each fix names |
| HRD-13 | Windows CI green; screenshots on `ci-screenshots` |

## Risks

- Ornament cost: tiled `DrawingBrush` and many paths can be slow in the overlay. Freeze every brush and geometry; the
  overlay uses only rods, vellum and the seal.
- Font licensing: OFL requires the licence text ship with the fonts. Add each `OFL.txt` (one per family, renamed).
- Concurrent pushes: the maintainer may push README or docs changes to `main` while this lands. If a push is
  rejected, `git pull --rebase origin main` and push again; never force-push.

## Deviations

Owners of the concepts this spec adds: Core `ThemeCatalog` (liveries), `PageHeadings` (tab names, drop caps, ledes,
figure captions), `LanguageSeal` (overlay seals), `ProviderCandle` (candles) and `ReplyHeading` (the desk heading);
`Themes/Ornaments.xaml` (the kit); `tools/themes/make_palettes.py` (the palettes).

1. **Ornaments.xaml is merged by Controls.xaml** (REQ-HRD-4), which App.xaml merges after the palette. WPF resolves a
   `StaticResource` only through the dictionary that loads it and its own merged dictionaries, so Controls.xaml's
   styles (the fleuron rule, the wax seal in the callout) need the kit merged there. The effect for windows is the same.
2. **Herald is in the livery picker** as the first card, and "Follow the game" uses it when no game runs
   (`ThemeResolver.Default`). New settings files start on `herald` (REQ-HRD-2), so a new user no longer follows the
   game until they pick "Follow the game"; saved settings keep their choice.
3. **Classic keeps the vellum.** The reference's "Night watch" livery is a dark charter; REQ-HRD-2 says every livery
   shares oak, vellum and ink, so Classic is sable and or on vellum.
4. **Palettes are generated** by a new `tools/themes/make_palettes.py`, so the seven files keep identical keys. Every
   brush in a palette is frozen (`po:Freeze`); the geometry in Ornaments.xaml is frozen by its type converter.
5. **The charter heading lives in MainWindow** (REQ-HRD-6): the drop cap, title, plain name and lede follow the checked
   tab, so the pages lost their own `Text.H1` titles. Each page keeps its intro paragraph. The banner tabs are 50 px tall
   (the reference: 56). The rail's parts do not all fit every window height, so Core `RailLayouts.For` picks what the
   rail shows: on a shorter rail it first slims the tabs to 44 px (still a full touch target), then folds away the
   crest, the motto under the wordmark and the two status lines, in that order; the wordmark, the nine tabs and the
   version always stay. The crest is drawn at 60 % (the reference: 75 %) so it fits beside compact tabs in the default
   820 px window. The status lines no longer wrap (they trim, with the full text as a tooltip), so their height is
   fixed. The `war-table` smoke section checks that the last tab ends above the rail's foot at the default height and
   at `MinHeight`.
6. **Ruled rows** (REQ-HRD-7) are a 1 px rule above each `SettingRow` (`Row.Rule`) rather than a ruled background, so
   every rule sits on a row edge. The drop-cap square (`RowLabel`) is on every row whose label is a literal; rows whose
   label is a binding or a two-line block (War Room "Get ready", the first two Translation rows) keep a plain label.
7. **Callouts and banners use seals** (E for information, ! for a warning or error) instead of Segoe icon glyphs.
   Provider health's heading reads "The candles: is each translator answering?"; its button stays "Retry now"
   (REQ-HRD-11). `ProviderState.Unknown` covers both "never called" and "not set up" (a `Configuration` failure), so
   `ProviderCandle.For` also takes the last error kind: never called is lit, not set up is snuffed.
8. **Language seals** (REQ-HRD-8): Japanese is 日 on or (the reference's amber wax); a language Envoy does not know shows
   "?" on sable. The overlay text lost its drop shadow: the vellum card, whose alpha 07b keeps readable, carries it.
9. **Fonts** (REQ-HRD-3): EB Garamond ships as three static instances of the Google Fonts variable font (WPF reads no
   variation axes), made with fonttools, with typographic and WWS family names so the three faces form one family. The
   four OFL texts are `requiredDocs` in `tools/ci/policy.json` and listed in `THIRD-PARTY-NOTICES.md`. The smoke test
   checks the fonts once (`herald-fonts`), not per palette, because they do not depend on the palette.
10. **Writing** (REQ-HRD-10): the compatibility matrix marked an unverified cell with an em dash; it now reads
    `untested` (the policy check follows). `PhrasebookText.Missing` reads "not yet translated". The built-in "I'm using
    a translator" phrase (en, ru, de, pl) lost its dash, and R15 now also scans `src/Envoy.Core/Phrases/*.json`, since
    Envoy types those phrases into game chat. Code-built strings
    outside R15's globs (the tray tooltip, the lookup window, language display names, the slang gloss, the transcript
    header) were rewritten too; R15 itself scans only the globs this spec names.
11. **Carry-over** (REQ-HRD-12): finding 3 (notices held before `Host.Start()` when the guide will open by itself) is
    App start-up code the smoke test cannot drive; it is covered by M-OUX-1 and code review. Findings 1, 2 and 5 have
    unit tests (`OnboardingFlowTests`, `OnboardingGateTests`) and `guide-entry` smoke checks. Finding 1 covers every
    way in, not only the step list: a new guide opens at `OnboardingGate.StartStep`, which keeps it on Language while
    privacy is not acknowledged (the tray's cheat sheet, the resume of `LastStep` after a `PrivacyText.Version` bump, a
    page link). For finding 5, close-flush clears `FirstRunCompleted` and saves again right before it closes
    `MainWindow` (earlier checks close other `MainWindow`s, which set it) and first checks the file reads false. CLAUDE.md's privacy rule
    now names the soak host's exception (finding 4).
12. **Verification** (REQ-HRD-13): the smoke sections are `herald-fonts`, `war-table({palette})`,
    `herald-controls({palette})`, `desk({palette})` and `oath({palette})`, beside the existing `pages({palette})`;
    the setup-guide screenshots are now taken for `herald` instead of `imperial`. `docs/images/` was refreshed in
    a follow-up commit, once the Windows CI run on `main` had published the new look to `ci-screenshots`; it also gained
    four `herald-*` images (War Room, overlay, reply box, setup guide). That run showed the reply box taller than the
    768 px CI screen, which cut off its footer, so the desk is capped at the work area, its phrase ribbons scroll and the
    `desk` smoke check asserts that "Seal and send" ends inside the window and the window inside the work area. A new manual step, M-HRD-1, compares every window with the references. The CI
    desktop is 1024x768 and Windows keeps a window within the screen, so the `war-table` section drives the marginal
    column's rule with a 1180 px width directly (`MainWindow.LayoutWarTable`) and checks that the real window agrees with
    `PageHeadings.ShowsMarginalia` at whatever width the screen allows; CI screenshots of the settings window therefore
    show the narrow layout without the marginal drawing.
13. **README**: this change edits README.md only where R15 forces it. The maintainer has since asked for a redesigned
    README (download, setup and use, real game footage); that supersedes this spec's "do not edit" note and is left to
    its own change.
14. **Accessibility**: ornaments are non-focusable elements, which the 07b audit skips; banner tabs carry the plain page
    name as their accessible name.
15. **Contrast on gradients** (REQ-HRD-1, 07b): `PaletteContrastTests` also checks rubrics (`Accent`) on the darkest
    stop of the charter's `Vellum.Gradient` (now the palette key `Vellum.Deep`) and the seal glyph on the halfway blend
    of every wax. To pass, Frontier's vert is `#2A5530` (REQ-HRD-2 names `#2F5E35`, 4.21:1 on that stop) and
    `Wax.Or.Light` is `#957026`. The gradient's last stop is `#D7BE91`, not REQ-HRD-1's `#D3B987` (which stays as
    `Vellum.Dark`): on `#D3B987` Parchment's azure rubric is 4.40:1, so the stop is a shade lighter. Plain wax (the
    setup guide's unvisited step numbers) is pale, so its glyph is ink, `Wax.Plain.Text`, not `Wax.Text` (1.9 to
    2.6:1). `Success` is `#2F5229` (was `#3F6B3A`, 3.5 to 4.4:1 on vellum) and is now a checked text colour, on the four
    backgrounds and on `Vellum.Deep`, with the other text colours. In high contrast the drop cap is window text on the window colour (it was window text
    on the highlight colour). The `WaxSeal` style's SemiBold weight is set back to Normal wherever a seal uses
    `Font.Title` or `Font.Wordmark`, which have only a Regular face, so WPF never fakes a bold; `Font.Wordmark` gains the
    same CJK fallback chain as the other font resources.
16. **One Herald** (REQ-HRD-11): the read-aloud feature was also called "Herald". Its Overlay heading and its
    CHANGELOG, README and getting-started entries now say "Read aloud", so "Herald" names the livery (and, in the
    plural, the Overlay tab's playful name).
17. **Room for the rows** (REQ-HRD-6, 7): the rail, the charter and the marginal column left the pages less width than
    their fixed rows needed, so boxes and drop-downs were cut off. Setting rows' label columns are 220 px (were 260 and,
    on Hotkeys, 300), the charter's side padding is 36 px (was 54), the marginal column is 140 px (was 160), and the
    drawing shows from 1180 px, the default window (REQ-HRD-6 says 1100 px). The `rows-fit` smoke section checks that
    every setting row's controls end inside the row at the default size, at `MinWidth`, and at the narrowest width that
    shows the drawing.
