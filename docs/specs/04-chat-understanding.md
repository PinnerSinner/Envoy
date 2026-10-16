# Chat understanding: wrapped lines, game notices, stable message identity and the pending-message lifecycle

Status: Implemented (revised against `00-integration.md`; see Deviations)

Area id: `04-chat-understanding` · Requirement prefix: `CHAT` · Step **6** of 11 (contract §1).

**Depends on** (all already on `main` when this step starts):

- `01-ui-foundation`: `Pages/ReadingChatPage.xaml`, `MainViewModel.ReadingChat.cs`, `CapturePreviewViewModel`, the
  preview lease, `Envoy.Core.Mvvm`, `SettingsAutoApplier`, and the smoke loop over `ThemeManager.PaletteIds` ×
  `PageIds.All`;
- `08a-quality-gates`: analyzers, `BannedSymbols.txt`, and `tools/ci/check_policy.py` with `policy.json`;
- `02-reliability-observability`: `ILog`/`LogHub`/`LogField.Sensitive`, `AppHostOptions`, instance `DataFolder`,
  `[Personal]`, `DataInventory`, `docs/privacy.md`, `IShell.CopyText`, and the `CLAUDE.md` rule that every `catch` in
  `AppHost.cs` logs;
- `03-translation-resilience`: the per-call deadline inside `FallbackTranslator`, which rethrows
  `OperationCanceledException` only when the caller's token is cancelled (REQ-TRR-7);
- `05-capture-engine`: the `ChatWatcher` rewrite and its tick (contract §2.7), `OcrMerger.MergeLines`, `OcrLine` with
  reserved `Left`/`Right`, `CaptureFrame(Image, Lines, OcrTime)`, and the watcher passing `IReadOnlyList<OcrLine>` with
  `CancellationToken.None` to `ChatPipeline.ProcessAsync` (REQ-CAP-42).

**Delivers for later steps** (contract §1, row 6): stitching, dedupe, system lines, the cleaner, the
pending-message lifecycle (contract §2.6), and "Understood as" in `CapturePreviewViewModel`.

`docs/specs/00-integration.md` is binding. Where this file and the contract disagree, the contract wins.

## Context

### What exists today (after steps 1–5)

| Piece | Behaviour | Where |
| --- | --- | --- |
| OCR line geometry | 05's `OcrMerger.MergeLines(passes, preferredLanguage)` returns one `OcrLine(Text, Top, Bottom, Left = 0, Right = 0)` per row. `Left`/`Right` are reserved and always `0`. `Merge` is `MergeLines(...).Select(l => l.Text)`. | `src/Envoy.Core/Ocr/OcrResults.cs` |
| Watcher hand-off | Tick step 10 calls `pipeline.ProcessAsync(lines, CancellationToken.None).Forget(log, "pipeline")`. `lines` is the `IReadOnlyList<OcrLine>` from `MergeLines`. 05's geometry overload throws the geometry away (`lines.Select(l => l.Text)`). Step 11 raises `FrameCaptured(new CaptureFrame(copy, lines, ocrTime))` while a preview lease is held. | `src/Envoy.App/Services/ChatWatcher.cs` (05) |
| Live view | `CapturePreviewViewModel` holds the preview lease while `IsActive`, and shows `Image`, `Text`, `Stats` and 05's `MetricsText`/`HealthText`. | `src/Envoy.App/ViewModels/CapturePreviewViewModel.cs` (01, 05) |
| Parsing | `ChatLineParser.Parse(string)` handles one line. It strips a `12:04` / `[12:04:33]` timestamp, then a bracketed channel prefix (`[All]`, `(Team)`, `<…>`) taken from a hard-coded `KnownChannels` set in about 10 languages. It then takes the first `:`/`：` within 33 characters as the sender separator, and recognises a `Name (Team):` suffix channel. Lines it does not recognise become sender-less text. | `src/Envoy.Core/Chat/ChatLineParser.cs` |
| Message model | `record ChatMessage(Raw, Channel, Sender, Text)`. | `src/Envoy.Core/Chat/ChatMessage.cs` |
| Pipeline | `ChatPipeline.ProcessAsync(IEnumerable<string>)` treats **each OCR line as one message**. It parses, then applies `IsWorthShowing` (MinCharacters, needs a letter, not me, not muted, channel filter), then `ChatDeduplicator.TryAccept(chat.Raw)`, then classifies and translates with `Task.WhenAll`. | `src/Envoy.Core/Pipeline/ChatPipeline.cs` |
| Options | `ChatPipelineOptions` is a mutable class. `AppHost.ApplySettings` copies seven properties into `Pipeline.Options` on the UI thread while the watcher thread reads them. `MutedPlayers` is the live `AppSettings` list. This breaks contract §7. | `ChatPipeline.cs`, `src/Envoy.App/AppHost.cs` |
| Pending messages | `TranslateAsync` has `catch (OperationCanceledException) { return; }` without raising `MessageUpdated`. A cancelled translation leaves the overlay on "translating…" for good. 05 already passes `CancellationToken.None`, so a capture pause no longer cancels. `NewMatch` and `Dispose` still have no defined outcome. | `ChatPipeline.cs` |
| De-duplication | `TryAccept(string)` normalises the **raw line**, including the timestamp and channel. Two reads are the same if they are equal, one is a prefix of the other (6+ characters), or `TextSimilarity.Similarity ≥ 0.8`. Memory is 3 minutes and 200 entries. | `src/Envoy.Core/Chat/ChatDeduplicator.cs`, `TextSimilarity.cs` |
| Player names | `PlayerDirectory` matches names loosely (similarity ≥ 0.8) for language memory. The overlay, transcript and dedupe all use the sender **exactly as OCR read it** in each frame. | `src/Envoy.Core/Chat/PlayerDirectory.cs` |
| Tests | `ChatLineParserTests`, `ChatDeduplicatorTests`, `OcrMergerTests`, `PipelineFeatureTests`, `ChatPipelineTests`. None of them covers more than one line of context. | `tests/Envoy.Core.Tests/ChatTests.cs`, `OcrTests.cs`, `PlayerAndUpdateTests.cs`, `TranslationTests.cs` |

### What is weak or missing for production

1. **Wrapped messages are split.** A long message wraps onto continuation lines that have no sender. Each
   continuation is translated as a separate message with no sender. The overlay shows half-sentences, and every
   wrapped message costs 2–3 provider requests. A short tail such as `它` falls under `MinCharacters` and is lost.
2. **Your own and muted players' wrapped lines leak.** The own-name and mute filters see only the first line. The
   continuation of your own message is translated back to you, and a muted player's tail still shows.
3. **Game notices are treated as chat.** Notices such as resignations, age-ups or "player joined" lines have no
   sender. They are translated, which wastes characters and clutters the overlay. There is no way to hide them.
4. **Scrolling breaks identity.** When chat scrolls, a message's head line can leave the region while its tail stays.
   The tail then looks new and is translated as a fragment. The dedupe key includes the timestamp and channel. Two
   players typing the same `gg` are told apart only by accident of string similarity.
5. **Icon junk.** Rank, civilisation or platform icons next to names are read as stray glyphs (`©`, `■`, `|`, U+FFFD,
   private-use or zero-width characters). They become part of the sender name, which breaks the own-name, mute and
   dedupe matching.
6. **Stuck "translating…".** A translation that ends in `OperationCanceledException` never reaches a terminal state.
   Contract §2.6 assigns this lifecycle to this area.
7. **Unsafe option sharing.** The watcher thread reads options that the UI thread mutates (contract §7).
8. **No regression corpus.** No test feeds a realistic multi-line, multi-frame capture through OCR merging and the
   pipeline, so the heuristics can regress without anyone noticing.
9. **Repeats, name jitter and hard-coded formats** (lower priority). A player who writes `gg` twice shows once.
   `Zhang Wei` and `Zhanq Wei` show as two speakers. Channel words and separators are compiled into `ChatLineParser`.

## Goals / Non-goals

**Goals (MUST for 1.0)**

- Join continuation lines to their message (stitching), using parse results and the line geometry that 05 delivers.
- Classify game notices ("system lines") from a user-editable pattern list. Notices are never translated, and they
  are hidden by default.
- Clean OCR symbol junk before parsing.
- Give each message one identity across frames and scrolling. Dedupe is keyed on (sender, text), and fragments of
  known messages are suppressed.
- Own the pending-message lifecycle: every `Pending` message reaches exactly one terminal state (contract §2.6).
- Publish `ChatPipelineOptions` as an immutable snapshot (contract §7).
- Build a golden-test corpus of multi-line, multi-frame captures, in a text format that includes geometry.
- Add an "Understanding chat" card to the Reading Chat page.

**Goals (SHOULD/COULD, built if the step has room)**

- "Understood as" and "Copy as test capture" in the Live view (`CapturePreviewViewModel`).
- Occurrence counting for repeated messages, and canonical sender names.
- Data-driven chat format profiles (an embedded `generic` profile plus an optional user `chat-formats.json`).
- A player-name heuristic for notices, `Left`/`Right` geometry, and full-width folding.

**Non-goals**

- Any change to `ChatWatcher`, the tick, `CaptureFrame` or `MergeLines`. These belong to 05 (contract §2.7). REQ-CHAT-1
  and REQ-CHAT-3 moved to 05 as REQ-CAP-42.
- Game-specific facts we cannot verify here, such as exact notice strings, chat colours or per-game channel words.
  Nothing ships with built-in notice patterns.
- Colour-based or pixel-based line classification.
- Re-translating a message whose text grows after it was emitted. This is listed under Risks.
- Changes to `LookupWindow` (Scan & translate). It still joins all lines with newlines.
- Changes to the translation stack (03), the overlay layout (07c) or the transcript format. One exception: a message
  cancelled by the lifecycle shows no error row in the overlay (REQ-CHAT-47).
- New `IShell` members, `AppHostOptions` members, P/Invoke, network code or process launches.

## Requirements

MUST count: **17** (contract §9): CHAT-5, 7, 9, 15, 16, 18, 20, 23, 25, 26, 27, 35, 36, 40, 44, 46 and 47.

