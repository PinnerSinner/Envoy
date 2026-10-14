# Architecture

Envoy is two projects. A platform-independent core holds all the logic worth testing. A thin Windows app talks to
the screen, the keyboard and the user.

```
┌──────────────────────── Envoy.App (WPF, Windows only) ────────────────────────┐
│                                                                               │
│  ChatWatcher loop (background)                                                │
│   ScreenCapturer ──► preprocess ──► fingerprint ──(changed?)──► OcrService    │
│   (GDI StretchBlt)   (grey/contrast/  (skip OCR when            (one engine   │
│                       invert)          chat is static)           per lang)    │
│                                                                     │         │
└─────────────────────────────────────────────────────────────────────┼─────────┘
                                                                      ▼
┌──────────────────────── Envoy.Core (net8.0, any OS) ──────────────────────────┐
│  OcrMerger ── pick the best engine per line, strip CJK spaces                 │
│      │                                                                        │
│  ChatPipeline                                                                 │
│   ChatLineParser ─► filters (noise, me, muted, channel) ─► ChatDeduplicator   │
│        ─► SlangGlossary ─► ScriptDetector ─► ITranslator ─► PlayerDirectory   │
│                                                                               │
│  ITranslator stack:  Caching( Fallback[ Budgeted(provider) … , GoogleFree ] ) │
│  OutgoingTranslator: PhraseBook first, then the translator stack              │
└───────────────────────────────┬───────────────────────────────────────────────┘
                                ▼ events (marshalled to the UI thread by AppHost)
┌──────────────────────── Envoy.App ────────────────────────────────────────────┐
│  OverlayWindow (click-through, topmost)   MainWindow (settings / War Room)    │
│  ComposeWindow (hotkey) ──► KeyboardSender (SendInput) ──► game chat          │
└───────────────────────────────────────────────────────────────────────────────┘
```

## Envoy.Core

| Folder | What's there |
| --- | --- |
| `Chat/` | `ChatLineParser` (channel, sender and text from many chat formats), `ChatDeduplicator` (fuzzy, time-windowed "seen it" memory), `TextSimilarity`, `PlayerDirectory` (per-player language memory), `MuteList`, `TranscriptWriter` |
| `Ocr/` | `OcrMerger` combines several engines' readings line by line; `OcrText.Clean` removes the spaces Windows OCR puts between CJK characters |
| `Languages/` | `LanguageCatalog` (codes, names, OCR tags, normalising provider codes), `ScriptDetector` (offline language guess from Unicode blocks) |
| `Translation/` | `ITranslator` and providers (`Providers/`), `FallbackTranslator`, `CachingTranslator`, `BudgetedTranslator` + `UsageTracker`, `TranslatorFactory`, `SlangGlossary` |
| `Pipeline/` | `ChatPipeline` (OCR lines → `IncomingMessage` events), `OutgoingTranslator` (your text → their language) |
| `Phrases/` | `PhraseBook` + embedded `phrases.json` |
| `Settings/` | `AppSettings` (the whole settings model) and `SettingsStore` (JSON, `[Secret]` properties encrypted via `ISecretProtector`) |
| `Updates/` | `UpdateChecker` (GitHub Releases) |

Everything here is covered by `tests/Envoy.Core.Tests` and runs on any OS.

### Message lifecycle

1. The watcher hands `ChatPipeline.ProcessAsync` the lines read in one capture.
2. Each line is parsed. Noise, your own messages, muted players and filtered channels are dropped.
3. `ChatDeduplicator.TryAccept` rejects lines already seen within the last 3 minutes. Matching is fuzzy, so OCR
   jitter like `rushing`/`rushlng` doesn't count as a new line.
4. `Classify`: a whole-message slang hit is answered from the glossary. A non-Latin script that matches your
   language is marked *Untranslated*. Everything else stays *Pending*.
5. `MessageReceived` fires so the overlay shows the line immediately ("translating…").
6. Pending messages are translated concurrently. `MessageUpdated` fires with the result or the error.
7. `PlayerDirectory` records the sender's language. `SuggestedReplyLanguage` uses it to pick the reply language.

## Envoy.App

| Folder / file | What's there |
| --- | --- |
| `AppHost.cs` | Composition root: owns settings, services and history; marshals events to the UI thread; applies settings live |
| `App.xaml.cs` | Start-up, single instance, tray icon, hotkey bindings, window management |
| `Services/` | `ScreenCapturer` (GDI), `OcrService` (Windows.Media.Ocr), `ChatWatcher` (the loop), `HotkeyService` (RegisterHotKey), `KeyboardSender` (SendInput), `GameDetector`, `ThemeManager`, `DpapiProtector`, `StartupRegistration` |
| `Windows/` | `MainWindow` (settings + War Room), `OverlayWindow`, `ComposeWindow`, `RegionSelectorWindow` |
| `Themes/` | `Palette.<theme>.xaml` (colours only) + `Controls.xaml` (all control templates) |
| `SmokeTest.cs` | `Envoy.exe --smoke-test` builds every window under every theme; CI runs it on Windows |

### Design rules

- **Never touch the game process.** Read pixels and send input only. This is a product promise; see the [FAQ](faq.md).
- **Logic goes in Core.** If something can be tested without Windows, it belongs in `Envoy.Core` with a test.
- **Everything themeable is a `DynamicResource`.** Colours never appear in window XAML.
- **Settings apply live.** `MainWindow` debounces any edit, calls `AppHost.ApplySettings()` and auto-saves.
- **Fail soft.** Translation errors show on the message; a broken provider falls through to the next; OCR or capture
  errors show in the status line and never crash the loop.

## Threading

- The capture/OCR loop runs on the thread pool (`Task.Run`). Each tick does capture → OCR → hand-off to the pipeline
  without waiting for translation.
- `ChatPipeline` is thread-safe for concurrent `ProcessAsync` calls: de-duplication and the player directory lock
  internally.
- All UI updates go through `Dispatcher.BeginInvoke` in `AppHost`.
