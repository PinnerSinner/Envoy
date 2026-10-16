# Troubleshooting

## Reporting a bug

Open **About › Troubleshooting** and click **Copy diagnostics**, then paste it into your
[bug report](../../../issues/new/choose). The report lists Envoy, Windows and monitor details, the detected game, OCR
languages, which providers have a key set, hotkeys that failed and the last 50 log lines. It never contains your API
keys, chat, player names or where the chat area is on screen; see [Privacy](privacy.md). **Export diagnostics…**
saves the same report with the full diagnostic log as a `.zip` you can attach instead.

When something goes wrong, Envoy shows a small pop-up in the bottom-right corner instead of a dialog, so the game
keeps focus. **Details** opens Troubleshooting. Turn the pop-ups off with **Show a pop-up when something goes wrong**;
problems are still written to the log (**Open log folder**). **Detailed logging** adds more entries if a developer
asks for them.

## Safe mode

If Envoy misbehaves at start-up, start it in safe mode: hold **Shift** while you start Envoy (keep holding until the
window appears), run `Envoy.exe --safe-mode`, or click **Restart in safe mode** under About › Troubleshooting. Envoy
also starts in safe mode by itself after two starts in a row that never finished.

In safe mode the overlay, the hotkeys and automatic chat reading are off; you can still change and save every setting
and start reading chat by hand. A banner at the top of the window says why, and **Restart normally** leaves safe mode.

## "Your settings file was damaged"

Envoy keeps a copy of the last settings that loaded correctly (`settings.json.bak`). If `settings.json` is damaged
(for example by a power cut while saving), Envoy loads that copy, keeps the damaged file as `settings.json.broken`
and tells you with a pop-up. Only if both are unusable does it start with default settings.

## "Envoy closed unexpectedly last time"

Envoy noticed that the previous session didn't end normally (a crash, or Envoy was ended from Task Manager). If it
happens again, copy diagnostics into a bug report; the report includes the last crash.

## Nothing is being translated

1. **Is it reading?** Check the status in the sidebar or the War Room:
   - *Chat area not set*: click **Select chat area**.
   - *Waiting for a game to start*: Envoy only reads while an Age game is running. Turn this off under
     **Reading Chat → Fine tuning** if your game isn't detected, or pick the game manually under **Reading Chat → Game**.
   - *Chat reading is off*: press `Ctrl+Shift+P` or click **Pause / resume reading**.
   - *Paused while the game is minimised.*: reading resumes by itself within a second of restoring the game.
   - *Paused because another window covers the chat area.*: move the other window (a browser, Discord…) off the
     chat. If nothing visible covers it, an invisible window such as a recorder or a vendor overlay may; untick
     **Reading Chat → Fine tuning → Pause while another window covers the chat area**.
   - *Paused while the game isn't the active window.*: you turned on **Pause when the game isn't the active window**;
     click the game, or turn the option off.
   - *The chat area is off-screen.*: a monitor was unplugged or rearranged. Select the chat area again.
   - *Text recognition took too long* or *Reading the chat area failed*: Envoy keeps retrying and slows down while
     it fails. *Envoy can't read the screen right now* is normal while Windows is locked or a UAC prompt is open.
   - *Chat reading stopped responding*: turn chat reading off and on (`Ctrl+Shift+P` twice). If it keeps happening,
     **Copy diagnostics** and report it.
2. **Does it see the text?** Open **Reading Chat → Live view**. Its header shows how reading is going and the
   capture and OCR times, skip rate and current check interval.
   - **Black image**, or *The chat area is blank…* under the status: the game is in exclusive fullscreen, which can't
     be read. Switch it to *Borderless window* (or *Windowed*), or select the chat area again if it really is empty.
   - **Text visible but nothing read:** install the OCR language (see [OCR languages](ocr-languages.md)), or raise
     *Enlarge before reading*.
3. **Is translation failing?** Go to **Translation → Try it**. If it shows an error, follow it. Common causes are a
   wrong key, a used-up quota or no internet. The **Provider health** card on the same page shows what each provider
   is doing; after fixing the cause, press **Retry now** on its row (see
   [When a provider fails](translation-providers.md#when-a-provider-fails)).

## The chat area is in the wrong place

When a game is detected while you select the chat area, Envoy stores it relative to the game window, and the
Reading Chat page says it *follows the game window* (naming the game). Moving the game window then moves the chat
area with it. If the game's size changed since you chose the area, Envoy scales it and shows *The game's size changed
since you chose the chat area…*: select it again if the Live view looks wrong. An area chosen while no game was
running stays at a *fixed screen position*.

## The overlay doesn't show

- Make sure **Overlay → Show the overlay** is on, or press `Ctrl+Shift+O`.
- Click **Show sample messages** to see where the overlay is. If it's off-screen, set **Position → Above the chat area**.
- Exclusive fullscreen hides every overlay; use borderless.

## Replies don't arrive in game chat

- **Check the "open chat" key.** Envoy presses **Enter** to open chat before typing; check
  **Replying → Key that opens chat in game** matches your game's keybinding.
- **Characters are missing or garbled:** set **Replying → Typing method** to *Paste with Ctrl+V*.
- **Nothing is typed:** if the game runs as administrator, Windows blocks keystrokes from non-admin apps. Run Envoy
  as administrator too, or (better) stop running the game as admin.
- **It types before the game has focus:** raise **Replying → Wait for game to focus** to 250–400 ms.
- **"Nothing was typed…" / "Envoy stopped typing…":** Envoy only types into the window you were replying to, and
  stops the moment another window comes to the front. Open the reply box over the game with your hotkey (not from the
  tray while no game is detected), stay in the game while it types, then send again. The reply is always on your
  clipboard in that case.
- As a fallback, choose **When I press Enter → Copy to clipboard only** and paste it yourself.

## Hotkeys don't work

Another app (Discord, GeForce Experience, Steam…) may already own the combination. Envoy lists any hotkeys it
couldn't register under **Hotkeys**. Pick a different combination there.

## Wrong or garbled translations

- Garbage in, garbage out: check **Live view** to see what OCR actually read.
- Free machine translation struggles with slang and sarcasm. Try DeepL or Claude.
- If a message is wrongly treated as being in your language, turn off
  **Translation → Don't translate messages already in my language**.

## High CPU use

- Envoy adapts how often it checks: fast while the chat is moving, every 1.5 s while it's quiet. Raise
  **Reading Chat → Fine tuning → Slowest check** (or turn off **Adapt reading speed to chat activity** and raise
  **Check for new messages every**) to check less often.
- Reading pauses by itself while the game is minimised.
- Turn on **Only read chat while an Age game is running**.
- Untick OCR languages you don't need.

Envoy skips OCR entirely when the chat area hasn't changed since the last check, so a quiet chat costs almost nothing.

## Resetting everything

- **Reset settings…** (About › Troubleshooting) restarts Envoy with default settings. Your API keys are removed too;
  a copy of the old settings is kept as `settings.json.reset-<date>.bak`.
- **Delete all Envoy data…** deletes every file Envoy stores (settings, keys, phrases, logs, transcripts; the list is
  in [Privacy](privacy.md)), removes the "Start with Windows" entry and restarts Envoy as if newly installed.
