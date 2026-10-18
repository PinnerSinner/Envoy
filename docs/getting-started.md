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

## The setup guide

The first time Envoy starts, the **setup guide** opens instead of the main window. It walks through the steps below in
a few minutes: **Language & privacy**, **Game & display**, **Chat area**, **Text recognition**, **Test translation**
and **Hotkeys**. Each step writes the same settings as the main window's pages, and the list on the left shows which
steps are done and which still need attention.

- The first step shows **What leaves your PC**. Translation stays off until you tick "I understand what leaves my PC"
  (see [Privacy](privacy.md#what-leaves-your-pc)); **Next** is enabled once you do.
- **Game & display** says whether the game looks windowed, borderless or likely exclusive fullscreen. Press
  **Check again** after changing the game's display mode.
- **Chat area** hides the guide while you drag a box around the chat, then shows a live view of what Envoy reads.
- **Text recognition** lists your language, the ones you read and the common opponent languages, each Installed or
  Missing, with an **Install** button for missing packs.
- **Skip setup** (or Esc, or the close button) closes the guide; the War Room then shows "Finish setting up Envoy" with
  a button that reopens it where you left off. You can also reopen it any time from the tray (**Setup guide…**), from
  About (**Run setup guide again**), and jump straight to the hotkey list with tray › **Hotkey cheat sheet…**.

The last step also offers **Watch a one-minute tour**: it finishes the guide and plays **the Tale**, six short
illustrated chapters on what Envoy does and how to use it. Play it again any time from Chronicle (About) › **Play the
tale** or the tray (**Play the tale…**). Space plays or pauses, Left and Right move between chapters, Esc closes. With
animations off (Livery › Accessibility › Motion, or Windows' "Show animations" off) each chapter is a still picture
and **Next** moves on.

The guide never opens by itself in safe mode or when Envoy starts with `--minimized`, and existing users who already
marked a chat area are not asked again. The rest of this page describes the same steps by hand.

### Tips in the notification area

Envoy can tell you about a missing text recognition pack, a translation provider that keeps failing and a new Envoy
version with a tip by the clock. Tips wait while the game is in front and appear after you alt-tab out or the game
ends; click one to open the right page. The War Room lists the same notices (except updates, which have their own
banner) with **Don't remind me**. Turn tips off, or only some kinds, under **Appearance → Notifications**.

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
  types it into game chat. Press **Up** in the reply box to bring back a reply you sent earlier (Down returns to what
  you were typing); this history is forgotten when Envoy closes. The number under the box is the length of what will
  be sent. If a game cuts long messages, set **Replying › Warn above** to its limit and the number turns red past it.
- Want a smaller overlay? Tick **Overlay → Compact layout** for one line per message. To copy a message, press
  **`Ctrl+Shift+E`** and click ⧉ next to it, then **Done**.
- Press **`Ctrl+Shift+Q`** and drag over any other text (lobby names, map descriptions) to translate it once.
- Turn on **Overlay → Read aloud** to hear translations read aloud.
- Or click a **quick phrase** (gl hf, gg wp, rematch?…). These are human translations. Phrases translated into the
  language you are replying in come first, with the ones you use most at the front.

Next: [choose a translation provider](translation-providers.md) if you want better quality than the free default, or
[troubleshooting](troubleshooting.md) if something doesn't work.
