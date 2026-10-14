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
| `Phrases/` | `PhraseBook` + embedded `phrases.json`; `PhrasePreview` (the Phrasebook page's rows and starter file) |
| `Hotkeys/` | `HotkeyStatus`, `HotkeyRegistrationResult` (one per action, published as `AppHost.HotkeyResults`), `HotkeyText` |
| `Mvvm/` | `ObservableObject`, `RelayCommand`, `RelayCommand<T>`, `AsyncRelayCommand` (required `onError`; never rethrows) |
| `Text/` | `TextTemplate.Format`, the one formatter for the `*Text` classes (named placeholders such as `{count}`) |
| `Threading/` | `LeaseCounter` (backs the watcher's preview leases) |
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
| `AppHost.cs` | Composition root: owns settings, services and history; marshals events to the UI thread; applies settings live; holds `HotkeyResults` (written only by `App.RegisterHotkeys`) |
| `App.xaml.cs` | Start-up, single instance, tray icon, hotkey bindings, window management; implements `IShell` |
| `IShell.cs` | The app-level actions view models may trigger (select region, open compose, open a URL, install an OCR pack, show an info box…). Each area adds its own block |
| `Services/` | `ScreenCapturer` (GDI), `OcrService` (Windows.Media.Ocr), `ChatWatcher` (the loop), `HotkeyService` (RegisterHotKey), `KeyboardSender` (SendInput), `GameDetector`, `ThemeManager`, `DpapiProtector`, `StartupRegistration`, `DialogService` (the only place that shows a `MessageBox`), `SettingsAutoApplier` (the 400 ms debounce) |
| `Windows/` | `MainWindow` (title bar, sidebar and page hosts), `OverlayWindow`, `ComposeWindow`, `LookupWindow`, `RegionSelectorWindow` |
| `Pages/` | One `UserControl` per settings page (`WarRoomPage`, `TranslationPage`, `ReadingChatPage`, `OverlayPage`, `ReplyingPage`, `PhrasebookPage`, `HotkeysPage`, `AppearancePage`, `AboutPage`) and `PageIds` |
| `ViewModels/` | `MainViewModel` (one instance shared by every page, split into `.Translation`, `.ReadingChat`, `.Phrasebook` and `.Actions` partials), `CapturePreviewViewModel` (the live view), `Option`, `OcrLanguageItem`, `ThemeOption` |
| `Controls/` | `HotkeyBox`, `SecretBox`, converters, `AutoApply.Ignore` |
| `Themes/` | `Palette.<theme>.xaml` (colours only) + `Controls.xaml` (all control templates) |
| `SmokeTest.cs` | `Envoy.exe --smoke-test` builds every window and settings page under every palette and fails on binding errors; CI runs it on Windows |

### Settings window

`MainWindow` creates one `MainViewModel` and sets it as its `DataContext`; the nine pages inherit it and never set
their own. Buttons and links bind to the view model's commands (`Envoy.Core.Mvvm`), and the view model reaches
app-level actions only through `IShell`. Any edit bubbles up to `MainWindow`, which restarts a 400 ms
`SettingsAutoApplier`; the apply runs `AppHost.ApplySettings()` and refreshes the status text. Controls that aren't
settings (the sidebar, the "Try it" box, the live view, the phrase preview language) set `c:AutoApply.Ignore="True"`.

### Live view and preview leases

`ChatWatcher.BeginPreview()` returns a lease; while at least one is held (`IsPreviewing`), every capture is published
to `FrameCaptured` even when nothing changed. `CapturePreviewViewModel` holds a lease exactly while its `IsActive` is
true; `ReadingChatPage` sets that from its visibility. Two live views never switch each other off.

### Design rules

- **Never touch the game process.** Read pixels and send input only. This is a product promise; see the [FAQ](faq.md).
- **Logic goes in Core.** If something can be tested without Windows, it belongs in `Envoy.Core` with a test.
- **Everything themeable is a `DynamicResource`.** Colours never appear in window XAML.
- **Settings apply live.** `MainWindow` debounces any edit, calls `AppHost.ApplySettings()` and auto-saves.
- **Pages bind; view models command.** Page XAML has no event handlers and page code-behind no logic; view models use
  `IShell`, never `App.Current`, `Process.Start` or `MessageBox`. `tools/typecheck/check_ui_rules.py` enforces this.
- **Text built in code lives in Core.** User-facing strings assembled in code are `*Text` classes in `Envoy.Core`
  with named placeholders, filled by `TextTemplate.Format`.
- **Fail soft.** Translation errors show on the message; a broken provider falls through to the next; OCR or capture
  errors show in the status line and never crash the loop.

## Threading

- The capture/OCR loop runs on the thread pool (`Task.Run`). Each tick does capture → OCR → hand-off to the pipeline
  without waiting for translation.
- `ChatPipeline` is thread-safe for concurrent `ProcessAsync` calls: de-duplication and the player directory lock
  internally.
- All UI updates go through `Dispatcher.BeginInvoke` in `AppHost`.
