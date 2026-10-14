# Troubleshooting

Envoy writes unexpected errors to `%APPDATA%\Envoy\error.log`. Attach it when you
[report a bug](../../../issues/new/choose).

## Nothing is being translated

1. **Is it reading?** Check the status in the sidebar or the War Room:
   - *Chat area not set*: click **Select chat area**.
   - *Waiting for a game to start*: Envoy only reads while an Age game is running. Turn this off under
     **Reading Chat → Fine tuning** if your game isn't detected, or pick the game manually under **Reading Chat → Game**.
   - *Paused*: press `Ctrl+Shift+P` or click **Pause / resume reading**.
2. **Does it see the text?** Open **Reading Chat → Live view**.
   - **Black image:** the game is in exclusive fullscreen. Switch it to *Borderless window*.
   - **Text visible but nothing read:** install the OCR language (see [OCR languages](ocr-languages.md)), or raise
     *Enlarge before reading*.
3. **Is translation failing?** Go to **Translation → Try it**. If it shows an error, follow it. Common causes are a
   wrong key, a used-up quota or no internet.

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

- Raise **Reading Chat → Check for new messages every** to 1,000–1,500 ms.
- Turn on **Only read chat while an Age game is running**.
- Untick OCR languages you don't need.

Envoy skips OCR entirely when the chat area hasn't changed since the last check, so a quiet chat costs almost nothing.

## Resetting everything

Quit Envoy and delete `%APPDATA%\Envoy\settings.json`. API keys are stored there too, so you'll need to re-enter them.
