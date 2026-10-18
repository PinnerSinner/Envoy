# FAQ

### Can I get banned for using Envoy?

Envoy is built so there is nothing for anti-cheat to object to:

- It never opens the game's process, reads or writes its memory, injects code or edits game files.
- It **reads pixels** from the screen with the same Windows API that screenshot tools, OBS and Discord use.
- It **types** through `SendInput`, the API that on-screen keyboards and input method editors use.
- It runs as a normal user and asks for no special privileges.

In short, Envoy reads the screen like OBS and presses keys like a keyboard.

It doesn't automate gameplay. It only reads and writes chat text, which you could do yourself with a phone
translator. That said, it's an unofficial tool, and no studio has reviewed or endorsed it.

### Is Envoy allowed in ranked/anti-cheat games?

Nobody has approved it, so we can't promise that it is. What we can say is what it does and doesn't do:

- **Does:** reads pixels from your screen (like OBS or Discord's screen share), recognises text with Windows' own OCR,
  shows an overlay window on top, and types replies through `SendInput` like an on-screen keyboard, only when you
  press the reply hotkey.
- **Doesn't:** read or write the game's memory, inject code, hook the game, edit game files, run itself with
  administrator rights, or automate anything in the match.

No publisher (Microsoft, World's Edge, Relic Entertainment or Forgotten Empires) has reviewed or endorsed Envoy. We
found no published third-party-tool policy for Age of Empires II: DE, III: DE, IV, Age of Mythology: Retold or Age of
Empires: DE (checked 17 October 2026); their general community rules (the Xbox Community Standards and the World's
Edge Code of Conduct) still apply. If a publisher publishes one, this answer will link to it. Use Envoy at your own
judgement in ranked play.

### Can I turn the animations off?

Yes. Livery › Accessibility › **Motion**: **Fades only** stops everything that moves and keeps gentle fades; **Off**
shows every change at once. The default, **Follow Windows**, uses fades only when "Show animations in Windows" is off
(Windows Settings › Accessibility › Visual effects). The overlay never runs a looping animation, so it costs nothing
while no message arrives.

### Does it work with every Age game?

It works with any game that shows chat as text on screen in windowed or borderless mode. It auto-detects Age of
Empires IV, II: DE, III: DE, Age of Mythology: Retold and Age of Empires: DE, plus the older AoE II HD and AoE III
releases. Other games work too if you pick **Detect automatically** and mark the chat area. Envoy just won't switch
theme or remember a separate region for them.

### Does it cost anything?

No. The default providers are free and need no account. Paid providers are optional and billed to your own
account. See [Translation providers](translation-providers.md).

### What data leaves my PC?

Only the text of chat messages and replies, sent to the translation providers you choose, and only after you confirm
"What leaves your PC" (the setup guide's first step, or **Turn on translation** on the War Room); until then
translation is off. The default provider is Google's unofficial keyless endpoint; see
[Privacy](privacy.md#what-leaves-your-pc). Update checks and update downloads go to GitHub only (`api.github.com` and this project's release downloads), sending nothing but the request and the Envoy
version; untick **Check for updates automatically** (Appearance › Startup & updates) and Envoy makes no update request
unless you press **Check for updates** on About. Envoy collects no telemetry. Transcripts, if you enable them, stay on
your PC in `%APPDATA%\Envoy\logs`. See [Installing & updates](installing.md).

### Does Envoy phone home?

No. There is no telemetry, no account and no Envoy server. The only requests Envoy makes on its own are update checks,
and they go to GitHub only: at start-up (at most once an hour) and every 12 hours, plus the update download on an
installed copy. They can be turned off with **Check for updates automatically**; safe mode never checks by itself.
See [How updates work](installing.md#how-updates-work).

### Why not hook into the game to read chat directly?

That would be more precise, but it's exactly what anti-cheat systems look for. It would also break with every game
patch. Reading the screen is safer and works with every game.

### Why does it need OCR language packs?

Windows' OCR engine recognises one script per language pack. They're free official Windows components; Envoy just
helps you install them.

### Why is the exe so big (~75 MB)?

It carries its own copy of .NET, so you don't have to install anything. A framework-dependent build is about 3 MB
but needs the .NET 8 Desktop Runtime. See [Building](building.md).

### Can it speak (text-to-speech) or listen to voice chat?

Not yet. Voice chat translation is on the [roadmap](../README.md#roadmap).

### Can I add my own phrases?

Yes. Go to **Phrasebook → Edit my phrases**. See [Phrasebook](phrasebook.md).
