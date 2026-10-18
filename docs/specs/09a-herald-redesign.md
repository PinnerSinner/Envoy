# The Herald's Writ: visual redesign of every window

Status: Proposed. Lands as step 12, after 08b. Revised against `00-integration.md` (binding); where this spec adds a
shared concept it names the owner here.

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

(None yet.)