| Id | Priority | Requirement |
| --- | --- | --- |
| **Stitching** | | |
| REQ-CHAT-5 | MUST | A line that is not a system head and has no sender joins the chat message above it when all of these hold: it follows a chat head, or that head's continuation, in the same capture; stitching is on; the head has fewer than `MaxContinuationLines` continuations (default `4`; further lines start a fragment); and the geometry allows it. Geometry allows the join when `−0.5·median ≤ line.Top − prev.Bottom ≤ MaxGapFactor·median` (default `1.0`) and `abs(line.Height − prev.Height) ≤ HeightTolerance·median` (default `0.5`), where `median` is the capture's median line height. With `Chat.StitchWrappedLines = false` (default `true`), every line is its own message, as today. The three numbers are **provisional** until the release calibration step (M-CHAT-5) confirms them. *(Absorbs REQ-CHAT-6 and 11.)* |
| REQ-CHAT-7 | MUST | Joined text uses no separator when the character before the join or the first character after it is Han, Kana, or CJK or full-width punctuation. Otherwise it uses one space. |
| REQ-CHAT-8 | SHOULD | A stitched message has `Raw` = the cleaned source lines joined with `\n`, `LineCount` = the number of lines, and `Top`/`Bottom` taken from its first and last lines. |
| REQ-CHAT-9 | MUST | The own-name, muted-player, channel and `MinCharacters` filters judge a stitched message as a whole. No continuation of a dropped message is ever emitted. |
| REQ-CHAT-10 | SHOULD | When no line has geometry (all heights are zero, for example from the `IEnumerable<string>` overload), stitching joins adjacent lines using parse results alone. |
| REQ-CHAT-12 | SHOULD | A continuation that follows a system head joins that system entry. It does not become a chat fragment. |
| REQ-CHAT-13 | COULD | When `Left`/`Right` are known, a line that parses with a sender is treated as a continuation instead if the sender is not a known player and the previous line's `Right` is within 6 % of the capture's widest `Right`. |
| **Fragments and identity** | | |
| REQ-CHAT-15 | MUST | Sender-less lines that are not system lines and have no head above them in the capture form a `Fragment` entry. This happens at the top of the capture, after a geometry break, or past the continuation limit. Consecutive such lines join one fragment. A fragment is suppressed when its normalised text (2+ characters) is contained in a remembered message, either exactly or fuzzily (window similarity ≥ dedupe `Threshold`). Otherwise it is emitted as a message with no sender. *(Absorbs REQ-CHAT-14.)* |
| REQ-CHAT-16 | MUST | Dedupe is keyed on (sender, text). Channel, timestamp and geometry are ignored. Two reads are the same message when their texts match (equal, a 6+ character prefix, or similarity ≥ `Threshold`), **and** either sender is null or the two senders' similarity is ≥ `SenderThreshold` (default `0.75`). So `A: gg` and `B: gg` are two messages. *(Absorbs REQ-CHAT-17.)* |
| REQ-CHAT-18 | MUST | Messages dropped by filters (own, muted, channel, hidden system) are still recorded in dedupe memory as seen, so that their scrolled-off fragments are suppressed later. |
| REQ-CHAT-19 | SHOULD | Occurrence counting. Suppose one capture holds N reads of the same message key, and the most seen at once before was M < N. Then N − M of them are accepted as new. Two visible `gg` lines from one player give two messages, and re-reading them gives none. |
| REQ-CHAT-20 | MUST | Concurrency. `ChatDeduplicator.AcceptNew(capture)` decides a whole capture atomically under its lock, so concurrent `ProcessAsync` calls cannot both accept the same message. `ChatPipelineOptions` is an immutable record. AppHost builds it in `ApplySettings` with copied collections and publishes it to `ChatPipeline.Options`, which uses `Volatile.Write`. The pipeline reads the snapshot once per capture (contract §7). |
| REQ-CHAT-21 | SHOULD | Before dedupe and emission, a sender is replaced by the display name of a known player with an equal `TextSimilarity.OcrKey` (OCR-confusable folding). Names that only look alike (`Player1`/`Player2`) are never merged. |
| **System lines** | | |
| REQ-CHAT-23 | MUST | User patterns (`Chat.SystemLinePatterns`) are tested **before** sender parsing. Each pattern matches the whole cleaned line, case-insensitively, both with and without a leading timestamp. `*` matches any run, `{player}` matches 1–32 characters, a `re:` prefix means a raw .NET regex, and blank lines and `#` comments are ignored. A matched line is a system head, even if it contains a colon. *(Absorbs REQ-CHAT-24.)* |
| REQ-CHAT-25 | MUST | An invalid pattern is reported as `PatternError(Index, Pattern, Message)` and skipped. A regex timeout (50 ms) counts as no match. Neither case throws. |
| REQ-CHAT-26 | MUST | System entries are never sent to the translator. They never update `PlayerDirectory`, `LastOpponentLanguage` or `SuggestedReplyLanguage`. |
| REQ-CHAT-27 | MUST | With `Chat.HideSystemLines = true` (the default), system entries are not emitted. With `false`, they are emitted with `Kind = System`, `Status = Untranslated` and `SourceLanguage = MyLanguage`. |
| REQ-CHAT-28 | SHOULD | With `Chat.DetectSystemLinesByPlayerName = true` (default **`false`**), a line with no sender that starts with a known player name, followed by whitespace, is a system head. A known player is `MyPlayerName` or a `PlayerDirectory` name of 3+ normalised characters, compared by `OcrKey`. |
| REQ-CHAT-29 | SHOULD | A profile's `systemPatterns` is keyed by language code or `*`. The patterns for `MyLanguage` and for `*` are added to the user's list. The built-in `generic` profile ships with none. |
| **Format profiles** | | |
| REQ-CHAT-30 | SHOULD | Chat formats load from an embedded `chat-formats.json` that holds a `generic` profile. Parsing with `generic` gives results identical to today's `ChatLineParser` for every existing `ChatLineParserTests` case. |
| REQ-CHAT-31 | SHOULD | A profile defines channel aliases per canonical channel (`All`, `Team`, `Observers`, `Private`), bracket pairs, sender separators, a timestamp regex, `maxSenderLength` and stitching options. It may `inherit` another profile and override fields. |
| REQ-CHAT-32 | SHOULD | `AppHost.ChatFormatsPath` (`{DataFolder}\chat-formats.json`), when the file exists, adds or replaces profiles by id and can map game ids to profiles. A malformed file or field leaves the built-in profiles in force, records the problem in `ChatFormatCatalog.Errors`, and never throws. Envoy only reads this file and never writes it. |
| REQ-CHAT-33 | SHOULD | `ChatFormatCatalog.Resolve(setting, gameId)` returns the profile named by the setting. `auto` uses the game mapping, and anything unknown falls back to `generic`. |
| REQ-CHAT-34 | SHOULD | The active profile is re-resolved when settings apply (`ApplySettings` slot 5) and when the detected game changes (`RefreshGame` slot 2). |
| **Cleanup** | | |
| REQ-CHAT-35 | MUST | `ChatTextCleaner.Clean` removes control and format characters (including zero-width characters and BOM), U+FFFD, private-use characters, variation selectors and lone surrogates. It also collapses runs of whitespace and trims. |
| REQ-CHAT-36 | MUST | `Clean` strips a leading run of icon junk that is followed by whitespace before the channel or sender. Icon junk means the Unicode symbol categories `So`/`Sm`/`Sk`/`Sc` plus `•·»›■□●○◆◇▶►|*#~`. Opening brackets are never stripped. |
| REQ-CHAT-37 | SHOULD | `Clean` folds full-width ASCII letters and digits (U+FF10–FF19, FF21–FF3A, FF41–FF5A) to ASCII. It must not apply NFKC or NFKD, so Hangul compatibility jamo (`ㅋㅋ`, `ㄱㄱ`) stay unchanged. |
| REQ-CHAT-38 | SHOULD | Sender names are trimmed of surrounding punctuation other than bracket pairs, so clan tags survive. |
| REQ-CHAT-39 | SHOULD | `Chat.CleanSymbols = false` skips REQ-CHAT-36 to 38, but still removes invisible characters (REQ-CHAT-35). |
| **UI and tooling** | | |
| REQ-CHAT-40 | MUST | `Pages/ReadingChatPage.xaml` has an "Understanding chat" card. It binds stitching, hide notices and the notice-pattern list, plus a control for each SHOULD setting that is built (format profile, name heuristic, symbol cleanup). Pattern and profile errors appear on the card in the `Danger` colour, with text from `ChatUnderstandingText`. Bindings live in the page, the logic lives in `MainViewModel.ReadingChat.cs`, there is no `Click=`, and every new interactive control has `AutomationProperties.Name` (contract §7, §8.7, §8.8). |
| REQ-CHAT-41 | SHOULD | `CapturePreviewViewModel.UnderstoodText` shows "Understood as" under the raw OCR lines. It is computed on the UI thread from `host.Pipeline.Describe(frame.Lines)`, which has no side effects (contract §2.8). Each entry shows its kind, channel, sender, text and joined-line count. |
| REQ-CHAT-42 | SHOULD | `CapturePreviewViewModel.CopyAsTestCaptureCommand` copies the last frame's merged lines, in `CaptureFixture` format, through `IShell.CopyText`. It runs only when the user clicks it, nothing is sent anywhere, and the caption warns that the text contains chat and names. |
| **Golden suite, robustness, privacy and lifecycle** | | |
| REQ-CHAT-44 | MUST | A golden suite of at least 14 fixture files (the required set in the Test plan) runs each frame through `OcrMerger.MergeLines` and `ChatPipeline.ProcessAsync`, with a fake translator and a fake clock, and compares the emitted messages exactly. `CaptureFixture.Parse(Write(x))` round-trips frames, passes, geometry and directives, and parse errors name the line number. *(Absorbs REQ-CHAT-43.)* |
| REQ-CHAT-45 | SHOULD | If capture analysis throws unexpectedly, the pipeline falls back to one-line-per-message parsing for that capture, logs a Warning under category `Chat` with no chat text, sets `LastAnalysisError`, and keeps running. |
| REQ-CHAT-46 | MUST | Privacy and locality. Nothing under `src/Envoy.Core/Chat`, `src/Envoy.Core/Ocr` or `src/Envoy.Core/Pipeline` does network I/O. Chat text, patterns and player names reach the log only through `LogField.Sensitive`. `Chat.SystemLinePatterns` carries `[Personal]`. If REQ-CHAT-32 is built, `chat-formats.json` joins `DataInventory.Items` and `docs/privacy.md` in the same change (contract §8.2). |
| REQ-CHAT-47 | MUST | The pending-message lifecycle (contract §2.6). Every message emitted as `Pending` reaches exactly one terminal state (`Translated`, `Failed` or `Untranslated`) and raises `MessageUpdated` exactly once for that transition. Translations run under a pipeline-lifetime token owned by `ChatPipeline`. The token passed to `ProcessAsync` cancels only analysis. `NewMatch()`, `Reset()` and `Dispose()` cancel the lifetime token. Any `OperationCanceledException` during translation ends as `Failed` with `Error = "cancelled"` and `WasCancelled = true`. The overlay then shows the original line with no spinner and no error row. |

The contract's "`Skipped`" terminal state is today's `IncomingStatus.Untranslated`. No new enum value is added.

**Retired ids** (contract §9):

- 1 and 3 moved to 05 as REQ-CAP-42.
- 2 and 22 are now Definition-of-done items: existing `OcrMergerTests` and `ChatDeduplicatorTests` stay unchanged and
  green.
- 6 and 11 folded into 5, 14 into 15, 17 into 16, 24 into 23, and 43 into 44.

## Design

### Overview of the new flow

```
05 tick: lines = OcrMerger.MergeLines(passes, lang)          (IReadOnlyList<OcrLine>, geometry kept)
   │      pipeline.ProcessAsync(lines, CancellationToken.None)
   ▼
ChatPipeline.ProcessAsync(IReadOnlyList<OcrLine>, ct)        ct cancels analysis only
   state = Volatile.Read(_state)                              (options snapshot + compiled patterns, once per capture)
   ChatCaptureAnalyzer.Analyze(lines, state.Context)          pure, stateless
      per line: ChatTextCleaner.Clean → SystemLinePatterns? → ChatLineParser.Parse → [name heuristic]
      walk: heads (Chat/System) collect continuations by geometry; orphans become Fragments
   [PlayerDirectory.Canonicalize(sender)]                     SHOULD
   ChatDeduplicator.AcceptNew(all entries)                    atomic; fragment containment; filtered ones remembered
   emit = accepted ∩ ShouldEmit (system hidden?, IsWorthShowing)
   Classify; system → Untranslated, no request
   translate under _lifetime.Token  ──►  exactly one MessageUpdated per Pending message
```

### `src/Envoy.Core/Ocr/OcrResults.cs` (changed, Core only)

05 owns `MergeLines` and the record shape. 04 adds helpers to the record, plus the SHOULD median for `Left`/`Right`:

