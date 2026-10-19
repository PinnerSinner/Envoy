# The Herald's Writ in motion: animation, reactivity and the Tale

Status: Implemented (step 13, after 09a). Revised against `00-integration.md` (binding). Differences from the text
below are under Deviations.

Area id: `09b-herald-motion` · Requirement prefix: `MOT`.

## Context

09a gives every window the Herald's Writ look. The reference designs in `docs/design/herald/` also move: banners
sway, charters unroll, dispatches unfurl and seals stamp, ink writes itself, candles flicker, and `Explainer.dc.html`
is a one-minute animated tale that teaches the app. This spec brings that motion to WPF, with a hard rule that motion
never costs accessibility or game performance.

## Goals / Non-goals

**Goals**

- Every state change the user causes gets an immediate, crafted response (hover, press, toggle, page change, send).
- The Tale window explains what Envoy does and how to use it in about a minute.
- Motion is optional and respects Windows' "Show animations" setting.

**Non-goals**

- Video playback. The Tale is drawn and animated in XAML, not a video file.
- 3D. A flat torchlit wall and a 2D seal are enough (COULD below for a `Viewport3D` seal).

## Requirements

### Policy

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-MOT-1 | MUST | Core `MotionPolicy` (pure, tested) decides `Full`, `Reduced` or `Off` from a new setting `Appearance.Motion` (`Auto`, `Full`, `Reduced`, `Off`; default `Auto`) and Windows' client-area animation flag (`SystemParameters.ClientAreaAnimation` false means `Reduced` under `Auto`). Durations and easings are named constants in Core `Motion` (`Quick` 150 ms, `Snap` 320 ms, `Unroll` 450 ms, `Ink` 1.4 s). Every animation in the app goes through one helper (`Services/Motion.cs`) that returns the end state at once under `Off` and keeps only fades under `Reduced`. The Livery page gets the setting with plain labels. |
| REQ-MOT-2 | MUST | Animations change only `Opacity`, `RenderTransform`, `Clip`, `OpacityMask` or `StrokeDashOffset`; nothing animates layout. The overlay never takes focus and runs no infinite animation, so a game in front pays nothing when no message arrives. |

### Reactivity

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-MOT-3 | MUST | Controls react: buttons lift 2 px on hover and press down on click; wax buttons tilt and brighten; seal switches slide and stamp (scale 1.8 to 1 with a slight overshoot); banner tabs slide out 8 px on hover; the active tab drops and sways gently (Full only); plaques glow gold on focus. |
| REQ-MOT-4 | MUST | Page change unrolls the charter from the top (`Unroll`, back ease) and redraws the marginal figure stroke by stroke; the drop cap and the lede fade in on the same beat. |
| REQ-MOT-5 | MUST | Overlay: each dispatch unfurls from the top rod, its seal stamps in 0.3 s later, and it fades out like ink at the end of its linger time. Both `FullItem` and `CompactItem`. |
| REQ-MOT-6 | MUST | Reply desk: the translation writes itself left to right (`OpacityMask` sweep, `Ink`) each time it changes; "Seal & send" presses, and on a successful send the desk folds toward the chat and closes; the candle on the desk flickers. |
| REQ-MOT-7 | MUST | Provider health candles on the Interpreters page: lit flame flickers, a resting provider's flame gutters, a refused key's candle is snuffed with rising smoke; "Relight" relights it with a pop. Setup guide: completed seals stamp in, the current seal pulses, and step pages turn with a short slide. |
| REQ-MOT-8 | SHOULD | War Room wall: the oak and damask background is lit by two torches, Full only, paused while the window is not visible. The flame must look real: a particle flame (12 to 16 soft blobs that rise, shrink and cool from white-gold to deep red, merged by blur, each with its own duration so the pattern never visibly loops), a little smoke and the odd spark, and light that wavers through two or three slow overlapping oscillations. No uniform scale pulsing and no per-frame random jitter. The reference is `docs/media/readme/flame.py`. |

### The Tale

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-MOT-9 | MUST | `Windows/TaleWindow.xaml` plays the six chapters of `Explainer.dc.html`: 1 foreign chat on the field, 2 Envoy reads only the screen, 3 a dispatch unfurls with the translation, 4 Ctrl ⇧ T and the desk writing Korean, 5 the reply is typed into game chat, 6 free to use, a livery for every Age, private by default, with "Set up Envoy" and "Watch again". Drawings draw themselves (`StrokeDashOffset`); captions are a heading plus one plain sentence from Core `TaleText` (no em dashes). Controls: play/pause, six chapter seals on a progress rope, Skip. Keys: Space, Left, Right, Esc. Captions are an automation live region. |
| REQ-MOT-10 | MUST | The Tale opens once after the setup guide finishes (offered, not forced: "Watch a one-minute tour" on the last step), and any time from Chronicle / About ("Play the tale") and the tray menu. Under `Reduced` or `Off` it shows each chapter's finished frame and moves on Next only. |
| REQ-MOT-11 | COULD | A `Viewport3D` wax seal on the Livery page that turns slowly and takes the livery's colour. |

