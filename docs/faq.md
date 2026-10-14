# FAQ

### Can I get banned for using Envoy?

Envoy is built so there is nothing for anti-cheat to object to:

- It never opens the game's process, reads or writes its memory, injects code or edits game files.
- It **reads pixels** from the screen with the same Windows API that screenshot tools, OBS and Discord use.
- It **types** through `SendInput`, the API that on-screen keyboards and input method editors use.
- It runs as a normal user and asks for no special privileges.

It doesn't automate gameplay. It only reads and writes chat text, which you could do yourself with a phone
translator. That said, it's an unofficial tool, and no studio has reviewed or endorsed it.

### Does it work with every Age game?

It works with any game that shows chat as text on screen in windowed or borderless mode. It auto-detects Age of
Empires IV, II: DE, III: DE, Age of Mythology: Retold and Age of Empires: DE, plus the older AoE II HD and AoE III
releases. Other games work too if you pick **Detect automatically** and mark the chat area. Envoy just won't switch
theme or remember a separate region for them.

### Does it cost anything?

No. The default providers are free and need no account. Paid providers are optional and billed to your own
account. See [Translation providers](translation-providers.md).

### What data leaves my PC?

Only the text of chat messages, sent to the translation provider you choose. If update checks are on, the
start-up check also contacts GitHub (`api.github.com`), sending nothing but the request itself. Envoy collects no
telemetry. Transcripts, if you enable them, stay on your PC in `%APPDATA%\Envoy\logs`.

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
