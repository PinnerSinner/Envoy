# Theming

Every pixel of Envoy's UI (settings, overlay, reply box and region picker) is drawn from one palette dictionary
plus a shared control library. Switching theme swaps the palette at runtime. Every control uses `DynamicResource`,
so the change is instant.

## The five themes

| Id | Name | Inspired by | Feel |
| --- | --- | --- | --- |
| `imperial` | Imperial | Age of Empires IV | Midnight blue, burnished gold |
| `parchment` | Parchment | Age of Empires II | Illuminated vellum, oak, rubric red |
| `frontier` | Frontier | Age of Empires III | Deep teal charts, brass |
| `olympian` | Olympian | Age of Mythology | Aegean blue, bronze, marble |
| `classic` | Classic | Age of Empires | Sandstone and bronze-age earth |

**Match the game** (the default) picks the theme of the running game.

All the decoration is original: gilded rules, diamond studs at frame corners, a diamond slider thumb, and Cinzel
(an open-licence font modelled on Roman inscriptions) for headings. No game assets, logos or fonts are used.

## Palette keys

Each `Themes/Palette.<id>.xaml` defines the same keys. CI and `tools/typecheck/typecheck.sh` fail if they drift
apart.

| Key | Used for |
| --- | --- |
| `Bg.Window`, `Bg.Panel`, `Bg.PanelAlt`, `Bg.Input` | Window, cards, raised surfaces, text inputs |
| `Accent`, `Accent.Bright`, `Accent.Dim` | Gold/brass trim, highlights, subtle rules |
| `Accent.Gradient`, `Header.Gradient`, `Rule.Gradient` | Primary buttons, title bar, flourish dividers |
| `Text.Primary`, `Text.Secondary`, `Text.Muted`, `Text.OnAccent` | Text hierarchy; text on accent fills |
| `Border.Subtle` | Hairlines and input borders |
| `Danger`, `Success` | Errors and confirmations |
| `Overlay.Bg.Color`, `Overlay.Text`, `Overlay.Sender`, `Overlay.Original` | The in-game overlay (its background alpha comes from settings) |

Every brush also has a matching `*.Color` resource.

## Adding a theme

1. Copy `src/Envoy.App/Themes/Palette.imperial.xaml` to `Palette.<your-id>.xaml` and change the colours.
2. Add a `ThemeInfo` to `ThemeManager.Themes` and swatches to `MainViewModel.SwatchesFor`.
3. Optionally make it a game's default in `GameCatalog` (`ThemeId`).
4. Run `tools/typecheck/typecheck.sh` (or let CI run the smoke test, which builds every window under every theme).

Check contrast on the overlay in particular. It's read at a glance over busy game scenery, so keep `Overlay.Text`
near white (or near black on a light theme) with a solid enough background.

## Shared styles

`Themes/Controls.xaml` holds keyed styles you can use in new windows:

- Text: `Text.H1`, `Text.H2`, `Text.Label`, `Text.Body`, `Text.Caption`, `Text.Icon`
- Containers: `OrnateFrame` (a `ContentControl` with double rules and corner studs), `Flourish` (a divider)
- Buttons: the default style, `Button.Primary`, `Button.Ghost`, `Button.Chip`, `Button.Chrome`
- Navigation: `NavItem`, `ThemeCard`
- Implicit styles for `TextBox`, `PasswordBox`, `ComboBox`, `CheckBox` (a toggle switch), `Slider`, `ScrollBar`
  and `ToolTip`