### Verification

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-MOT-13 | MUST | Remove the slogan "never touch(es) the game" (and "not the game") from every user-facing string and doc, and add `never touch` to R15's banned phrases. Safety is explained plainly where it is asked about (FAQ, privacy, the Safe for ranked play section): Envoy reads the screen like OBS and presses keys like a keyboard. |
| REQ-MOT-12 | MUST | Smoke: every animated window renders with `Motion` `Off` and `Full` under every palette, with zero binding errors; the Tale is stepped through all six chapters under each palette; `MotionPolicyTests` and `TaleTextTests` (six chapters, no U+2014 or U+2013, each sentence under 120 characters). `TaleWindow` joins `SmokeTest.cs` in the per-palette phase. |

## Acceptance

| Req | Check |
| --- | --- |
| MOT-1 | `MotionPolicyTests`; `SettingsRedactionTests` (no marker needed); smoke toggles the setting |
| MOT-2 | Typecheck rule R16: no `Storyboard.TargetProperty` other than the allowed ones in `src/Envoy.App/**/*.xaml`; code review of `Services/Motion.cs` |
| MOT-3 to 8 | Smoke sections `motion({palette})`; screenshots taken after animations settle |
| MOT-9, 10 | Smoke section `tale({palette})`; `TaleTextTests`; `check_a11y.py` covers the window |
| MOT-12 | Windows CI green |

## Risks

- Overlay cost in game: keep each dispatch animation under 1 s, freeze brushes, no blur effects.
- Accessibility: `Reduced` must remove movement, not just shorten it. Screen readers get the Tale's captions.
- Concurrent pushes: if a push is rejected, `git pull --rebase origin main` and push again; never force-push.

## Deviations

Owners of the concepts this spec adds: Core `Motion`, `MotionPolicy` and `MotionText` (levels, durations, labels),
`CandleMotion` (candle changes), `TaleText` and `TalePlayer` (the Tale); `Services/Motion.cs` (every animation and the
`s:Motion` attached properties); `AppHost.ApplyMotion` (the level); `Controls/Torch.cs` (the torches).

