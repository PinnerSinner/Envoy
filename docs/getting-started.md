# Getting started

This takes about two minutes and you only do it once.

## 1. Download and run

Download **`Envoy-win-Setup.exe`** from the latest [release](../../../releases/latest) and run it. It installs Envoy
for your Windows account only (no admin rights, no .NET to install), adds it to the Start menu and keeps it up to
date. Prefer no install? Download the portable `Envoy.exe` from the same page instead: it's a single file you can put
anywhere (a `Tools` folder or the desktop) and double-click. [Installing & updates](installing.md) compares the two.

> **Windows SmartScreen** may say it "protected your PC" because the download isn't code-signed yet. Click
> **More info → Run anyway**. The exe is built by GitHub Actions straight from this repository's source; see
> [Releasing](releasing.md) to check a download against its build.

Envoy lives in the system tray (the shield icon by the clock). Closing the main window doesn't quit it. To quit,
right-click the tray icon and choose **Exit**.

## 2. Put the game in borderless mode

In the game's graphics settings, set **Display mode** to **Borderless window** (or Windowed):

| Game | Setting |
| --- | --- |
| Age of Empires IV | Settings → Graphics → Display mode → *Borderless Window* |
| Age of Empires II: DE | Options → Graphics → *Borderless Window* |
| Age of Empires III: DE | Options → Graphics → Display mode → *Borderless* |
| Age of Mythology: Retold | Settings → Graphics → *Borderless Window* |
| Age of Empires: DE | Options → Graphics → *Borderless Window* |

Exclusive fullscreen bypasses the Windows compositor, so other apps can't draw over the game or see it. Borderless
looks and performs the same on modern Windows.

## 3. Mark the chat area

1. Start a game, or open a replay or the lobby, so chat is visible.
2. In Envoy's **War Room**, click **Select chat area** (or press `Ctrl+Shift+R`).
3. Drag a box around where chat messages appear. Leave a little room on the right for long messages.

Each game remembers its own area, so you only do this once per game. To check what Envoy reads, open
**Reading Chat → Live view**.

## 4. Pick your language

In **War Room → Choose your language**, pick the language you read and type. Incoming chat is translated into it
and your replies are written in it.

Optionally, enter your in-game name so your own messages aren't echoed back to you.

## 5. Install OCR languages for your opponents

Windows can only read a script it has OCR data for. Go to **Reading Chat → Languages to recognise**:

- Tick the languages your opponents use. In ranked play that's usually Chinese, Korean, Japanese and Russian.
- If one says *Not installed*, click **Install** (Windows asks for admin), or add it yourself in
  *Settings → Time & language → Language & region → Add a language*. Only the OCR part is needed; you don't have
  to switch your Windows display language.
- Restart Envoy after installing.

See [OCR languages](ocr-languages.md) for details.

## 6. Play

- Foreign chat appears translated in the overlay next to the game's chat. It fades out after 25 seconds by default.
- Press **`Ctrl+Shift+T`** to reply. Type in your language and press **Enter**. Envoy translates the message and
  types it into game chat.
- Press **`Ctrl+Shift+Q`** and drag over any other text (lobby names, map descriptions) to translate it once.
- Turn on **Overlay → Herald** to hear translations read aloud.
- Or click a **quick phrase** (gl hf, gg wp, rematch?…). These are human translations.

Next: [choose a translation provider](translation-providers.md) if you want better quality than the free default, or
[troubleshooting](troubleshooting.md) if something doesn't work.
