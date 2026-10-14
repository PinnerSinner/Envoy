<p align="center">
  <img src="src/Envoy.App/Assets/envoy.png" width="96" alt="Envoy shield" />
</p>

<h1 align="center">Envoy</h1>

<p align="center">
  <b>Live chat translation for Age of Empires I–IV and Age of Mythology.</b><br />
  Read what your opponents say. Answer in their language. Never touch the game.
</p>

<p align="center">
  <a href="../../releases/latest"><b>Download Envoy.exe</b></a> ·
  <a href="docs/getting-started.md">Getting started</a> ·
  <a href="docs/translation-providers.md">Providers &amp; cost</a> ·
  <a href="docs/troubleshooting.md">Troubleshooting</a> ·
  <a href="docs/faq.md">FAQ</a>
</p>

---

Matchmaking puts you against players from everywhere: Chinese names in AoE IV ranked, Korean pros in AoE II,
Russian teams in AoM. Envoy reads the in-game chat from your screen, translates it into your language, and shows it
in an overlay styled after the games' own UI. Press a hotkey and type a reply in your language, and Envoy types the
translation into the game for you.

```
 What the game shows                          What Envoy adds
 ─────────────────────────────                ───────────────────────────────────────────
 [All] 龙骑士: 你好，再来一局吗？              中  龙骑士: Hello, another round?
 [All] 전략가: ㅈㅈ                           한  전략가: GG — I give up            (slang, offline)
                                              Ctrl+Shift+T → "gg wp, sure!"
                                                           → 好局，打得漂亮！当然！ → sent to chat
```

## Highlights

- **Works with every Age game.** It reads pixels, not memory, so any game that shows chat on screen works. AoE IV,
  II: DE, III: DE, AoM: Retold and AoE: DE are auto-detected, and each remembers its own chat area.
- **Free out of the box.** Google Translate needs no account, with Microsoft Translator as an automatic fallback.
  DeepL, Claude, Amazon Translate or LibreTranslate can be added with *your own* key. There's a daily spending cap,
  and no Envoy server ever touches your credits.
- **Understands gamers.** Slang like `666`, `ㅋㅋㅋ`, `ㅈㅈ`, `изи` and `kkkk` is explained offline. Each player's
  language is remembered, so in a 2v2 you answer the Korean player in Korean and the Chinese player in Chinese.
- **Replies that land.** You get a live preview and a back-translation ("Means: …") before sending. There are 16
  hand-translated quick phrases in 13 languages, and replies are typed straight into game chat.
- **Looks the part.** Five themes, one per game (Imperial, Parchment, Frontier, Olympian and Classic), with an
  option to switch automatically to match the running game.
- **One file.** A portable, self-contained `Envoy.exe` that lives in the tray, with global hotkeys. It can start with
  Windows and tells you about updates.
- **Private by design.** No telemetry. API keys are encrypted with Windows DPAPI. Optional chat transcripts stay on
  your PC.

## Quick start

1. Download **[Envoy.exe](../../releases/latest)** and run it. It's portable; nothing to install.
2. Set the game to **Borderless window**.
3. Click **Select chat area** and drag a box around the in-game chat.
4. Choose your language. Done. Press **`Ctrl+Shift+T`** in game to reply.

To read Chinese, Korean, Japanese or Russian chat, install that language's free Windows OCR pack. Envoy has an
**Install** button for it; see [OCR languages](docs/ocr-languages.md).

| Hotkey | Action |
| --- | --- |
| `Ctrl+Shift+T` | Reply box |
| `Ctrl+Shift+O` | Show / hide overlay |
| `Ctrl+Shift+P` | Pause / resume reading |
| `Ctrl+Shift+R` | Select chat area |
| `Ctrl+Shift+E` | Move / resize overlay |

## Is it safe for ranked?

Envoy never opens the game's process, reads its memory, injects code or modifies files. It **reads the screen**
like OBS or Discord and **types** like an on-screen keyboard. It doesn't automate gameplay. More in the
[FAQ](docs/faq.md#can-i-get-banned-for-using-envoy).

## Documentation

| For players | For developers |
| --- | --- |
| [Getting started](docs/getting-started.md) | [Architecture](docs/architecture.md) |
| [Translation providers & cost](docs/translation-providers.md) | [Building from source](docs/building.md) |
| [OCR languages](docs/ocr-languages.md) | [Theming](docs/theming.md) |
| [Phrasebook](docs/phrasebook.md) | [Releasing](docs/releasing.md) |
| [Troubleshooting](docs/troubleshooting.md) | [Contributing](CONTRIBUTING.md) |
| [FAQ](docs/faq.md) | [Changelog](CHANGELOG.md) |

## Roadmap

- [ ] Offline translation model (no internet needed at all)
- [ ] Voice chat transcription and translation
- [ ] Per-game chat colour presets for the OCR enhancement
- [ ] Text-to-speech for incoming messages
- [ ] Signed releases and an optional installer with auto-update
- [ ] More phrasebook languages, with community review by native speakers

Ideas and translations are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Building

```bash
dotnet test                                          # any OS
tools/typecheck/typecheck.sh                         # type-check the Windows app from Linux/macOS
dotnet publish src/Envoy.App -c Release -o publish   # Windows → publish/Envoy.exe
```

Every push builds and smoke-tests `Envoy.exe` on Windows. Pushing a `v*` tag publishes a release. See
[docs/building.md](docs/building.md).

---

<sub>Envoy is an unofficial fan project, not affiliated with or endorsed by Microsoft, Xbox Game Studios, World's Edge,
Relic Entertainment or Forgotten Empires. <i>Age of Empires</i> and <i>Age of Mythology</i> are trademarks of
Microsoft. MIT licensed. See <a href="THIRD-PARTY-NOTICES.md">third-party notices</a>.</sub>