```csharp
public sealed record OcrLine(string Text, double Top, double Bottom, double Left = 0, double Right = 0)
{
    public double Center => (Top + Bottom) / 2;          // exists
    public double Height => Bottom - Top;                // exists
    public bool HasGeometry => Bottom > Top;             // 04
    /// <summary>Uniform synthetic geometry for callers and tests that only have strings. lineHeight 0 = unknown.</summary>
    public static IReadOnlyList<OcrLine> FromTexts(IEnumerable<string> texts, double lineHeight = 0, double gap = 0); // 04
}
```

REQ-CHAT-4 (SHOULD) fills `Left`/`Right` in `src/Envoy.App/Services/OcrService.cs`, using the minimum and maximum
of the words' `BoundingRect` edges, and has `MergeLines` carry the row's median of each. Rows without words keep
`0`. This is the only App service file 04 touches, and it does not touch `ChatWatcher`.

### `src/Envoy.Core/Ocr/CaptureFixture.cs` (new)

A text format for recorded captures. The golden tests use it, and so does "Copy as test capture" (SHOULD).

```
# Free comment lines start with '#'.
@me Hoplite                     # optional: options.MyPlayerName
@language en                    # optional: options.MyLanguage (default en)
@muted Spammer, Toxic99         # optional
@players 天下第一, Kim           # optional: pre-seed PlayerDirectory (Observe with null language)
@set hideSystem=false stitch=true clean=true byName=true profile=generic
@pattern *resigned*              # optional, repeatable: Chat.SystemLinePatterns entries
@frame                          # starts a capture; the fake clock advances 1 s per frame
@pass zh-CN                     # optional: engine language for the lines below (default: @language)
 10.0  28.0 | [全 部] 天 下 第 一 ： 这 张 地 图 的 金 矿 在 中 间 ， 我 们
 32.0  50.0 | 应 该 先 占 领 它
@expect                         # messages emitted by THIS frame, in order; "(none)" for none
chat | All | 天下第一 | 这张地图的金矿在中间，我们应该先占领它
```

- A line row is `top bottom [left right] | text`. The text is exactly what the engine returned. Raw Windows OCR quirks,
  such as spaces between CJK characters, are kept, and `OcrText.Clean` runs inside `MergeLines`.
- An expect row is `kind | channel | sender | text`, where `kind` is `chat`, `system` or `fragment` and `-` means null.
- An unknown `@set` key fails parsing with `FormatException`. A key whose feature was not built (for example `byName`
  or `profile`) is unknown, so a fixture can never pass while silently testing nothing.
- Fixtures contain chat text and player names. Contributed fixtures must be anonymised (`docs/chat-formats.md`).

```csharp
public sealed record CaptureFrameFixture(IReadOnlyList<OcrPass> Passes, IReadOnlyList<string> Expect);
public sealed record CaptureFixture(IReadOnlyDictionary<string, string> Directives, IReadOnlyList<string> Patterns,
                                    IReadOnlyList<CaptureFrameFixture> Frames)
{
    public static CaptureFixture Parse(string text);                 // FormatException("line N: …")
    public static string Write(IReadOnlyList<OcrLine> lines, string passLanguage); // one frame, empty "@expect"
    public string Write();                                           // full round-trip
}
```

### `src/Envoy.Core/Chat/ChatMessage.cs` (changed, source-compatible)

```csharp
public enum ChatLineKind { Chat, System, Fragment }

public sealed record ChatMessage(string Raw, string? Channel, string? Sender, string Text)
{
    public ChatLineKind Kind { get; init; } = ChatLineKind.Chat;
    public int LineCount { get; init; } = 1;     // REQ-CHAT-8 (SHOULD); 1 if not built
    public double Top { get; init; }
    public double Bottom { get; init; }
}
```

Existing `new ChatMessage(raw, ch, sender, text)` calls (`IncomingMessage.Preview`, tests) compile unchanged.

### `src/Envoy.Core/Chat/ChatTextCleaner.cs` (new)

```csharp
public static class ChatTextCleaner
{
    /// REQ-CHAT-35 always; REQ-CHAT-36 (and 37 if built) when symbols = true.
    public static string Clean(string line, bool symbols = true);
    /// REQ-CHAT-38 (SHOULD): trims punctuation around a sender, keeping balanced bracket pairs ("[CLAN] Bob").
    public static string CleanSender(string sender);
}
```

If REQ-CHAT-39 is not built, `symbols` is always `true`, and there is no `CleanSymbols` setting or checkbox.

### `src/Envoy.Core/Chat/SystemLinePatterns.cs` (new)

```csharp
public sealed record PatternError(int Index, string Pattern, string Message);

public sealed class SystemLinePatterns
{
    public static SystemLinePatterns Empty { get; }
    public static SystemLinePatterns Compile(IEnumerable<string> patterns, out IReadOnlyList<PatternError> errors);
    public int Count { get; }
    public bool IsMatch(string cleanedLine);   // RegexMatchTimeoutException → false
    /// Settings text box helper: split on newlines, trim, drop empty lines (comments kept).
    public static List<string> SplitLines(string text);
}
```

Each pattern compiles to `^…$` with `RegexOptions.IgnoreCase | CultureInvariant` and a 50 ms timeout. A wildcard
pattern is escaped first. Then `\*` becomes `.*`, `\{player}` becomes `.{1,32}?`, and each whitespace run becomes `\s+`.
`PatternError.Message` holds the regex error for the log, where it goes through `LogField.Sensitive`. The card never
shows it (contract §8.7: no raw exception text).

### `src/Envoy.Core/Chat/ChatCaptureAnalyzer.cs` (new, pure)

```csharp
public sealed record ChatContext(
    SystemLinePatterns SystemPatterns,       // user patterns (+ profile patterns for MyLanguage and "*", REQ-CHAT-29)
    IReadOnlyList<string> KnownPlayers,      // MyPlayerName + PlayerDirectory names (used by REQ-CHAT-28 only)
    bool Stitch,                             // options.StitchWrappedLines (&& profile.Stitching.Enabled if REQ-CHAT-31)
    StitchOptions Stitching,                 // defaults, or the profile's (REQ-CHAT-31)
    bool CleanSymbols,
    bool SystemByPlayerName,
    ChatFormatProfile? Profile);             // null → today's ChatLineParser.Parse(string) (REQ-CHAT-30 not built)

public sealed record StitchOptions(double MaxGapFactor = 1.0, double HeightTolerance = 0.5, int MaxContinuationLines = 4);

public static class ChatCaptureAnalyzer
{
    public static IReadOnlyList<ChatMessage> Analyze(IReadOnlyList<OcrLine> lines, ChatContext context);
    internal static string Join(string a, string b);   // REQ-CHAT-7
}
```

The algorithm:

1. Clean each line, and drop lines that are empty after cleaning. `median` is the median `Height` of the lines that
   have geometry. If no line has geometry, geometry is unknown.
2. Classify each line, in this order:
   1. `SystemPatterns.IsMatch(text)` or `IsMatch(StripTimestamp(text))` → **System head**.
   2. `Parse(text)` returns a sender → **Chat head**. The sender goes through `CleanSender` if REQ-CHAT-38 is built.
      REQ-CHAT-13 (COULD) can demote the line to a continuation.
   3. `SystemByPlayerName`, and the timestamp-stripped text starts with a known player at a word boundary →
      **System head** (REQ-CHAT-28).
   4. Otherwise → **continuation candidate**.
3. Walk from top to bottom, with `current` as the open entry. A candidate joins `current` when all of these hold:
   - `Stitch` is on and `current` is not null;
   - `current` is a chat entry, or a system entry if REQ-CHAT-12 is built;
   - `current.Continuations < MaxContinuationLines`;
   - `GeometryAllows(prev, line)`.

   `GeometryAllows` is the REQ-CHAT-5 test when `median > 0`. When geometry is unknown, it is `true` if REQ-CHAT-10 is
   built and `false` if not. A candidate that does not join opens a new **Fragment** entry, which later candidates can
   join. A head always opens a new entry.
4. Emit one `ChatMessage` per entry. A chat entry keeps the parsed channel and sender, and its `Text` is `Join` of the
   head text and each continuation. A system or fragment entry has `Channel = Sender = null`. Every entry sets `Kind`,
   and sets `Raw`, `LineCount`, `Top` and `Bottom` if REQ-CHAT-8 is built.

Cost: O(lines) regex calls per capture. Patterns compile once per settings change, never per capture.

### `src/Envoy.Core/Chat/ChatDeduplicator.cs` (changed)

```csharp
public double SenderThreshold { get; set; } = 0.75;

/// Atomically decides which messages of one capture are new; remembers all of them (REQ-CHAT-18, 20).
public IReadOnlyList<ChatMessage> AcceptNew(IReadOnlyList<ChatMessage> capture);

public bool TryAccept(string line) => AcceptNew([new ChatMessage(line, null, null, line)]).Count == 1;
```

Memory entries become `(string? SenderKey, string TextKey, DateTimeOffset SeenAt, int MaxSimultaneous)`. Under the
lock, `AcceptNew` takes each message in capture order:

- A **Fragment** first tries containment (REQ-CHAT-15) against every entry's `TextKey`. A match means it is rejected.
  A rejected fragment is not added to memory.
- Otherwise, find the best-matching entry by REQ-CHAT-16, or create one with `MaxSimultaneous = 0`. Increment this
  capture's hit count for that entry, and refresh `SeenAt` and `TextKey` (keeping the longer text).
  - With REQ-CHAT-19 built, accept only when `hits > MaxSimultaneous`, then set `MaxSimultaneous = hits`.
  - Without it, accept only when the entry was just created.

`TryAccept(string)` passes the whole raw line as text with a null sender, so every existing `ChatDeduplicatorTests`
case gives the same result as today.

`TextSimilarity` gains `ContainsFuzzy(string haystack, string needle, double threshold)`. It slides a window of the
needle's length (±1) over the haystack. Inputs are capped at 400 characters.

### `src/Envoy.Core/Chat/TextSimilarity.cs` and `PlayerDirectory.cs` (changed, SHOULD: REQ-CHAT-21, 28)

```csharp
/// Normalize + fold OCR-confusable sequences: rn→m, vv→w, cl→d, q→g. Digits beyond Normalize's 0/1/5 are not folded.
public static string OcrKey(string text);

public sealed class PlayerDirectory
{
    public IReadOnlyList<string> KnownNames { get; }               // takes the existing _gate
    public string? Canonicalize(string? sender);                   // known name with the same OcrKey, else input
}
```

`Player1`/`Player2` must stay distinct. `Zhanq Wei`/`Zhang Wei` and `rnike`/`mike` must fold together.

### Format profiles (SHOULD: REQ-CHAT-29 to 34)

`src/Envoy.Core/Chat/ChatFormatProfile.cs`, `ChatFormatCatalog.cs` and the embedded `chat-formats.json`
(`LogicalName="Envoy.Core.Chat.chat-formats.json"`):

