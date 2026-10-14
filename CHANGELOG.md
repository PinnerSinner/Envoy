# Changelog

All notable changes to Envoy. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning: [SemVer](https://semver.org/).

## [Unreleased]

## [0.1.0] — first release

### Reading chat
- Screen OCR with the built-in Windows engine. It runs several languages at once and keeps the best reading per line.
- Clean-up of CJK spacing; fuzzy de-duplication so each message is translated once despite OCR jitter.
- Parsing of channel and player names across AoE chat formats (`[All] Name: msg`, `Name (Team): msg`, timestamps,
  full-width colons).
- A chat area remembered per game; auto-detection of AoE I/II/III/IV (DE and older) and AoM: Retold.
- Live view of the captured image and the OCR text for tuning.
- Skips OCR when the chat area hasn't changed; can idle when no game is running.

### Translating
- Free providers with no account: Google Translate, with Microsoft Translator as the fallback.
- Bring-your-own-key providers: DeepL, Claude, Amazon Translate, LibreTranslate.
- A fallback chain, a translation cache, and a daily character cap for paid providers.
- An offline slang glossary (666, ㅋㅋ, ㅈㅈ, изи, kkkk, www and more).
- Per-player language memory, so replies default to the language of whoever spoke last.
- A mute list and a channel filter (all / team only / everything except team).
- Optional chat transcripts, saved locally.

### Replying
- A hotkey reply box with a live translation preview and back-translation.
- 16 hand-translated quick phrases in 13 languages; user phrasebook overrides.
- Sends via Unicode keystrokes or paste, or copies to the clipboard.

### App
- Five themes inspired by each game, with optional automatic switching.
- A click-through overlay with anchoring, drag-to-place, fade-out and adjustable look.
- Tray icon, global hotkeys, start with Windows, update notifications.
- API keys encrypted with Windows DPAPI.
- A single self-contained `Envoy.exe`; CI builds it, smoke-tests it on Windows and attaches it to releases.
