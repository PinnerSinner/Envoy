# Theming

Every pixel of Envoy's UI (settings, overlay, reply box, setup guide and region picker) is drawn from one palette
dictionary, an ornament kit and a shared control library. Switching livery swaps the palette at runtime. Every control
uses `DynamicResource`, so the change is instant.

## The Herald's Writ

Envoy's look is "The Herald's Writ": an illuminated manuscript on a war table. Oak and damask walls, vellum charters
with knotwork borders, gold leaf, wax seals, swallowtail banners and ink drawings. The reference designs are in
`docs/design/herald/` (open the `.dc.html` files in a browser); measurements, colours and paths come from them.

| Window | Reference | What it looks like |
| --- | --- | --- |
| Settings (`MainWindow` and every page) | `WarRoom.dc.html` | The war table: the banner rail on oak, the page on a charter with a drop cap, a lede, a fleuron and the page's ink drawing |
| Overlay | `Main.dc.html` | Dispatch scrolls: rods, vellum, a wax seal with the sender's language |
| Reply box | `Main.dc.html` | The scribe's desk: "Reply to {name}", "Your words", "The scribe writes", "Seal and send" |
| Setup guide | `Setup.dc.html` | The oath: a chain of seals, one per step |
| Palette, type, ornaments | `Tokens.dc.html` | The tokens below |

Medieval flavour lives in names and headings only. Every tab shows its plain name under the playful one
(Interpreters, Translation), and every instruction, error and setting label says plainly what it does.

## Liveries

| Id | Name | Game | Tinctures |
| --- | --- | --- | --- |
| `herald` | Herald | none (Envoy's own arms, the default) | gules and or |
| `imperial` | Imperial | Age of Empires IV | gules and or |
| `parchment` | Parchment | Age of Empires II | azure and or |
| `frontier` | Frontier | Age of Empires III | vert and argent |
| `olympian` | Olympian | Age of Mythology | purpure and or |
| `classic` | Classic | Age of Empires | sable and or |

Every livery shares the oak, vellum and ink; only `Livery.Primary`, `Livery.Secondary`, the livery wax and the rubric
colour (`Accent`) change. New settings start on `herald`. **Follow the game** picks the running game's livery (and
`herald` with no game). The ids never change, so saved settings keep working. The list lives in Core
`ThemeCatalog`.

A seventh palette, `highcontrast`, is not in the picker: Envoy switches to it when a Windows contrast theme is on
(Appearance › Accessibility › High contrast: **Follow Windows**, the default), or always with **Always**. Its colours
are Windows' own system colours (`SystemColors`), read when the palette loads, so Envoy reloads it whenever the
contrast theme changes. Its ornaments are flat: no gradients, no knotwork.

Fresh renders of every window in every livery are pushed to the `ci-screenshots` branch on each build of `main`.

All the decoration is original or comes from open-licence fonts. No game assets, logos or fonts are used.

## Type