```csharp
public sealed class ChatFormatProfile
{
    public const string GenericId = "generic";
    public string Id { get; }  public string Name { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Channels { get; }     // "All" → ["all","everyone","全部",…]
    public IReadOnlyList<string> Brackets { get; }                                  // ["[]", "()", "<>"]
    public IReadOnlyList<string> Separators { get; }                                // [":", "："]
    public string TimestampPattern { get; }
    public int MaxSenderLength { get; }                                             // 32
    public bool StitchEnabled { get; }  public StitchOptions Stitching { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SystemPatterns { get; } // lang or "*" → patterns
    public static ChatFormatProfile Generic => ChatFormatCatalog.BuiltIn.Generic;
    public string? CanonicalChannel(string token);   // "All"/"Team"/"Observers"/"Private" or null
}

public sealed class ChatFormatCatalog
{
    public const string Auto = "auto";
    public static ChatFormatCatalog BuiltIn { get; }                 // lazy; embedded resource
    public IReadOnlyList<ChatFormatProfile> Profiles { get; }        // generic first
    public ChatFormatProfile Generic { get; }
    public IReadOnlyDictionary<string, string> GameProfiles { get; } // game id → profile id (built-in: empty)
    public IReadOnlyList<ChatFormatError> Errors { get; }            // (Profile?, Field?, Kind); no raw exception text
    public static ChatFormatCatalog LoadWithUserFile(string path, ILog log); // built-in + user; never throws; read only
    public static ChatFormatCatalog Parse(string builtInJson, string? userJson); // for tests
    public ChatFormatProfile Resolve(string? setting, string? gameId);
}
```

- The `generic` alias lists are exactly today's `KnownChannels` and `Canonical` mapping, moved into data. The
  `generic` stitching values are the REQ-CHAT-5 defaults. `systemPatterns` and `games` are empty.
- A user file has the same shape. A profile with an existing id replaces that profile, `"inherits": "generic"` copies
  every field the user profile leaves out, and `games` entries are merged.
- A field that fails validation falls back to the inherited value and adds an `Errors` entry. Validation fails for a
  bad regex, empty separators, `maxGapFactor` outside 0.2–5, or `maxContinuationLines` outside 0–10. Unknown properties
  are ignored. JSON is read case-insensitively and allows comments and trailing commas.
- `ChatLineParser` gains `Parse(string line, ChatFormatProfile profile)`. `Parse(string)` stays as it is, and it is
  equal to `Parse(line, Generic)`.
- The algorithm is unchanged. The constants become profile lookups, and `FindSenderColon` keeps its digit:digit and
  `://` exclusions.

If these SHOULDs are not built:

- `ChatContext.Profile` is null and the analyzer calls today's `Parse(string)`.
- There is no `FormatProfile` setting, combo box, `ChatFormatsPath` or inventory entry.
- The omission is listed under Deviations.

### `src/Envoy.Core/Chat/ChatUnderstandingText.cs` (new, contract §8.7)

User-facing text built in code. Each member is a whole-sentence format string, and doc comments name the
placeholders, following 01's `*Text` style.

| Member | Text |
| --- | --- |
| `UnderstoodHeader` | `Understood as:` |
| `Nothing` | `(nothing understood yet)` |
| `ChatEntry` | `[{channel}] {sender}: {text}`. The `[{channel}] ` part is dropped when the channel is null. |
| `NoticeEntry` / `FragmentEntry` | `(notice) {text}` / `(part of an earlier message) {text}` |
| `LineCount` | `{entry} ({count} lines)`. Shown when `LineCount > 1`. |
| `PatternInvalid` | `Line {line} isn't a pattern Envoy understands, so it's ignored. Check its brackets and symbols, or remove "re:".` |
| `FormatsFileUnreadable` | `Your chat-formats.json couldn't be read, so Envoy is using the built-in formats. Check the file for typos.` |
| `FormatsFieldInvalid` | `In chat-formats.json, the "{field}" setting of profile "{profile}" isn't valid, so Envoy uses the inherited value.` |
| `CopiedCaption` | `Copied. It contains chat and player names, so only share it if you're happy to.` |

`public static string Describe(IReadOnlyList<ChatMessage> entries)` writes one line per entry. `Errors(...)` formats
the pattern and profile errors for the card.

`Envoy.Core.Pipeline.PipelineText.Cancelled = "cancelled"` is the `Error` text for REQ-CHAT-47. The overlay never shows
it, but the transcript and log do.

### `src/Envoy.Core/Pipeline/ChatPipeline.cs` (changed)

```csharp
public sealed record ChatPipelineOptions           // was a mutable class; object initialisers still compile
{
    public string MyLanguage { get; init; } = "en";
    public string MyPlayerName { get; init; } = "";
    public bool SkipMyLanguage { get; init; } = true;
    public bool UseSlangGlossary { get; init; } = true;
    public int MinCharacters { get; init; } = 2;
    public IReadOnlyList<string> MutedPlayers { get; init; } = [];
    public ChannelFilter Channels { get; init; } = ChannelFilter.Everything;
    // 04
    public bool StitchWrappedLines { get; init; } = true;
    public bool HideSystemLines { get; init; } = true;
    public IReadOnlyList<string> SystemLinePatterns { get; init; } = [];
    public bool DetectSystemLinesByPlayerName { get; init; }          // SHOULD (28); false
    public bool CleanSymbols { get; init; } = true;                   // SHOULD (39)
    public ChatFormatProfile? Format { get; init; }                   // SHOULD (30–34); null = legacy parser

    /// Snapshot for AppHost.ApplySettings: copies every collection with ToArray() (contract §7).
    public static ChatPipelineOptions From(AppSettings settings, ChatFormatProfile? format);
}

public sealed class ChatPipeline : IDisposable
{
    public ChatPipeline(ITranslator translator, ChatPipelineOptions options, TimeProvider? clock = null, ILog? log = null);

    /// Getter: Volatile.Read. Setter (UI thread): compiles patterns once, then Volatile.Write of an immutable
    /// state record (Options, ChatContext pieces, PatternErrors).
    public ChatPipelineOptions Options { get; set; }
    public IReadOnlyList<PatternError> SystemPatternErrors { get; }

    public Task<IReadOnlyList<IncomingMessage>> ProcessAsync(IReadOnlyList<OcrLine> lines, CancellationToken ct = default);
    public Task<IReadOnlyList<IncomingMessage>> ProcessAsync(IEnumerable<string> lines, CancellationToken ct = default)
        => ProcessAsync(OcrLine.FromTexts(lines), ct);

    /// Analysis only: no dedupe, no events, no translation, no PlayerDirectory writes. Thread-safe.
    public IReadOnlyList<ChatMessage> Analyze(IReadOnlyList<OcrLine> lines);
    public string Describe(IReadOnlyList<OcrLine> lines) => ChatUnderstandingText.Describe(Analyze(lines));

    public string? LastAnalysisError { get; }        // SHOULD (45)
    public void Reset();                             // dedupe cleared + lifetime token cancelled and replaced
    public void NewMatch();                          // as today + lifetime token cancelled and replaced
    public void Dispose();                           // lifetime token cancelled; later ProcessAsync returns []
}
```

**Processing.** `ProcessAsync` reads the state once. It calls `ct.ThrowIfCancellationRequested()` before analysis and
again before `AcceptNew`, and never after. Then it does the following, in order:

1. `Analyze`, falling back to line mode on an unexpected exception (REQ-CHAT-45).
2. Canonicalise senders (REQ-CHAT-21).
3. `AcceptNew` over **all** entries, so filtered entries are remembered (REQ-CHAT-18).
4. Emit the accepted entries that pass `ShouldEmit`:
   - For `Kind == System`: emitted only when `!HideSystemLines` and `TextElementCount ≥ MinCharacters`.
   - Otherwise: the existing `IsWorthShowing`, applied to the whole stitched message (REQ-CHAT-9).
5. Classify. A `System` message skips `Classify`, gets `Status = Untranslated` and `SourceLanguage = MyLanguage`, and
   is never observed in `PlayerDirectory` (REQ-CHAT-26).

**Lifecycle (REQ-CHAT-47, contract §2.6).**

- `_lifetime` is a `CancellationTokenSource` swapped under a small lock. `ProcessAsync` captures `_lifetime.Token` when
  it emits and passes that token, never `ct`, to every `TranslateAsync`. 03's `FallbackTranslator` links it with the
  per-call deadline.
- `Reset`, `NewMatch` and `Dispose` cancel the current source. `Reset` and `NewMatch` then install a new one.
- `TranslateAsync` ends in exactly one place that sets the terminal state and raises `MessageUpdated`. The two cancel
  cases are:
  - `catch (OperationCanceledException)` → `Status = Failed`, `Error = PipelineText.Cancelled`, `WasCancelled = true`;
  - `catch (Exception)` → `Failed` with the message, as today.
- Nothing returns early without raising the event.
- A capture pause or a watcher restart does not touch the lifetime token, so those translations finish normally.
- `IncomingMessage` gains `public bool WasCancelled { get; internal set; }`.
- `OverlayItem.IsFailed` becomes `Status == Failed && !Message.WasCancelled`. The row shows the original text
  (`DisplayText`), with no spinner and no ⚠ row.
- `Reset()` is called on region change. It also cancels in-flight translations, as contract §2.6 requires. See Risks.

### Settings (`src/Envoy.Core/Settings/AppSettings.cs`, shared)

```csharp
public sealed class AppSettings
{
    // … existing …
    public ChatSettings Chat { get; set; } = new();   // NEW
}

public sealed class ChatSettings                      // NEW; additive, CurrentVersion stays 1 (contract §7)
{
    public bool StitchWrappedLines { get; set; } = true;
    public bool HideSystemLines { get; set; } = true;
    /// One pattern per entry; see docs/chat-formats.md. May contain player names.
    [Personal] public List<string> SystemLinePatterns { get; set; } = [];
    public bool DetectSystemLinesByPlayerName { get; set; } = false;   // only if REQ-CHAT-28 is built
    public bool CleanSymbols { get; set; } = true;                     // only if REQ-CHAT-39 is built
    public string FormatProfile { get; set; } = ChatFormatCatalog.Auto; // only if REQ-CHAT-33 is built
}
```

The defaults reproduce today's behaviour except where this spec deliberately changes it:

- No pattern means no line is treated as a notice. The name heuristic is off.
- Stitching, cleanup and the new identity rules are on, which is the point of this area.

Under the arrival rule, a property for an unbuilt SHOULD is left out, not added as a placeholder. 02's reflection test
then masks `SystemLinePatterns` as `[N entries]`.

### App shell changes (thin)

**`src/Envoy.App/AppHost.cs` (shared).** Each change goes into the slot that contract §3 reserves for it:

- **Constructor slot 5:** `ChatFormats = ChatFormatCatalog.LoadWithUserFile(ChatFormatsPath, Log.For("Chat"))`.
  - This applies only if REQ-CHAT-32 is built. With REQ-CHAT-30 only, it is `ChatFormatCatalog.BuiltIn`.
  - `public string ChatFormatsPath => Path.Combine(DataFolder, "chat-formats.json");` is an instance property
    (contract §3). It never names `%APPDATA%`.
  - Catalog errors are logged once at Warning under `Chat`.
- **Constructor slot 6:** `Pipeline = new ChatPipeline(_translator, BuildChatOptions(), Time, Log.For("Chat"))`.
- **`ApplySettings` slot 5:** `Pipeline.Options = BuildChatOptions();`. This one assignment replaces today's seven
  per-property copies. `BuildChatOptions()` is
  `ChatPipelineOptions.From(Settings, ChatFormats?.Resolve(Settings.Chat.FormatProfile, CurrentGameId))`.
- **`RefreshGame` slot 2:** when the game changes, `Pipeline.Options = BuildChatOptions();` re-resolves the format
  (REQ-CHAT-34).
- **`ReloadChatFormats()`** (SHOULD, with REQ-CHAT-32) reloads the catalog, then runs `Pipeline.Options =
  BuildChatOptions()`.
- **`Dispose()`:** `Pipeline.Dispose()` right after the watcher stops, which is inside contract §4 `ExitApp` step 3.
  04 writes no file during `Dispose`, so 02's `SaveSuppressed` rule has nothing to check here.
