# The Herald's Writ in motion: animation, reactivity and the Tale

Status: Proposed. Lands as step 13, after 09a. Revised against `00-integration.md` (binding).

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

(None yet.)