The fonts are embedded (SIL Open Font License 1.1; each family's licence is next to it in `Assets/Fonts/`):

| Resource | Font | Used for |
| --- | --- | --- |
| `Font.Wordmark` | UnifrakturMaguntia | The wordmark and drop caps only |
| `Font.Title` | IM FELL English SC | Titles, buttons, tab names, rubrics |
| `Font.Aside` | IM FELL English italic | Originals and asides |
| `Font.Body` | EB Garamond (regular, italic, semibold) | All reading text |

Each chain ends in `Microsoft YaHei UI, Malgun Gothic, Yu Gothic UI, Segoe UI`, so Chinese, Korean and Japanese fall
back to the Windows UI fonts. EB Garamond covers Latin, Greek and Cyrillic. Sizes: titles 30 to 36 px, reading text
17 to 20 px, rubrics 13 to 16 px, tab names 18 px. The smoke test checks that each family resolves to its own face.

## Palette keys

Each `Themes/Palette.<id>.xaml` defines the same keys. CI and `tools/typecheck/typecheck.sh` fail if they drift
apart. Every brush also has a matching `*.Color` resource.

| Key | Used for |
| --- | --- |
| `Bg.Window`, `Bg.Panel`, `Bg.PanelAlt`, `Bg.Input` | Vellum shades: cards, raised panels, inputs |
| `Accent`, `Accent.Bright`, `Accent.Dim` | The livery's rubric colour, its darker shade, and gold edges |
| `Text.Primary`, `Text.Secondary`, `Text.Muted`, `Text.OnAccent` | Ink on vellum; text on wax and livery fills |
| `Border.Subtle`, `Danger`, `Success` | Rulings and hairlines; errors and confirmations |
| `Overlay.Bg`, `Overlay.Text`, `Overlay.Sender`, `Overlay.Original` | The dispatch scroll (its card alpha comes from settings and the readable-text floor) |
| `Focus.Ring`, `Focus.Ring.Oak` | The focus ring on vellum, and on oak (the rail, the toast, the guide's seal chain) |
| `Gules`, `Or`, `Vellum`, `Oak`, `Ink` and their `.Light`/`.Dark`/`.Deep`/`.Soft` shades, `Oak.Tab` | The tokens of `Tokens.dc.html` |
| `Leather.Text`, `Leather.Muted` | Text on oak and leather |
| `Livery.Primary`, `Livery.Secondary`, `Livery.Dark` | The livery: active tab, crest, drop-cap letter, slider, knotwork band |
| `Wax.Gules`, `Wax.Azure`, `Wax.Vert`, `Wax.Or`, `Wax.Purpure`, `Wax.Sable`, `Wax.Plain`, `Wax.Livery`, `Wax.Text`, `Wax.Plain.Text` | Seal wax (radial brushes) and the glyph on it (`Wax.Plain.Text`, ink, on the pale plain wax) |
| `Ornament.Gold`, `Ornament.Pip`, `Ornament.Edge`, `Ornament.Filigree`, `Ornament.Rule` | Ornament strokes and fills |
| `Knot.Horizontal`, `Knot.Vertical`, `Damask.Oak` | The knotwork band tiles and the damask wall (tiled `DrawingBrush`es) |
| `Vellum.Gradient`, `Oak.Gradient`, `Gold.Gradient`, `DropCap.Gradient`, `Boss.Gradient`, `Rod.Gradient`, `Plaque.Gradient`, `Crest.Field`, `Candle.Flame`, `Candle.Wax`, `Row.Rule` | Charter, rail, wordmark, drop caps, bosses, scroll rods, plaques, crest, candles, ruled rows |
| `Header.Gradient`, `Accent.Gradient`, `Rule.Gradient` | Older surfaces kept for compatibility: oak header, the update banner, thin rules |
| `Overlay.HitTest`, `Overlay.Dim` | The region selector's near-invisible hit-test surface and dimmed screen. Functional, not decorative: `#01000000` and `#99000000` in every palette, including high contrast |

The palettes are written together by `tools/themes/make_palettes.py`, so a new key reaches all seven at once; in
`Palette.highcontrast.xaml` it becomes a `SystemColors` value.

### Contrast rules

`PaletteContrastTests` (Core unit tests) read every `Palette.*.xaml` except `highcontrast` and fail the build unless:

- `Text.Primary`, `Text.Secondary`, `Text.Muted`, `Danger`, `Success` and `Accent` reach **4.5:1** against `Bg.Window`,
  `Bg.Panel`, `Bg.PanelAlt`, `Bg.Input` and `Vellum.Deep` (the darkest stop of the charter);
- `Focus.Ring` reaches **3:1** against the same four backgrounds, and `Focus.Ring.Oak` against `Oak`, `Oak.Deep` and
  `Oak.Tab`;
- `Leather.Text` and `Leather.Muted` reach **4.5:1** on the three oak shades;
- `Wax.Text` reaches **4.5:1** on every lettered wax, `Wax.Plain.Text` on the plain wax, and `Text.OnAccent` on `Accent`, `Accent.Bright`, `Livery.Primary`
  and `Livery.Dark`;
- `Overlay.Text` and `Overlay.Sender` reach **4.5:1** against an opaque `Overlay.Bg`.

With **Keep text readable** on, the overlay card alpha is never lower than
`ContrastMath.MinimumBackgroundAlpha(Overlay.Text, Overlay.Bg, 4.5)` over white, grey and black backdrops.

No window, page or control XAML may use a literal colour (`#…`); `tools/typecheck/check_a11y.py` enforces this,
along with a focus visual on every focusable style and an accessible name on every interactive control.

## The ornament kit

`Themes/Ornaments.xaml` (merged by `Controls.xaml`) holds the fonts, the geometry of the reference SVGs and these
styles:

| Style | Target | What it draws |
| --- | --- | --- |
| `CharterFrame` | `ContentControl` | Vellum with a knotwork band, gold edge lines, corner bosses and filigree corners; `Padding` is the inner margin |
| `PlateFrame` | `ContentControl` | A lighter vellum plate with a gold double edge (lookup, region picker) |
| `Fleuron` | `Control` | The divider rule |
| `DropCap` | `ContentControl` | A gold box with a blackletter letter in the livery colour; `Content` is the letter |
| `WaxSeal` | `ContentControl` | A round seal; `Background` is the wax (default `Wax.Livery`), `Content` the glyph |
| `ScrollRods` | `Control` | The rolled end of a dispatch |
| `Crest` | `Control` | The livery's shield |
| `Candle` | `ContentControl` | A provider's candle; `Tag` is `Lit`, `Guttering` or `Snuffed` (Core `ProviderCandle`); it flickers at Full motion |
| `RowLabel` | `ContentControl` | A settings row's label with its drop-cap square |

The nine marginal drawings are `Figure.<PageId>.Ink` and `Figure.<PageId>.Red` geometries; `MainWindow` splits them
into one path per stroke and draws them stroke by stroke on a page change. The overlay seal's glyph and wax come from Core `LanguageSeal.For(code)`: 中 on gules for Chinese, 한 on
azure for Korean, 日 on or for Japanese, Р on vert for Russian, the first letter of the language's English name on
purpure for the rest.

## Motion

Spec 09b brings the reference designs' motion to WPF. Everything that moves goes through one helper,
`Services/Motion.cs`, which reads the **Motion** setting (Livery › Accessibility › Motion):

| Setting | What moves |
| --- | --- |
| Follow Windows (default) | Full, unless "Show animations in Windows" is off, then Fades only |
| Full | Everything: hover and press responses, page changes, dispatches, the desk, candles, the torches, the Tale |
| Fades only | Fades only; nothing slides, scales, draws or loops |
| Off | Nothing: every change shows its end state at once |

Durations are the named constants of Core `Motion`: `Quick` 150 ms (hover, press, focus), `Snap` 320 ms (switches,
stamps, page turns, the desk folding), `Unroll` 450 ms (charters and dispatches) and `Ink` 1.4 s (writing and
drawing). An animation may change only `Opacity`, a render-transform part, a clip's transform, an opacity mask's
stops or `StrokeDashOffset`; nothing animates layout (typecheck R16). Loops (candle flames, the active tab's sway, the
guide's current seal, the torches) run at Full only and only while their element is visible.

In XAML the helper is the `s:Motion` attached properties (`xmlns:s="clr-namespace:Envoy.App.Services"`):

| Property | On | Does |
| --- | --- | --- |
| `s:Motion.React` | a template part | `Lift` (plaques, ribbons), `Wax` (wax buttons), `Tab` (banner tabs), `Card` (livery cards); the helper owns the part's render transform, so no trigger may set it |
| `s:Motion.HoverGlow`, `s:Motion.FocusGlow` | a template part | Fades the part in while the control is under the mouse, or has keyboard focus |
| `s:Motion.Switch` | a seal switch's seal | Slides that many pixels and stamps when checked |
| `s:Motion.Entrance` (+ `Delay`, `OnLoad`) | any element | `Fade`, `Rise`, `Unfurl`, `UnrollX`, `Stamp`, `Ink` or `Draw`, played on load or by `Motion.Replay` |
| `s:Motion.StampWhen`, `s:Motion.PulseWhen` | a seal | Stamps when the binding turns true; pulses while it is true |
| `s:Motion.Candle` (+ `CandlePart`) | the candle template | Flickers, gutters, snuffs with smoke and relights with a pop (Core `CandleMotion`) |

## Shared styles

`Themes/Controls.xaml` holds keyed styles you can use in new windows:

- Text: `Text.H1`, `Text.H2` (a rubric; also `Text.Rubric`), `Text.Label`, `Text.Body`, `Text.Caption`, `Text.Aside`,
  `Text.Icon`
- Containers: `OrnateFrame` (a section of a page), `Flourish` (the fleuron), `Plaque` (a `Border` for values),
  `SettingRow` (a `Grid` row on ruled vellum), `Callout` (a dispatch card for a Core `PageHint`)
- Buttons: the default style (a gold-edged plaque), `Button.Primary` (a wax-seal pill), `Button.Ghost`,
  `Button.Chip` (a phrase ribbon), `Button.Chrome` (caption buttons on oak)
- Switches: the implicit `CheckBox` and `Switch.Seal` for a `ToggleButton`: plain wax off, the livery's stamped seal on
- Navigation: `NavItem` (a swallowtail banner tab: `Content` is the playful name, `Tag` the plain one), `ThemeCard`
- Focus: `FocusVisual.Ring` and, on oak, `FocusVisual.Ring.Oak` (set one as `FocusVisualStyle` on any new focusable
  style; never `{x:Null}`)
- Implicit styles for `TextBox`, `PasswordBox`, `ComboBox`, `Slider` (a gules track with a wax thumb), `ScrollBar`
  (thin gold), `ToolTip` and `ContextMenu`

## Adding a livery

1. Add its colours to the `LIVERIES` table in `tools/themes/make_palettes.py` and run it; it rewrites all seven
   palettes.
2. Add a `Livery` to Core `ThemeCatalog.Liveries` (name, a description without dashes, the two tinctures).
3. Optionally make it a game's livery in `GameCatalog` (`ThemeId`).
4. Run the Core tests and `tools/typecheck/typecheck.sh`, or let CI run the smoke test, which builds every window
   under every palette.

`PaletteContrastTests` and `ThemeManagerTests` cover a new livery automatically; fix any colour they report.