1. **What Off and Reduced mean for state changes** (REQ-MOT-1). A change of state still lands: under Off every call shows
   its end state at once, and under Reduced everything but fades does. So a checked switch's seal, the active tab's
   offset and a hovered button's lift still take their place (as in 09a's static look), without moving there. Loops
   (flames, sway, pulse, torches) run at Full only and only while their element is visible and its window is not
   minimised (a minimised WPF window stays `IsVisible`, so the loop also follows the window's `StateChanged`). The level is process-wide,
   like the palette, and is set at ApplySettings position 10 (09b's slot), in `Start` and when Windows' "Show
   animations" changes (App's `SystemParameters` listener).
2. **The setting** (REQ-MOT-1) is a Motion row in the Livery page's Accessibility frame, so the contract §8.6 order of
   the page is unchanged. Its labels come from Core `MotionText`: Follow Windows (Auto), Full, Fades only (Reduced), Off.
3. **Reactions** (REQ-MOT-3). A press moves a plaque 1 px down (half the lift). "The active tab drops" is the 14 px it
   already stood out in 09a, reached with a back ease, plus a sway of under a degree. "Plaques glow gold on focus" is
   a gold ring just outside the focus ring of buttons, text boxes, password boxes and drop-downs: a ring, not a fill,
   so no text contrast changes. Template triggers no longer set any `RenderTransform`; the helper owns those parts.
4. **Page change** (REQ-MOT-4): the unroll is a clip opening downwards with the page settling from 96 % height on a back
   ease. The marginal drawing is now one path per stroke (`MainWindow.Figure`, replacing `FigureInk` and `FigureRed`)
   and is redrawn only while the marginal column shows (1180 px and wider).
5. **Overlay** (REQ-MOT-5): the compact ribbon unfurls and stamps like the full dispatch. "Fades out like ink" is the
   existing opacity fade over the last 2 s, now from Core `Motion.LingerOpacity`; with motion Off a dispatch stays
   solid until it goes.
6. **Reply desk** (REQ-MOT-6): the desk folds as the reply is sent, before Envoy types it, because the desk must be gone
   for the keystrokes to reach the game; this delays typing by 320 ms at Full (150 ms with fades only). Copy and the
   clipboard send mode close without folding. One send or copy runs at a time: Enter, "Seal & send" and Copy do nothing
   until it ends, the reply hotkey does nothing while the desk folds, and the wait for the fold is capped at 1 s. A fold
   that is replaced or undone still ends its task, so a reply is never typed twice or held back. The desk's candle is a small `Candle` in its heading.
7. **Candles** (REQ-MOT-7): the button stays "Retry now" (09a Deviation 7); the relight pop plays whenever a candle
   goes from snuffed to lit or guttering. The pop and the gutter briefly take over the flame's scale, and the flicker
   starts again when they end. A guide step turns with a 24 px slide and a fade, from the right going
   forward and from the left going back.
8. **Torches** (REQ-MOT-8, SHOULD, done): `TorchFlame` has 14 blobs, 4 smoke puffs and 3 sparks with periods from a
   fixed seed, merged by a blur, and `TorchLight` wavers through three overlapping oscillations. The light sits under
   the rail and the charter, so it never lights text; the torches hide below Full motion and in high contrast. Blobs
   start within the first second instead of the SVG's negative begin times, and the colours are existing palette
   keys (`Vellum.Light`, `Candle.Flame`, `Gules`, `Ink.Soft`, `Or.Light`), so no palette key was added.
9. **The Tale** (REQ-MOT-9, 10): chapters last 10 s (a 60 s tour; the reference's default is 8 s). The title uses
   `Font.Title`, since `Font.Wordmark` is for the wordmark and drop caps only. The pictures are simplified XAML versions
   of the reference drawings, and chapter IV's key plaques spell the user's own reply hotkey. Pause finishes the
   chapter's picture at once instead of freezing it mid-stroke. Space on a focused button presses that button, and in
   a still tale Space acts as Next. Captions are plain English (the reference's "thy" is gone, and chapter II no
   longer says "it never touches the game"). "Offered, not forced": the guide's last step has a "Watch a one-minute
   tour" button that finishes the guide and opens the Tale; nothing records whether it was watched. The tray item is
   "Play the tale…".
   The caption sentence is the live region and has no other automation name, so Narrator reads the sentence itself.
   Chapter IV's "Reply to 전략가, in Korean" is drawn as a drop cap and the rest of the line, and the line's automation
   name is the whole phrase. When the motion level drops below Full while the Tale is open it becomes a still tale at
   once (Play hides, the hint shows); back at Full, Play returns and the tale stays paused until pressed. A minimised
   Tale stops its clock and goes on when restored.
10. **REQ-MOT-11** (COULD, deferred): no `Viewport3D` seal. The 2D seals stay; a 3D seal adds GPU cost to the settings
    window for little gain.
11. **Verification** (REQ-MOT-12): the smoke sections are `motion({palette})` (Off and Full: page change, candles,
    torches, overlay dispatches in both layouts, the desk's ink and fold, the guide's page turn, which must leave its
    step; the marginal column is forced on so the drawing redraws; after the animations settle every part is back at
    rest and both overlay layouts are sampled for movement, while at Full the relit candle and the torches must still
    move and at Off nothing may) and `tale({palette})` (all six chapters with Right under Off
    and Full, each caption's automation name equal to its sentence, Left, Space, Esc, the a11y audit on the first palette, `OpenTale` single instance), plus a
    `{palette}-tale.png` screenshot. The rest of the smoke run uses Motion Off, so screenshots show finished frames.
    R16 also keeps `BeginAnimation`, `BeginStoryboard` and `new Storyboard` inside `Services/Motion.cs`, and the helper
    refuses any property outside its allowed list at run time. The motion checks were type-checked on Linux; they run
    for the first time in Windows CI. A new manual step, M-MOT-1, covers what only a person can judge.
12. **REQ-MOT-13**: besides the settings window footer, the Scouts page lede, `docs/architecture.md`,
    `docs/installing.md` (a data-folder sentence the new phrase check also caught) and `CONTRIBUTING.md` were reworded;
    the FAQ adds "Envoy reads the screen like OBS and presses keys like a keyboard". R15 bans the prefix `never touch`
    in its files only; code comments elsewhere are unaffected.
13. **MUST count**: this spec has 11 MUST rows (REQ-MOT-13 was added after the index counted 10); the index row now
    says 11, and the index total now sums every row, 09a and 09b included.
14. **The setting on disk** (REQ-MOT-1): `Appearance.Motion` is saved by name, and a name this build does not know
    (one a newer Envoy wrote) or any other value loads as Auto through `MotionSettingJsonConverter` instead of failing
    the whole settings file.