- Every new `catch` logs (02's `CLAUDE.md` rule).

**`src/Envoy.App/Pages/ReadingChatPage.xaml`.** Add an `OrnateFrame` card titled "Understanding chat" between "Fine
tuning" and "Live view". It reuses `SettingRow`, `Text.H2`, `Text.Label` and `Text.Caption`, and uses only
`DynamicResource` colours. Each control below carries `AutomationProperties.Name` equal to its visible label:

- `CheckBox` "Join messages that wrap onto the next line" → `Settings.Chat.StitchWrappedLines`.
- `CheckBox` "Hide game notices (lines that aren't a player talking)" → `Settings.Chat.HideSystemLines`.
- "Also treat these lines as notices": a `TextBox` with `AcceptsReturn=True` and `MinHeight=64`, bound to
  `SystemLinePatternsText` with `UpdateSourceTrigger=LostFocus`. Caption: "One per line. `*` matches anything,
  `{player}` matches a name, and starting a line with `re:` makes it a regular expression."
- An error `TextBlock` with `Foreground={DynamicResource Danger}`, bound to `ChatUnderstandingStatus` and visible when
  `HasChatUnderstandingErrors` is true.
- Only if the SHOULD is built:
  - "Chat format" `ComboBox` (`ItemsSource=ChatFormatOptions`, `SelectedValue=Settings.Chat.FormatProfile`);
  - `CheckBox` "Treat lines that start with a player's name but have no ':' as notices";
  - `CheckBox` "Remove stray symbols that OCR reads from icons".

If REQ-CHAT-41/42 are built, the Live view adds:

- a `TextBlock` bound to `CapturePreview.UnderstoodText` under the raw lines;
- a "Copy as test capture" `Button` bound to `CapturePreview.CopyAsTestCaptureCommand`;
- a caption bound to `CapturePreview.CopyCaption`.

The code-behind still only toggles `IsActive`.

**`src/Envoy.App/ViewModels/MainViewModel.ReadingChat.cs`:**

- `SystemLinePatternsText`. The getter joins with `\n`. The setter runs `Settings.Chat.SystemLinePatterns =
  SystemLinePatterns.SplitLines(value); _applier.ApplyNow();`.
- `ChatUnderstandingStatus` (`ChatUnderstandingText.Errors(Host.Pipeline.SystemPatternErrors, Host.ChatFormats?.Errors)`)
  and `HasChatUnderstandingErrors`, both refreshed after `ApplyNow`.
- `ChatFormatOptions` (SHOULD): `("auto", "Match the game")`, followed by each profile's `(Id, Name)`.

**`src/Envoy.App/ViewModels/CapturePreviewViewModel.cs`** (SHOULD: REQ-CHAT-41, 42):

- The constructor gains an optional `Action<string>? copyText = null`. `MainViewModel` passes `_shell.CopyText`. 07a's
  guide passes nothing, and the copy button is then hidden.
- In `OnFrame`, after the `IsActive` check, it sets `UnderstoodText = Host.Pipeline.Describe(frame.Lines)` and keeps
  `_lastLines`.
- `CopyAsTestCaptureCommand` (a `RelayCommand`) runs `copyText(CaptureFixture.Write(_lastLines,
  Host.Settings.MyLanguage))` and then sets `CopyCaption = ChatUnderstandingText.CopiedCaption`.

**`src/Envoy.App/Windows/OverlayItem.cs`:** the one-line `IsFailed` change (REQ-CHAT-47).

**`src/Envoy.App/Services/OcrService.cs`:** `Left`/`Right` only if REQ-CHAT-4 is built.

This step leaves these unchanged: `ChatWatcher.cs`, `App.xaml.cs`, `IShell`, `AppHostOptions`, `Native.cs`,
`OverlayWindow.xaml`, `ComposeWindow*`, `LookupWindow*`, the palettes and `Controls.xaml`.

### Allowlists and launches (contract §5)

- **P/Invoke:** none. `Native.cs` and `policy.json` → `native` are unchanged.
- **Network:** none. `allowedPaths` and hosts are unchanged. Chat, OCR and pipeline code must stay outside the
  network `allowedPaths`. `check_policy.py` enforces this (AC-CHAT-24).
- **Process launches:** none. Copying goes through `IShell.CopyText` (02).

### Threading

- `ChatCaptureAnalyzer`, `ChatTextCleaner`, `SystemLinePatterns.IsMatch`, the profiles and `ChatPipelineOptions` are
  immutable or pure. They are safe on the watcher thread, in concurrent `ProcessAsync` calls, and in `Describe` on the
  UI thread.
- Only the UI thread sets `ChatPipeline.Options`, from `ApplySettings`, `RefreshGame` and `ReloadChatFormats`. The
  pipeline reads the published state once per capture.
- `ChatDeduplicator.AcceptNew` holds its lock for the whole capture. That is at most about 40 entries × 200 memory
  entries of Levenshtein on short strings, well under 1 ms.
- `PlayerDirectory.Canonicalize`/`KnownNames` take the existing `_gate`.
- `MessageUpdated` is raised on a thread-pool thread, as today. AppHost marshals it with `BeginInvoke`.

### Logging (02's `ILog`, category `Chat`)

| Event | Level | Fields |
| --- | --- | --- |
| Pattern compile errors after a settings change | Info | `count`, `pattern=Sensitive`, `error=Sensitive` (once per change) |
| `chat-formats.json` errors | Warning | `kind`, `profile`, `field` (no file content) |
| Analyzer fallback (SHOULD 45) | Warning | exception type and stack only, no message text from lines |
| Translation cancelled by lifecycle | Debug | `reason=newMatch/reset/dispose`, `count` |

A regex timeout is not logged per line, to avoid spam.

### Failure behaviour

| Failure | Behaviour |
| --- | --- |
| User `chat-formats.json` is malformed (SHOULD) | Built-in profiles stay in use. `FormatsFileUnreadable` shows on the card, and a Warning is logged. |
| Profile field invalid (SHOULD) | That field is inherited and `FormatsFieldInvalid` is listed. |
| Profile id in settings no longer exists (SHOULD) | `Resolve` returns `generic`. The setting is kept, so it works again if the file is restored. |
| User pattern invalid | Skipped. `PatternInvalid` (line N) shows on the card, and the other patterns still apply. |
| Regex timeout | Counts as no match for that line. |
| Analyzer exception | SHOULD: line-mode fallback for that capture. If REQ-CHAT-45 is not built, the exception faults `ProcessAsync` and 05's `.Forget(log, "pipeline")` logs it. That capture is lost and the loop keeps running. |
| Geometry missing (all zero) | Adjacency-only stitching (SHOULD 10), or one message per line. |
| New match, region change or exit while translating | The message ends `Failed`/`WasCancelled`, and the overlay shows the original line. |

## Acceptance criteria

All MUST rows use contract §9 kinds: a Core unit test, a typecheck, a named smoke section, a CI check, or a numbered
`docs/releasing.md` step (M-CHAT-n).

| Id | Requirements | Verified by |
| --- | --- | --- |
| AC-CHAT-1 | REQ-CHAT-4 (SHOULD) | `OcrMergerTests.MergeLines_carries_left_right_medians`; typecheck for `OcrService`. |
| AC-CHAT-2 | (retired) | REQ-CHAT-3 moved to 05 (REQ-CAP-42). |
| AC-CHAT-3 | REQ-CHAT-5, 7 (8) | `ChatCaptureAnalyzerTests.Joins_continuation_within_gap`, `Gap_too_large_starts_fragment`, `Height_mismatch_starts_fragment`, `Stops_at_max_continuations`, `Stitching_off_keeps_one_message_per_line`, `Joins_cjk_without_space`, `Joins_latin_and_hangul_with_space`, plus SHOULD `Stitched_message_has_raw_linecount_and_bounds`. Golden fixtures `latin-wrapped`, `zh-wrapped`, `ja-wrapped`, `ko-wrapped`, `ru-wrapped`, `geometry-breaks`. |
| AC-CHAT-4 | REQ-CHAT-9, 18 | Golden `own-and-muted-wrapped`: in frame 1 the stitched own and muted messages are dropped, and in frame 2 their scrolled-off tails emit nothing. Also `ChatPipelineUnderstandingTests.Filtered_messages_are_remembered_for_fragments`. |
| AC-CHAT-5 | REQ-CHAT-10 (SHOULD) | `ChatCaptureAnalyzerTests.Unknown_geometry_stitches_adjacent_lines`. |
| AC-CHAT-6 | REQ-CHAT-23 (12) | `ChatCaptureAnalyzerTests.Pattern_with_colon_beats_sender_parse`; `SystemLinePatternsTests.Wildcard_player_and_regex_forms`, `Matches_with_and_without_timestamp`, `Comments_and_blanks_ignored`; golden `system-patterns`. SHOULD: `Continuation_of_notice_joins_notice`. |
| AC-CHAT-7 | REQ-CHAT-13 (COULD) | `ChatCaptureAnalyzerTests.Full_previous_line_demotes_unknown_sender_to_continuation`, only if built. |
| AC-CHAT-8 | REQ-CHAT-15 | `ChatCaptureAnalyzerTests.Leading_lines_are_one_fragment`; `ChatDeduplicatorTests.Fragment_contained_in_known_message_is_rejected`, `Fuzzy_fragment_is_rejected`, `Unknown_fragment_is_accepted`; golden `scroll-orphan`, `orphan-unknown`. |
| AC-CHAT-9 | REQ-CHAT-16 | `ChatDeduplicatorTests.Same_text_from_different_senders_is_two_messages`, `Sender_jitter_is_same_message`, `Channel_and_timestamp_do_not_affect_identity`. Every existing `ChatDeduplicatorTests` case stays unchanged and green (DoD, formerly REQ-CHAT-22). |
| AC-CHAT-10 | REQ-CHAT-20 (19) | `ChatDeduplicatorTests.Concurrent_captures_do_not_double_accept` (`Parallel.For` 50 × `AcceptNew([A: hello])` → exactly 1 acceptance). `ChatPipelineOptionsTests.Snapshot_is_isolated_from_settings`: after `From(settings, …)`, mutating `settings.MutedPlayers`, `settings.Chat.SystemLinePatterns` and `MyPlayerName` leaves the snapshot unchanged (contract §7). `ChatPipelineOptionsTests.Options_swap_mid_capture_uses_one_snapshot`. SHOULD: `Two_visible_copies_are_two_messages`, `Rereading_copies_accepts_none`, `Third_copy_accepts_one`, golden `repeat-in-capture`. |
| AC-CHAT-11 | REQ-CHAT-21 (SHOULD) | `TextSimilarityTests.OcrKey_folds_confusables`, `OcrKey_keeps_distinct_digits`; `PlayerDirectoryTests.Canonicalize_maps_misread_to_known_name`, `Canonicalize_keeps_unknown`; golden `name-misread`. |
| AC-CHAT-12 | REQ-CHAT-25 | `SystemLinePatternsTests.Invalid_regex_reported_and_skipped`, `Catastrophic_regex_times_out_as_no_match`. |
| AC-CHAT-13 | REQ-CHAT-26, 27 | `ChatPipelineUnderstandingTests.System_lines_hidden_by_default`, `Shown_system_lines_are_untranslated_and_cost_nothing` (`FakeTranslator` records zero requests; `SuggestedReplyLanguage` and `Players` unchanged). |
| AC-CHAT-14 | REQ-CHAT-28 (SHOULD) | `ChatCaptureAnalyzerTests.Line_starting_with_known_player_is_notice`, `Short_names_do_not_trigger_notice`, `Heuristic_off_by_default`; golden `system-known-player`. |
| AC-CHAT-15 | REQ-CHAT-29, 31–33 (SHOULD) | `ChatFormatCatalogTests.Builtin_has_generic_only`, `User_file_adds_and_replaces_profiles`, `Inherits_fills_missing_fields`, `Bad_regex_field_falls_back_with_error`, `Malformed_user_file_keeps_builtins`, `Resolve_auto_uses_game_map_then_generic`, `Resolve_unknown_id_is_generic`, `Profile_system_patterns_apply_for_my_language_and_star`. |
| AC-CHAT-16 | REQ-CHAT-30 (SHOULD) | Every existing `ChatLineParserTests` case passes unchanged; `ChatLineParserTests.Generic_profile_equals_legacy_parse`, `Custom_profile_separator_and_channels`. |
| AC-CHAT-17 | REQ-CHAT-34 (SHOULD) | Smoke setup `chat-understanding` (when REQ-CHAT-32 is built): a user file in the temp `DataFolder` maps the current game to a profile with separator `>`. After `ReloadChatFormats()`, `Analyze(["Bob> hi"])` yields sender `Bob`. |
| AC-CHAT-18 | REQ-CHAT-35, 36 (37–39) | `ChatTextCleanerTests.Removes_invisible_and_replacement_chars`, `Strips_leading_icon_junk_not_brackets`, `Keeps_hangul_compat_jamo`; golden `symbol-junk`. SHOULD: `Folds_fullwidth_ascii`, `Clean_sender_keeps_clan_brackets`, `Symbols_off_still_removes_invisibles`, golden `fullwidth`. |
| AC-CHAT-19 | REQ-CHAT-40 | `tools/typecheck/typecheck.sh` passes: the bindings compile, the resource keys `Danger` and `Text.Caption` are valid, and 01's `check_ui_rules.py` finds no `Click=` in Pages and no `App.Current` in ViewModels. The per-palette smoke section `pages({palette})` navigates `PageIds.Capture` for **every `ThemeManager.PaletteIds` entry** with zero binding errors, and teardown finds `PaletteIds.Count × PageIds.All.Count` settings PNGs. `ChatUnderstandingTextTests.Errors_format_pattern_and_profile_errors` checks the card text, and that it contains no raw regex message. |
| AC-CHAT-20 | REQ-CHAT-41, 42 (SHOULD) | `ChatUnderstandingTextTests.Describes_kinds_and_line_counts`; `CaptureFixtureTests.Write_of_merged_lines_parses`. Manual M-CHAT-6. |
| AC-CHAT-21 | (retired) | REQ-CHAT-43 folded into 44 (see AC-CHAT-22). |
| AC-CHAT-22 | REQ-CHAT-44 | `ChatGoldenTests.Fixture_matches_expected` (a `[Theory]` over every `Golden/Captures/**/*.capture.txt`) passes. `ChatGoldenTests.Corpus_has_required_coverage` asserts that the 14 required files exist. `CaptureFixtureTests.Round_trips_frames_passes_geometry`, `Parse_error_reports_line_number`, `Unknown_set_key_is_rejected`. |
| AC-CHAT-23 | REQ-CHAT-45 (SHOULD) | `ChatPipelineUnderstandingTests.Analyzer_failure_falls_back_to_line_mode`. An `internal` fault hook, reached through `InternalsVisibleTo`, makes `Analyze` throw. |
| AC-CHAT-24 | REQ-CHAT-46 | CI check: 08a's `check_policy.py` network rule passes, with `src/Envoy.Core/{Chat,Ocr,Pipeline}` outside `allowedPaths`. `ChatPrivacyTests.Pipeline_logs_no_chat_text_by_default`: a capturing sink with `IncludeSensitive = false`, run through invalid patterns, a lifecycle cancel and (SHOULD) the analyzer fallback, contains none of the fixture's chat words or names. `ChatSettingsTests.SystemLinePatterns_is_marked_personal` (reflection) and 02's `SettingsRedactionTests.Every_marked_property_is_masked` (unchanged) both pass. If REQ-CHAT-32 is built, 02's `DataInventoryTests.Every_item_is_documented_in_privacy_md` passes with the new `chat-formats.json` item. |
| AC-CHAT-25 | REQ-CHAT-5, 20, 23, 27 (host wiring) | Smoke setup section `chat-understanding` (see Test plan) logs `[smoke] chat-understanding ok elapsed_ms=<n>` and fails the run otherwise. |
| AC-CHAT-26 | REQ-CHAT-5, 23, 27 (in game) | `docs/releasing.md` steps M-CHAT-1 to M-CHAT-3. |
| AC-CHAT-27 | REQ-CHAT-47 | `ChatPipelineLifecycleTests.Caller_token_cancel_mid_translation_still_completes` (the watcher stops mid-translation and the message ends `Translated`); `NewMatch_mid_translation_ends_failed_cancelled`; `Reset_mid_translation_ends_failed_cancelled`; `Dispose_mid_translation_ends_failed_cancelled`; `Translator_OperationCanceled_ends_failed_cancelled`; `Every_pending_message_gets_exactly_one_update` (3 captures × 4 messages with a gated translator, then NewMatch, then release: 12 updates, none `Pending`); `Process_after_dispose_returns_empty`. Each test asserts `MessageUpdated` was raised exactly once per message. Manual M-CHAT-4. |
| AC-CHAT-28 | contract §8.3 (calibration) | `docs/releasing.md` step M-CHAT-5. |

### Definition of done (contract §10)

The contract §10 checklist applies, plus these items specific to this area:

- Every existing test is unchanged and green: `ChatLineParserTests`, `ChatDeduplicatorTests`, `OcrMergerTests`,
  `PipelineFeatureTests` and `ChatPipelineTests`. This absorbs REQ-CHAT-2 and 22.
- `ChatPipelineOptions` becoming a record must not need any test edits. Object initialisers and collection
  expressions compile against `IReadOnlyList<string>`.
- `CHANGELOG.md` and the docs listed under Files touched are updated.
- `docs/chat-formats.md` describes the pattern syntax and how to contribute an anonymised capture.
- Every SHOULD or COULD that was not built is listed under `## Deviations`, together with any setting, control or
  inventory entry it would have added.

## Test plan

All tests live in `tests/Envoy.Core.Tests` and run with `dotnet test`. Add
`<None Include="Golden\Captures\**\*.capture.txt" CopyToOutputDirectory="PreserveNewest" />` to
`Envoy.Core.Tests.csproj`.

### Unit tests to add

| File | Tests |
| --- | --- |
| `ChatAnalyzerTests.cs` (new) | `Joins_continuation_within_gap` (gap 0.3·h); `Gap_too_large_starts_fragment` (2·h); `Height_mismatch_starts_fragment` (2× height); `Stops_at_max_continuations` (5th starts a fragment); `Stitching_off_keeps_one_message_per_line`; `Joins_cjk_without_space` / `Joins_latin_and_hangul_with_space`; `Pattern_with_colon_beats_sender_parse` (`Tribute: …` matched by a pattern is System, not sender "Tribute"); `Leading_lines_are_one_fragment`. SHOULD/COULD: `Stitched_message_has_raw_linecount_and_bounds`, `Unknown_geometry_stitches_adjacent_lines`, `Continuation_of_notice_joins_notice`, `Line_starting_with_known_player_is_notice`, `Short_names_do_not_trigger_notice`, `Heuristic_off_by_default`, `Full_previous_line_demotes_unknown_sender_to_continuation`. |
| `ChatTests.cs` | `ChatDeduplicatorTests.Same_text_from_different_senders_is_two_messages`, `Sender_jitter_is_same_message`, `Channel_and_timestamp_do_not_affect_identity`, `Fragment_contained_in_known_message_is_rejected`, `Fuzzy_fragment_is_rejected`, `Unknown_fragment_is_accepted`, `Concurrent_captures_do_not_double_accept`. SHOULD: the occurrence-counting trio; `ChatLineParserTests.Generic_profile_equals_legacy_parse`, `Custom_profile_separator_and_channels`. |
| `ChatTextCleanerTests.cs` (new) | `Removes_invisible_and_replacement_chars` (ZWSP, BOM, U+FFFD, PUA, VS16); `Strips_leading_icon_junk_not_brackets` (`"■ [All] Bob: hi"` → `"[All] Bob: hi"`; `"[CLAN] Bob: hi"` unchanged); `Keeps_hangul_compat_jamo`. SHOULD: `Folds_fullwidth_ascii`, `Clean_sender_keeps_clan_brackets`, `Symbols_off_still_removes_invisibles`. |
| `SystemLinePatternsTests.cs` (new) | `Wildcard_player_and_regex_forms`; `Matches_with_and_without_timestamp`; `Comments_and_blanks_ignored`; `Invalid_regex_reported_and_skipped`; `Catastrophic_regex_times_out_as_no_match` (`re:(a+)+$` on 40 × `a` + `!` → false in < 1 s); `SplitLines_trims_and_drops_blanks`. |
| `ChatPipelineUnderstandingTests.cs` (new) | `System_lines_hidden_by_default`, `Shown_system_lines_are_untranslated_and_cost_nothing`, `Filtered_messages_are_remembered_for_fragments`, `Wrapped_message_costs_one_request`, `String_overload_matches_legacy_for_sender_lines`, `Analyze_has_no_side_effects` (dedupe, players and events untouched). SHOULD: `Analyzer_failure_falls_back_to_line_mode`. |
| `ChatPipelineLifecycleTests.cs` (new) | The seven AC-CHAT-27 tests, using a `GatedTranslator` (a `TaskCompletionSource` per request that honours the token). |
| `ChatPipelineOptionsTests.cs` (new) | `Snapshot_is_isolated_from_settings`, `Options_swap_mid_capture_uses_one_snapshot`, `From_copies_every_chat_setting`. |
| `ChatPrivacyTests.cs` (new) | `Pipeline_logs_no_chat_text_by_default`; `ChatSettingsTests.SystemLinePatterns_is_marked_personal`. |
| `ChatUnderstandingTextTests.cs` (new) | `Describes_kinds_and_line_counts`, `Errors_format_pattern_and_profile_errors`. |
| `CaptureFixtureTests.cs` (new) | `Round_trips_frames_passes_geometry`, `Parse_error_reports_line_number`, `Unknown_set_key_is_rejected`, `Write_of_merged_lines_parses`. |
| `Golden/ChatGoldenTests.cs` (new) | `Fixture_matches_expected` (`[Theory]`, `MemberData` = file names). For each frame it builds the `OcrPass`es, runs `MergeLines` and then `ProcessAsync`, and compares the `kind \| channel \| sender \| text` list exactly. On failure it prints the fixture name, the frame index and the actual rows, ready to paste. `Corpus_has_required_coverage`. |
| SHOULD only | `ChatFormatCatalogTests.cs` (AC-CHAT-15); `TextSimilarityTests.OcrKey_*`, `PlayerDirectoryTests.Canonicalize_*` (AC-CHAT-11); `OcrMergerTests.MergeLines_carries_left_right_medians` (AC-CHAT-1). |

### Golden fixtures (`tests/Envoy.Core.Tests/Golden/Captures/`)

The fixtures are hand-written, using geometry and OCR quirks typical of Windows OCR on upscaled chat. The chat text is
invented. Notice lines are illustrative text matched by `@pattern`s local to each fixture. They are not claims about
real game strings.

**Required (14, MUST):**

| File | Covers |
| --- | --- |
| `latin-basic.capture.txt` | Single-line messages, `[All]`/`(Team)`/suffix channel, timestamp |
| `latin-wrapped.capture.txt` | A 3-line wrap followed by a new sender |
| `zh-wrapped.capture.txt` | A zh-CN pass with spaces between characters, `：`, a wrap joined without a space, and a 1-character tail kept |
| `ja-wrapped.capture.txt` | Kana and kanji wrap |
| `ko-wrapped.capture.txt` | Hangul wrap joined with a space, and a single-line `ㅋㅋㅋ` |
| `ru-wrapped.capture.txt` | Cyrillic `Иван (Команда): …` wrap |
| `mixed-passes.capture.txt` | en + zh-CN + ko passes on one capture; four speakers; engine choice per row |
| `scroll-up.capture.txt` | 3 frames, each shifted up one line, with one new message at the bottom of each frame |
| `scroll-orphan.capture.txt` | Frame 2: the head has scrolled out and only its tail remains, so nothing is emitted |
| `orphan-unknown.capture.txt` | Frame 1 starts mid-message, so one `fragment` is emitted |
| `geometry-breaks.capture.txt` | A sender-less line 2.5 line heights down, and a taller sender-less line. Neither is joined. |
| `system-patterns.capture.txt` | A wrapped `@pattern` notice is hidden. The `@set hideSystem=false` frame emits `system`. |
| `own-and-muted-wrapped.capture.txt` | Wrapped `@me`/`@muted` messages and their scrolled tails emit nothing |
| `symbol-junk.capture.txt` | Leading `■`, `©`, ZWSP and U+FFFD give clean senders |

**Added with their SHOULDs:** `repeat-in-capture` (19), `name-misread` (21), `system-known-player` (28) and
`fullwidth` (37).

**Real captures (release pass, contract §8.3):** `Golden/Captures/real/*.capture.txt`. These are at least 3
anonymised "Copy as test capture" fixtures from at least 2 titles, added by M-CHAT-5. Real captures are not required
for this step's CI, and the `Theory` runs them once they exist.

### Smoke-test section (`src/Envoy.App/SmokeTest.cs`, setup phase, contract §6)

This section runs once, in setup, on the main host, which already has its temp `DataFolder`:

```csharp
// [smoke] chat-understanding
var sample = OcrLine.FromTexts(["[All] Zhang Wei: this message is long enough that it", "wraps onto a second line", "Kim: ㅋㅋㅋ"], 18, 4);
var entries = host.Pipeline.Analyze(sample);
Require(entries.Count == 2 && entries[0].Text.EndsWith("second line", StringComparison.Ordinal), "stitching");
host.Settings.Chat.SystemLinePatterns = ["*resigned*"]; host.ApplySettings();      // ApplySettings slot 5
Require(host.Pipeline.Analyze(OcrLine.FromTexts(["Bob resigned."], 18, 4)).Single().Kind == ChatLineKind.System, "patterns");
Require(host.Pipeline.Options.HideSystemLines, "hide default");
_ = host.Pipeline.Describe(sample);                                                   // formatter must not throw
host.Settings.Chat.SystemLinePatterns = []; host.ApplySettings();
// REQ-CHAT-32/34 if built: write {DataFolder}\chat-formats.json, ReloadChatFormats(), assert "Bob> hi" → sender Bob.
log.Info("[smoke] chat-understanding ok", LogField.Of("elapsed_ms", sw.ElapsedMilliseconds), LogField.Of("entries", 2));
```

No chat text is logged. The section adds well under a second. The Reading Chat card is exercised by 01's per-palette
`pages({palette})` loop under the binding-error listener. This step adds no new window.

### Manual steps for `docs/releasing.md` (08b gathers them; this step writes them into `docs/chat-formats.md`)

- **M-CHAT-1.** In AoE II DE or AoE IV (English client), get a message long enough to wrap from an AI ally or a second
  account. The overlay shows one entry.
- **M-CHAT-2.** Keep chatting until the wrapped message's first line scrolls out of the region. Nothing new appears.
- **M-CHAT-3.** Add `*resign*` (or the client-language equivalent) to the notice list and resign an AI game. Nothing is
  translated, and the Translation usage counter does not move.
- **M-CHAT-4.** While a translation is in flight (use a slow provider or pull the network), click New match, and
  separately pause capture. No overlay line stays on "translating…".
- **M-CHAT-5 (calibration, before 1.0).** Collect at least 3 anonymised "Copy as test capture" fixtures from at least
  2 titles into `Golden/Captures/real/`, fill in `@expect`, and run `dotnet test`. If any fixture needs different
  `MaxGapFactor`, `HeightTolerance` or `MaxContinuationLines` values, change the defaults (and `generic` if built) in
  the same PR. Then remove "provisional" from REQ-CHAT-5 and `docs/chat-formats.md`.
- **M-CHAT-6 (if REQ-CHAT-41/42 are built).** On the Reading Chat page with a wrapped message visible, "Understood as"
  shows `(2 lines)`. "Copy as test capture" puts text on the clipboard that `CaptureFixture.Parse` accepts.

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Ocr/OcrResults.cs` | `OcrLine.HasGeometry`, `FromTexts`; SHOULD: `Left`/`Right` medians in `MergeLines` | (05 owns `MergeLines`) |
| `src/Envoy.Core/Ocr/CaptureFixture.cs` | new | |
| `src/Envoy.Core/Chat/ChatMessage.cs` | `ChatLineKind`, `Kind`/`LineCount`/`Top`/`Bottom` init properties | |
| `src/Envoy.Core/Chat/ChatTextCleaner.cs`, `SystemLinePatterns.cs`, `ChatCaptureAnalyzer.cs`, `ChatUnderstandingText.cs` | new | |
| `src/Envoy.Core/Chat/ChatDeduplicator.cs` | `AcceptNew`, sender-aware keys, fragment containment; SHOULD: occurrence counts | |
| `src/Envoy.Core/Chat/TextSimilarity.cs` | `ContainsFuzzy`; SHOULD: `OcrKey` | |
| `src/Envoy.Core/Chat/PlayerDirectory.cs` | SHOULD: `KnownNames`, `Canonicalize` | |
| `src/Envoy.Core/Chat/ChatLineParser.cs`, `ChatFormatProfile.cs`, `ChatFormatCatalog.cs`, `chat-formats.json` | SHOULD: profile-driven parsing | |
| `src/Envoy.Core/Envoy.Core.csproj` | SHOULD: `<EmbeddedResource Include="Chat\chat-formats.json" LogicalName="Envoy.Core.Chat.chat-formats.json" />` | |
| `src/Envoy.Core/Pipeline/ChatPipeline.cs` | immutable options record + `From`, volatile state, geometry path, `Analyze`/`Describe`, system handling, lifecycle CTS, `IDisposable`, `ILog` | |
| `src/Envoy.Core/Pipeline/IncomingMessage.cs` | `WasCancelled` | |
| `src/Envoy.Core/Pipeline/PipelineText.cs` | new: `Cancelled` | |
| `src/Envoy.Core/Settings/AppSettings.cs` | `Chat` property and `ChatSettings` with `[Personal]` on `SystemLinePatterns` | **yes** |
| `src/Envoy.Core/Diagnostics/DataInventory.cs` | SHOULD (with REQ-CHAT-32): `chat-formats.json` item, "Your own chat format profiles (you create this file)." | **yes** (02) |
| `src/Envoy.App/AppHost.cs` | constructor slots 5–6, `ApplySettings` slot 5, `RefreshGame` slot 2, `Pipeline.Dispose()`; SHOULD: `ChatFormatsPath`, `ChatFormats`, `ReloadChatFormats()` | **yes** |
| `src/Envoy.App/Pages/ReadingChatPage.xaml` | "Understanding chat" card; SHOULD: Live view understood text and copy button | **yes** (01, 05) |
| `src/Envoy.App/ViewModels/MainViewModel.ReadingChat.cs` | `SystemLinePatternsText`, `ChatUnderstandingStatus`, `HasChatUnderstandingErrors`; SHOULD: `ChatFormatOptions` | **yes** (01) |
| `src/Envoy.App/ViewModels/CapturePreviewViewModel.cs`, `MainViewModel.cs` | SHOULD: `UnderstoodText`, `CopyAsTestCaptureCommand`, `CopyCaption`, optional `copyText` constructor argument | **yes** (01, 05, 07a) |
| `src/Envoy.App/Windows/OverlayItem.cs` | `IsFailed` excludes `WasCancelled` | (07c later) |
| `src/Envoy.App/Services/OcrService.cs` | SHOULD: `Left`/`Right` | (05) |
| `src/Envoy.App/SmokeTest.cs` | setup section `chat-understanding` | **yes** |
| `tests/Envoy.Core.Tests/*.cs`, `Golden/**`, `Envoy.Core.Tests.csproj` | new tests, harness, 14 fixtures, copy-to-output | |
| `docs/chat-formats.md` | new: pattern syntax, profiles (if built), contributing anonymised captures, M-CHAT steps | |
| `docs/privacy.md` | SHOULD (with REQ-CHAT-32): `chat-formats.json` line; note that fixtures copied from the Live view contain chat | **yes** (02, 07a) |
| `docs/architecture.md` | flow: `MergeLines` → analyzer → dedupe → lifecycle; `Chat/` table rows | |
| `docs/troubleshooting.md` | "Messages are split / notices are translated" section pointing at the card | |
| `CHANGELOG.md` | user-facing entry under `## Unreleased` | |
| `docs/specs/04-chat-understanding.md` | `Status: Implemented` and `## Deviations` at the end of the step | |

Not touched: `ChatWatcher.cs`, `App.xaml.cs`, `IShell.cs`, `Native.cs`, `tools/ci/policy.json`, `build.yml`,
`OverlayWindow.xaml`, `ComposeWindow*`, `LookupWindow*`, palettes and `Controls.xaml`.

## Risks & open questions

- **Wrong-speaker stitching.** If OCR drops a sender's colon (for example reading it as `;` or `.`), that line is
  stitched into the previous speaker's message. The mitigations are the geometry and height checks, the
  4-continuation cap, the "Join wrapped lines" toggle, and REQ-CHAT-13 if it is built. The real-capture corpus from
  M-CHAT-5 is the main check.
- **Provisional thresholds.** `MaxGapFactor` 1.0, `HeightTolerance` 0.5 and the 4-line cap are guesses, not
  measurements. The golden suite checks the implementation against its author's expectations, not against real games,
  until M-CHAT-5 adds real captures. Contract §8.3 makes that a pre-1.0 release step.
- **"Copy as test capture" is SHOULD, but M-CHAT-5 relies on it.** If REQ-CHAT-42 is not built, release fixtures have
  to be typed by hand from screenshots, which is slow and loses real geometry. We strongly recommend building 42 in this
  step. It is small: one command plus the already-required `CaptureFixture.Write`.
- **Colons inside wrapped text** (`…we push at min 5: go`) can still read as a new sender `we push at min 5` unless
  REQ-CHAT-13 lands. The 32-character sender cap limits this. We accept it for 1.0.
- **Region change cancels in-flight translations.** Contract §2.6 has `Reset()` cancel the lifetime token, and
  `Reset()` also runs on region selection. A user who re-selects the region mid-game sees in-flight lines end as the
  original text. This is acceptable: it is rare and nothing gets stuck.
- **Depends on 05's geometry overload.** This step assumes REQ-CAP-42 made the watcher call
  `ProcessAsync(IReadOnlyList<OcrLine>, CancellationToken.None)`. If 05 converted the lines to strings at the call
  site, stitching silently falls back to unknown geometry. AC-CHAT-25 does not catch this, because it calls `Analyze`
  directly. Mitigation: the implementer checks the call site first. If 05 deviated, that is a 05 deviation, fixed in a
  separate one-line PR before this step starts.
- **System heuristic false positives.** With REQ-CHAT-28 on, a chat continuation that starts with a known player's
  name ("Bob will boom") is hidden as a notice. Hiding real chat is worse than translating a notice, so the default is
  now **off**.
- **No built-in notice patterns.** We do not ship guessed game strings, so out of the box nothing is classified as a
  notice. Open question for after 1.0: should the repo's `chat-formats.json` carry verified per-language patterns,
  reviewed with screenshots?
- **Short repeated messages across time.** A `gg` repeated after the first copy has left the screen, but within the
  3-minute memory, is still suppressed, as today. Occurrence counting (SHOULD) only fixes copies that are visible at
  the same time.
- **Truncated head message.** If the region cuts off a message's last line, the emitted message is truncated. The prefix
  rule suppresses the later full read, and nothing is updated or re-translated (non-goal).
- **Canonicalisation collisions** (SHOULD 21). Two real players whose names fold to the same `OcrKey` (for example
  `Barn` and `Bam`) display as one name. This is rare, and the folding table is deliberately small.
- **Longer requests.** Stitching sends fewer, longer requests to 03's stack, which helps its throttle. A 5-line
  message (at most a few hundred characters) is far below every provider's request limit. If 03 finds otherwise, it
  records the finding under its Deviations. 04 does not split messages.

## Revision notes

Changes from the pre-review draft, made to comply with `00-integration.md` and the set critique:

- **MUST budget (contract §9).** There are now 17 MUSTs: CHAT-5, 7, 9, 15, 16, 18, 20, 23, 25, 26, 27, 35, 36, 40, 44,
  46 and 47 (the draft had 35).
  - REQ-CHAT-1 and 3 moved to 05 (REQ-CAP-42). REQ-CHAT-2 and 22 are now DoD items.
  - Folded: 6 and 11 into 5, 17 into 16, 24 into 23, and 43 into 44.
  - Demoted to SHOULD: 10, 12, 29, 30, 31, 32, 33 and 39, as the contract lists.
  - REQ-CHAT-8 is SHOULD, because the contract does not list it as MUST.
  - REQ-CHAT-14 (the fragment definition) is folded into 15, because 15 cannot be tested without it. The MUST count is
    unchanged.
  - REQ-CHAT-47 (the pending lifecycle) is new.
- **Critique major (04/05 watcher and preview conflict, contract §2.7, §2.8).** 04 no longer edits `ChatWatcher`,
  `CaptureFrame` or `MergeLines`. The `Understood` field is gone. "Understood as" is computed on the UI thread by
  `Pipeline.Describe(frame.Lines)`, which has no side effects, inside `CapturePreviewViewModel`. The copy command lives
  there too and uses `IShell.CopyText`. The page code-behind only toggles `IsActive`. `Preview()` is renamed
  `Analyze()`/`Describe()` to match the contract.
- **Critique major (pending messages stuck on "translating…", contract §2.6).** REQ-CHAT-47 adds:
  - a pipeline-lifetime CTS, cancelled by `NewMatch`, `Reset` and `Dispose`;
  - a `ProcessAsync` token that cancels analysis only;
  - every `OperationCanceledException` ending as `Failed("cancelled")` with exactly one `MessageUpdated`;
  - an overlay that shows the original line with no spinner and no error row.

  The three tests the contract requires are among the seven lifecycle tests.
- **Critique minor (invented thresholds and fixtures, contract §8.3).** The REQ-CHAT-5 thresholds are marked
  provisional. M-CHAT-5 adds the pre-1.0 calibration step with at least 3 real anonymised captures from 2 titles, and
  the golden harness picks up `Golden/Captures/real/`. The "Bob will boom" heuristic is now off by default.
- **Critique minor (`[Personal]` on `SystemLinePatterns`, contract §7).** It is now an unconditional MUST under
  REQ-CHAT-46, with a reflection test.
- **Critique minor (themes hard-coded in ACs, contract §6).** AC-CHAT-19 now names every `ThemeManager.PaletteIds`
  entry and the `PaletteIds.Count × PageIds.All.Count` screenshot count.
- **Contract §7 (thread safety).** `ChatPipelineOptions` is now an immutable record built by `From(settings, format)`
  with copied collections and published through `Volatile.Write`. `ApplySettings` slot 5 replaces the seven
  per-property copies. A snapshot-isolation test is added. This is folded into REQ-CHAT-20 (concurrency).
- **Contract §3 (instance paths and order).** `ChatFormatsPath` is an instance property on `DataFolder`. The changes
  are placed in constructor slots 5–6, `ApplySettings` slot 5 and `RefreshGame` slot 2, and `Pipeline.Dispose()` runs
  after the watcher stops. There are no `IShell` or `AppHostOptions` additions.
- **Contract §8.2 (data inventory).** `chat-formats.json` joins `DataInventory` and `docs/privacy.md` whenever
  REQ-CHAT-32 is built. Envoy never writes that file.
- **Contract §8.7 (strings).** The UI text lives in Core `ChatUnderstandingText` and `PipelineText`, in the contract's
  voice. Raw regex and JSON error messages are logged as sensitive fields and never shown.
- **Contract §8.8 (accessibility).** Every new card control has `AutomationProperties.Name`.
- **Logging (02).** Logging uses `ILog` category `Chat`, and chat text, patterns and names go only through
  `LogField.Sensitive`. `ChatPrivacyTests` verifies this. The draft's "if 02 has landed" conditions are removed.
- **Verifiable MUSTs (contract §9).** The draft's "review/grep" AC for REQ-CHAT-46 is now the `check_policy.py` CI
  check plus unit tests. Manual checks are numbered M-CHAT-1 to 6 for 08b's release pass. The smoke section has the
  `[smoke] chat-understanding ok elapsed_ms=<n>` form, runs once in setup, logs no chat text, and also checks the
  `ApplySettings` wiring.
- **Golden suite trimmed.** There are 14 required fixtures covering the MUST behaviour (gap-break and height-mismatch
  are merged into `geometry-breaks`). SHOULD fixtures arrive with their features, and an unknown `@set` key fails
  parsing, so an unbuilt feature can never pass vacuously.
- **Allowlists (contract §5).** No P/Invoke, network or launch additions. `policy.json` is unchanged.

## Deviations

Every MUST is built. Every SHOULD is built: REQ-CHAT-4, 8, 10, 12, 19, 21, 28–34, 37–39, 41, 42 and 45. The one COULD
is not.

**Not built**

- **REQ-CHAT-13 (COULD)**, demoting an unknown sender when the previous line is full width. `Left`/`Right` are now
  collected (REQ-CHAT-4), so it can land later with AC-CHAT-7; it adds no setting or control.

**Existing tests touched** (contract §10.1)

- 05's `OcrMergerTests.MergeLines_left_right_reserved_zero` asserted that `Left`/`Right` stay 0. REQ-CHAT-4 fills them
  by design, so it is replaced by `MergeLines_carries_left_right_medians` (AC-CHAT-1).
- `ChatLineParserTests`, `ChatDeduplicatorTests` and `PlayerDirectoryTests` became `partial` so the new tests carry the
  class names this spec uses. No existing test body changed.

**Design details that differ from the text above**

- **Fragments are joinable.** Algorithm step 3 lists chat and system entries; REQ-CHAT-15 needs consecutive orphan
  lines to form one fragment, so a fragment takes continuations under the same geometry test and limit. A candidate
  contributes its parsed text (a timestamp or known channel tag is removed); a lone channel tag (`[All]`) is skipped.
  System and fragment entries have no channel. A system entry's text has its leading timestamp removed.
- **`Raw`** is the cleaned source lines (also for a single line), not the untrimmed OCR text.
- **`TextSimilarity.ContainsFuzzy`** uses approximate substring matching (Sellers' algorithm: the needle's smallest
  edit distance to any stretch of the haystack, similarity = 1 − edits ÷ needle length) instead of sliding windows of
  the needle's length ±1. Same intent, O(haystack × needle) per memory entry, so a capture stays well under 1 ms.
- **Dedupe memory.** An empty-after-normalisation message is ignored (as `TryAccept` did). A suppressed fragment is not
  remembered. An entry created by a fragment adopts the sender of a later matching message. Eviction drops the oldest
  `SeenAt` beyond `Capacity`.
- **REQ-CHAT-28** compares a known name by `OcrKey` against the line's prefix of the same length, so a misread that
  changes the name's length is not matched.
- **REQ-CHAT-45.** The fallback parses each raw line with `ChatLineParser.Parse`. `ChatPipeline.Analyze` (and so
  `Describe`) falls back the same way without logging, so the Live view never throws.
- **Logging.** Format-file problems are logged by `ChatFormatCatalog.LoadWithUserFile`, which AppHost gives
  `Log.For("Chat")`, not by AppHost itself. Pattern errors are logged (Info, one entry per error, pattern and error as
  sensitive fields) only when the pattern list changed. A cancelled translation is not logged as "Message translation
  failed" by AppHost.
- **`CaptureFixture`.** An `@set` may appear inside a frame and applies from that frame on (the `system-patterns`
  fixture needs it), so the records gain `Settings` at fixture and frame level, plus `Language`, `Row`, `SettingKeys`,
  `DirectiveNames` and `Kinds`. Directive lines have no inline `# …` comments (the annotations in this spec's example
  are explanations); `@pattern` must come before the first `@frame`. An empty `@expect` equals `(none)`.
- **`ChatUnderstandingText`** gains `ChatEntryNoChannel` and `ChatEntryNoSender` (whole-sentence templates rather than
  cutting `[{channel}] ` out of `ChatEntry`), `FormatsSettingInvalid`, `FormatsProfileWithoutId`, `FormatsProfileInvalid` (errors without a profile or
  field), `MatchGameFormat` (the "Chat format" list's first entry, built in code) and `CopyFailedCaption`
  (`IShell.CopyText` returns false). `Describe` starts with `UnderstoodHeader`.
- **`CapturePreviewViewModel`** takes `Func<string, bool>? copyText` (`IShell.CopyText` returns `bool`) and exposes
  `CanCopy`, which hides the button for a view model without one.
- **Format profiles.** Validation also covers `heightTolerance` (0–5), `maxSenderLength` (1–64), brackets (two-character
  pairs), separators (1–8 characters, so a word such as `" says "` works), and channel keys (the four canonical names).
  A user file over 256 KiB is treated as unreadable. A profile without `inherits` takes missing fields from `generic`.
  A profile's notice patterns that don't compile are dropped and reported as its `systemPatterns` field. Additions:
  `ChatFormatCatalog.Find`, `ChatFormatProfile.SystemPatternsFor` and `CanonicalChannels`. `ReloadChatFormats()` has no
  button; the file is read at start-up (the smoke test calls the method).
- **Smoke.** The setup section `chat-understanding` uses the standard `Section` line (no `entries` field) and also
  checks pattern errors and the line-count text. A first-palette MainWindow check `chat-card({palette})` sets an invalid
  pattern through the view model and asserts the card's Danger line appears and clears. 05's capture-engine section
  now disposes its pipeline.
- **Golden corpus.** All 18 fixtures (the 14 required plus the four SHOULD ones) are hand-written. The `fullwidth`
  fixture avoids a space between full-width letters, because 05's `OcrText.Clean` (inside `MergeLines`) treats
  full-width forms as CJK and removes that space (`ｇｇ ｗｐ` → `ggwp`). That is outside this area; it is noted for
  the M-CHAT-5 calibration.
- **Docs.** M-CHAT-1 to 6 are in `docs/chat-formats.md` and also in `docs/releasing.md` › Manual checks, next to the
  other areas' steps. `CLAUDE.md` gains a chat-understanding rule.
