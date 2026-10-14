# Chat understanding: wrapped lines, game notices, chat format profiles and stable message identity

Status: Proposed

Area id: `04-chat-understanding` · Requirement prefix: `CHAT` · Depends on: nothing hard. It merges cleanly after
`01-ui-foundation` (pages split into `src/Envoy.App/Pages/`) and uses `02-reliability-observability`'s `ILog` and
`[Personal]` if they have landed. Both are optional; see "Files touched".

## Context

### What exists today

| Piece | Behaviour today | Where |
| --- | --- | --- |
| OCR line geometry | `OcrService.RecognizeAsync` builds `OcrLine(Text, Top, Bottom)` from word rectangles. `OcrMerger.Merge` groups lines into rows by vertical centre, keeps the best engine's text per row, and **returns `IReadOnlyList<string>`**. Geometry is lost at this point. | `src/Envoy.App/Services/OcrService.cs`, `src/Envoy.Core/Ocr/OcrResults.cs` |
| Watcher hand-off | `ChatWatcher.TickAsync` calls `OcrMerger.Merge(...)` and then calls `pipeline.ProcessAsync(lines, ct)` without awaiting it, so two captures can be processed at the same time. `CaptureFrame(Image, Lines: IReadOnlyList<string>, OcrTime)` feeds the Live view. | `src/Envoy.App/Services/ChatWatcher.cs` |
| Parsing | `ChatLineParser.Parse(string)` handles one line. It strips a `12:04` / `[12:04:33]` timestamp, then a bracketed channel prefix (`[All]`, `(Team)`, `<…>`) taken from a hard-coded `KnownChannels` set in about 10 languages. It then takes the first `:`/`：` within 33 characters as the sender separator, and a `Name (Team):` suffix channel. Lines it does not recognise become sender-less text. | `src/Envoy.Core/Chat/ChatLineParser.cs` |
| Message model | `record ChatMessage(Raw, Channel, Sender, Text)`. | `src/Envoy.Core/Chat/ChatMessage.cs` |
| Pipeline | `ChatPipeline.ProcessAsync(IEnumerable<string>)` treats **each OCR line as one message**: parse, then `IsWorthShowing` (MinCharacters, needs a letter, not me, not muted, channel filter), then `ChatDeduplicator.TryAccept(chat.Raw)`, then classify and translate. | `src/Envoy.Core/Pipeline/ChatPipeline.cs` |
| De-duplication | `TryAccept(string)` normalises the **raw line**, including timestamp and channel. Two reads are the same if they are equal, one is a prefix of the other (6+ characters), or `TextSimilarity.Similarity ≥ 0.8`. Memory is 3 minutes and 200 entries. | `src/Envoy.Core/Chat/ChatDeduplicator.cs`, `TextSimilarity.cs` |
| Player names | `PlayerDirectory` matches names loosely (similarity ≥ 0.8) for language memory. Overlay, transcript and dedupe all use the sender **exactly as OCR read it** in each frame. | `src/Envoy.Core/Chat/PlayerDirectory.cs` |
| Settings | Only `Capture.MinCharacters`, `Channels` and `MutedPlayers` affect chat interpretation. | `src/Envoy.Core/Settings/AppSettings.cs` |
| Tests | `ChatLineParserTests` (single-line cases), `ChatDeduplicatorTests`, `OcrMergerTests`, `PipelineFeatureTests`. Nothing covers more than one line of context. | `tests/Envoy.Core.Tests/ChatTests.cs`, `OcrTests.cs`, `PlayerAndUpdateTests.cs` |

### What is weak or missing for production

1. **Wrapped messages are split.** A long message wraps onto continuation lines that have no sender. Each
   continuation is translated as a separate, sender-less message, so the overlay shows half-sentences, and every
   wrapped message costs 2–3 provider requests. A short tail such as `它` falls under `MinCharacters` and is lost.
2. **Your own and muted players' wrapped lines leak.** The own-name and mute filters see only the first line, so the
   continuation of your own message is translated back to you, and a muted player's tail still shows.
3. **Game notices are treated as chat.** Notices such as resignations, victory and age-up messages, or "player
   joined" lines, have no sender. They are translated, which wastes characters and clutters the overlay. They are also
   in the user's own client language, so translating them is pointless. There is no way to hide them.
4. **Scrolling breaks identity.** When chat scrolls, a message's head line can leave the region while its tail stays.
   The tail is then "new" and gets translated as a fragment. The dedupe key includes the timestamp and channel, and
   two players typing the same `gg` are told apart only by accident of string similarity.
5. **Repeated messages vanish.** A player who writes `gg` twice in a row shows once, because the second read is a
   "duplicate" of the first.
6. **OCR name jitter.** `Zhang Wei` in one frame and `Zhanq Wei` in the next are shown as two different speakers in
   the overlay and transcript.
7. **Icon junk.** Rank, civilisation or platform icons next to names are read as stray glyphs (`©`, `■`, `|`,
   U+FFFD, private-use characters or zero-width characters). They become part of the sender name, which breaks the
   own-name, mute and dedupe matching.
8. **Formats are hard-coded.** Channel words, separators and the timestamp shape are compiled into
   `ChatLineParser`. Supporting a game or locale that differs means a code change. Users cannot fix it themselves.
9. **No regression corpus.** No test feeds a realistic multi-line, multi-frame capture through OCR merging and the
   pipeline, so heuristics can regress silently.

## Goals / Non-goals

**Goals**

- Keep line geometry from OCR merge through to the pipeline.
- Join continuation lines to their message (stitching), using parse results plus geometry.
- Classify game notices ("system lines") with a generic heuristic and a user-editable pattern list. Notices are
  never translated and are hidden by default.
- Data-driven chat format profiles (embedded JSON, user-overridable), with a `generic` profile that reproduces today's
  parser exactly.
- Clean up OCR symbol junk before parsing.
- Give each message one identity across frames and scrolling: dedupe keyed on (sender, text), with occurrence
  counting, fragment suppression and canonical sender names.
- A golden-test corpus of multi-line, multi-frame captures in a text format with geometry. Envoy can export the
  current Live view frame in that format so users can contribute real captures.
- Small UI: one settings card on Reading Chat and an "Understood as" Live view.

**Non-goals**

- Game-specific facts we cannot verify here, such as exact notice strings, chat colours or per-game channel words.
  Built-in profiles ship `generic` only, and per-game mappings are empty. Users and contributors can add profiles.
- Colour-based or pixel-based line classification. That belongs to `capture-engine`, if anywhere.
- Re-translating a message whose text grows after it was emitted (for example when the region cut off its last line).
  This is listed as a risk.
- Changes to `LookupWindow` (Scan & translate). It still joins all lines with newlines.
- Changes to the translation stack, the overlay layout or the transcript format. A system line, when shown, renders
  like a sender-less message.
- Any change to how pixels are captured or preprocessed.

## Requirements

| Id | Priority | Requirement |
| --- | --- | --- |
| **Geometry** | | |
| REQ-CHAT-1 | MUST | `OcrMerger.MergeLines(passes, preferredLanguage)` returns one `OcrLine` per row, ordered top to bottom, carrying the picked text and the row's median `Top`/`Bottom` across the engines that read it. |
| REQ-CHAT-2 | MUST | `OcrMerger.Merge` keeps its exact current output (`MergeLines(...).Select(l => l.Text)`), so `LookupWindow` and the existing tests are unchanged. |
| REQ-CHAT-3 | MUST | `ChatWatcher` passes `IReadOnlyList<OcrLine>` (with geometry) to `ChatPipeline.ProcessAsync`. |
| REQ-CHAT-4 | SHOULD | `OcrLine` gains optional `Left`/`Right` (default `0` = unknown), filled by `OcrService` from word rectangles and carried through `MergeLines` as the row's median. |
| **Stitching** | | |
| REQ-CHAT-5 | MUST | A line that is not a system line and has no sender, and that follows a chat head (or that head's continuation) in the same capture, is joined into that message when the vertical gap is at most `MaxGapFactor` × the capture's median line height (default `1.0`) and its height is within `HeightTolerance` × median of the previous line's height (default `0.5`). |
| REQ-CHAT-6 | MUST | At most `MaxContinuationLines` (default `4`) continuations join one head. Further lines start a new fragment. |
| REQ-CHAT-7 | MUST | Joined text uses no separator when the character before the join or the first character after it is Han, Kana or CJK/full-width punctuation. Otherwise it uses one space. |
| REQ-CHAT-8 | MUST | A stitched message has `Raw` = the cleaned source lines joined with `\n`, `LineCount` = number of lines, and `Top`/`Bottom` from its first and last lines. |
| REQ-CHAT-9 | MUST | The own-name, muted-player, channel and `MinCharacters` filters judge the stitched message as a whole, so no continuation of a dropped message is ever emitted. |
| REQ-CHAT-10 | MUST | When every line has zero height (geometry unknown, for example the `IEnumerable<string>` overload), stitching joins adjacent lines on parse results alone. |
| REQ-CHAT-11 | MUST | With `Chat.StitchWrappedLines = false`, each line is its own message (today's behaviour). |
| REQ-CHAT-12 | MUST | A continuation that follows a system head joins that system entry. It does not become a chat fragment. |
| REQ-CHAT-13 | COULD | When `Left`/`Right` are known, a line that parses with a sender is treated as a continuation instead if the sender is not a known player and the previous line's `Right` is within 6 % of the capture's widest `Right`. This is the "the previous line was full, so this one wrapped" rescue for colons inside wrapped text. |
| **Fragments and identity** | | |
| REQ-CHAT-14 | MUST | Sender-less, non-system lines with no head before them in the capture (at the top, after a geometry break, or past the continuation limit) form a `Fragment` entry. Consecutive such lines join into one fragment. |
| REQ-CHAT-15 | MUST | A fragment whose normalised text (2+ characters) is contained, exactly or fuzzily (window similarity ≥ dedupe `Threshold`), in a remembered message is suppressed. Otherwise it is emitted as a sender-less message. |
| REQ-CHAT-16 | MUST | De-duplication is keyed on (sender, text). Channel, timestamp and geometry are ignored. Two reads are the same message when their texts match (equal, a 6+ character prefix, or similarity ≥ `Threshold`) **and** either sender is null or the senders' similarity is ≥ `SenderThreshold` (default `0.75`). |
| REQ-CHAT-17 | MUST | The same text from two clearly different senders (`A: gg`, `B: gg`) produces two messages. |
| REQ-CHAT-18 | MUST | Messages dropped by filters (own, muted, channel, hidden system) are still recorded in dedupe memory as seen, so their scrolled-off fragments are suppressed later. |
| REQ-CHAT-19 | SHOULD | Occurrence counting: if one capture holds N reads of the same message key and the most seen at once before was M < N, then N − M of them are accepted as new. Two visible `gg` lines from one player give two messages, and re-reading them gives none. |
| REQ-CHAT-20 | MUST | `ChatDeduplicator.AcceptNew(capture)` decides a whole capture atomically under its lock, so concurrent `ProcessAsync` calls cannot double-accept. |
| REQ-CHAT-21 | SHOULD | Before dedupe and emission, a sender is replaced by the display name of a known player whose `TextSimilarity.OcrKey` is equal (OCR-confusable folding). Different names that only look similar (`Player1`/`Player2`) are never merged. |
| REQ-CHAT-22 | MUST | `ChatDeduplicator.TryAccept(string)` keeps its current results for all existing tests. |
| **System lines** | | |
| REQ-CHAT-23 | MUST | User patterns (`Chat.SystemLinePatterns`) match the whole cleaned line, with and without a leading timestamp, case-insensitively. `*` matches any run, `{player}` matches 1–32 characters, a `re:` prefix means a raw .NET regex, and blank lines or `#` comments are ignored. A matched line is a system head. |
| REQ-CHAT-24 | MUST | System patterns are tested before sender parsing, so a notice that contains a colon is not mistaken for chat. |
| REQ-CHAT-25 | MUST | An invalid pattern is reported as a `PatternError(Index, Pattern, Message)` and skipped. A regex timeout (50 ms) counts as no match. Neither throws. |
| REQ-CHAT-26 | MUST | System entries are never sent to the translator and never update `PlayerDirectory`, `LastOpponentLanguage` or `SuggestedReplyLanguage`. |
| REQ-CHAT-27 | MUST | With `Chat.HideSystemLines = true` (default) system entries are not emitted. With `false` they are emitted with `Kind = System`, `Status = Untranslated` and `SourceLanguage = MyLanguage`. |
| REQ-CHAT-28 | SHOULD | With `Chat.DetectSystemLinesByPlayerName = true` (default), a sender-less line that starts with a known player name (`MyPlayerName` or a `PlayerDirectory` name of 3+ normalised characters, compared by `OcrKey`) followed by whitespace is a system head. |
| REQ-CHAT-29 | MUST | A profile's `systemPatterns` is keyed by language code or `*`. The patterns for `MyLanguage` and for `*` are added to the user's list. The built-in `generic` profile ships with none. |
| **Format profiles** | | |
| REQ-CHAT-30 | MUST | Chat formats load from an embedded `chat-formats.json` holding a `generic` profile. Parsing with `generic` gives results identical to today's `ChatLineParser` for every existing `ChatLineParserTests` case. |
| REQ-CHAT-31 | MUST | A profile defines channel aliases per canonical channel (`All`, `Team`, `Observers`, `Private`), bracket pairs, sender separators, a timestamp regex, `maxSenderLength` and stitching options. It may `inherit` another profile and override fields. |
| REQ-CHAT-32 | MUST | `%APPDATA%\Envoy\chat-formats.json`, when present, adds or replaces profiles by id and can map game ids to profiles. A malformed file or field leaves the built-ins in force, records errors in `ChatFormatCatalog.Errors`, and never throws. |
| REQ-CHAT-33 | MUST | `ChatFormatCatalog.Resolve(setting, gameId)` returns the profile named by the setting. `auto` uses the game mapping, and anything unknown falls back to `generic`. |
| REQ-CHAT-34 | SHOULD | The active profile is re-resolved when settings apply and when the detected game changes. |
| **Cleanup** | | |
| REQ-CHAT-35 | MUST | `ChatTextCleaner.Clean` removes control and format characters (including zero-width characters and BOM), U+FFFD, private-use characters, variation selectors and lone surrogates, collapses runs of whitespace, and trims. |
| REQ-CHAT-36 | MUST | Clean strips a leading run of icon junk (Unicode symbol categories `So`/`Sm`/`Sk`/`Sc`, and `•·»›■□●○◆◇▶►|*#~`) that is followed by whitespace before the channel or sender. Opening brackets are never stripped. |
| REQ-CHAT-37 | SHOULD | Clean folds full-width ASCII letters and digits (U+FF10–FF19, FF21–FF3A, FF41–FF5A) to ASCII. It MUST NOT apply NFKC or NFKD, so Hangul compatibility jamo (`ㅋㅋ`, `ㄱㄱ`) stay unchanged. |
| REQ-CHAT-38 | SHOULD | Sender names are trimmed of surrounding punctuation other than bracket pairs, so clan tags survive. |
| REQ-CHAT-39 | MUST | `Chat.CleanSymbols = false` skips REQ-CHAT-36 to 38 but still removes invisible characters (REQ-CHAT-35). |
| **UI and tooling** | | |
| REQ-CHAT-40 | MUST | Reading Chat has an "Understanding chat" card that binds format profile, stitching, hide notices, name heuristic, pattern list and symbol cleanup. Pattern and profile errors show on the card in `Danger` colour. |
| REQ-CHAT-41 | SHOULD | The Live view shows "Understood as" next to the raw OCR lines, using `ChatCaptureFormatter.Describe`. Each entry shows its kind, channel, sender, text and joined-line count. |
| REQ-CHAT-42 | SHOULD | The Live view has a "Copy as test capture" button. It copies the current frame's merged lines in `CaptureFixture` format to the clipboard. It only runs when the user clicks it, and nothing is sent anywhere. |
| REQ-CHAT-43 | MUST | `CaptureFixture.Parse(Write(x))` round-trips frames, passes, geometry and directives. |
| **Golden suite and robustness** | | |
| REQ-CHAT-44 | MUST | A golden suite of at least 16 fixture files runs each frame through `OcrMerger.MergeLines` and `ChatPipeline.ProcessAsync` (fake translator, fake clock) and compares the emitted messages exactly. It covers Latin, Chinese, Japanese, Korean, Cyrillic, mixed-engine passes, wrapped, scrolled, orphan, gap break, system, own/muted wrapped, repeat, name misread and symbol junk. |
| REQ-CHAT-45 | SHOULD | If capture analysis throws unexpectedly, the pipeline falls back to one-line-per-message parsing for that capture, records the error (`LastAnalysisError`, and `ILog` if present), and keeps running. |
| REQ-CHAT-46 | MUST | Profiles, patterns and fixtures are local files. Nothing in this area does network I/O, and chat text is not logged unless 02's `LogChatText` is on. |

## Design

### Overview of the new flow

```
OcrPass[] ──OcrMerger.MergeLines──► OcrLine[] (text + Top/Bottom[/Left/Right])
   │
   ▼  ChatPipeline.ProcessAsync(IReadOnlyList<OcrLine>)
ChatCaptureAnalyzer.Analyze(lines, ChatContext)        ← pure, stateless
   per line: ChatTextCleaner.Clean → system patterns? → ChatLineParser.Parse(profile) → name heuristic
   walk: heads (Chat/System) collect continuations by geometry; orphans become Fragments
   ▼  ChatMessage[] (Kind, LineCount, Top, Bottom)
PlayerDirectory.Canonicalize(sender)
   ▼
ChatDeduplicator.AcceptNew(all entries)   ← atomic; occurrence counts; fragment containment; filtered ones remembered
   ▼
emit = accepted ∩ ShouldEmit (system hidden?, IsWorthShowing, not me, not muted, channel filter)
   ▼
Classify / translate (system → Untranslated, no request)
```

### `src/Envoy.Core/Ocr/OcrResults.cs` (changed)

```csharp
public sealed record OcrLine(string Text, double Top, double Bottom, double Left = 0, double Right = 0)
{
    public double Center => (Top + Bottom) / 2;
    public double Height => Bottom - Top;
    public bool HasGeometry => Bottom > Top;

    /// <summary>Uniform synthetic geometry (lineHeight, gap) for callers and tests that only have strings.</summary>
    public static IReadOnlyList<OcrLine> FromTexts(IEnumerable<string> texts, double lineHeight = 0, double gap = 0);
}

public static class OcrMerger
{
    /// <summary>One line per row, top to bottom; text from the best engine, Top/Bottom/Left/Right = median of the row.</summary>
    public static IReadOnlyList<OcrLine> MergeLines(IReadOnlyList<OcrPass> passes, string preferredLanguage);

    /// <summary>Unchanged output: MergeLines(...).Select(l => l.Text).</summary>
    public static IReadOnlyList<string> Merge(IReadOnlyList<OcrPass> passes, string preferredLanguage);
}
```

`FromTexts` defaults to `lineHeight = 0`, which means geometry is unknown and adjacency-only stitching applies
(REQ-CHAT-10). `ProcessAsync(IEnumerable<string>)` uses it. In the existing `PipelineFeatureTests` every line has a
sender, so stitching does not change their results.

### `src/Envoy.Core/Ocr/CaptureFixture.cs` (new)

This is a text format for recorded captures. Tests use it, and so does the Live view "Copy as test capture" button.

```
# Free comment lines start with '#'.
@me Hoplite                     # optional: Options.MyPlayerName
@language en                    # optional: Options.MyLanguage (default en)
@muted Spammer, Toxic99         # optional
@players 天下第一, Kim           # optional: pre-seed PlayerDirectory (Observe with null language)
@set hideSystem=false stitch=true clean=true byName=true profile=generic
@pattern *resigned*              # optional, repeatable: Chat.SystemLinePatterns entries
@frame                          # starts a capture; clock advances 1 s per frame
@pass zh-CN                     # optional: engine language for the lines below (default: @language)
 10.0  28.0 | [全 部] 天 下 第 一 ： 这 张 地 图 的 金 矿 在 中 间 ， 我 们
 32.0  50.0 | 应 该 先 占 领 它
@expect                         # messages emitted by THIS frame, in order; "(none)" for none
chat | All | 天下第一 | 这张地图的金矿在中间，我们应该先占领它
```

- Line rows are `top bottom [left right] | text`. The text is exactly what the engine returned, so raw Windows OCR
  quirks such as spaces between CJK characters are kept and `OcrText.Clean` runs inside `MergeLines`.
- Expect rows are `kind | channel | sender | text`. `kind` is `chat`, `system` or `fragment`, and `-` means null.
- Fixtures contain chat text and player names. The button copies to the clipboard only and shows a caption saying so.

```csharp
public sealed record CaptureFrameFixture(IReadOnlyList<OcrPass> Passes, IReadOnlyList<string> Expect);
public sealed record CaptureFixture(IReadOnlyDictionary<string, string> Directives, IReadOnlyList<string> Patterns,
                                    IReadOnlyList<CaptureFrameFixture> Frames)
{
    public static CaptureFixture Parse(string text);                 // throws FormatException with a line number
    public static string Write(IReadOnlyList<OcrLine> lines, string passLanguage, string? comment = null); // one frame, "@expect" left empty
    public string Write();                                           // full round-trip
}
```

### `src/Envoy.Core/Chat/ChatMessage.cs` (changed, source-compatible)

```csharp
public enum ChatLineKind { Chat, System, Fragment }

public sealed record ChatMessage(string Raw, string? Channel, string? Sender, string Text)
{
    public ChatLineKind Kind { get; init; } = ChatLineKind.Chat;
    public int LineCount { get; init; } = 1;
    public double Top { get; init; }
    public double Bottom { get; init; }
}
```

Existing `new ChatMessage(raw, ch, sender, text)` calls (`IncomingMessage.Preview`, tests) compile unchanged.

### `src/Envoy.Core/Chat/ChatFormatProfile.cs` + `ChatFormatCatalog.cs` + `chat-formats.json` (new)

```csharp
public sealed record StitchOptions(bool Enabled = true, double MaxGapFactor = 1.0, double HeightTolerance = 0.5,
                                   int MaxContinuationLines = 4);

public sealed class ChatFormatProfile
{
    public const string GenericId = "generic";
    public string Id { get; }
    public string Name { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Channels { get; }   // "All" → ["all","everyone","全部",…]
    public IReadOnlyList<string> Brackets { get; }                                // ["[]", "()", "<>"]
    public IReadOnlyList<string> Separators { get; }                              // [":", "："]
    public string TimestampPattern { get; }
    public int MaxSenderLength { get; }                                           // 32
    public StitchOptions Stitching { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SystemPatterns { get; } // lang or "*" → patterns

    public static ChatFormatProfile Generic => ChatFormatCatalog.BuiltIn.Generic;

    /// <summary>"All"/"Team"/"Observers"/"Private" for a known alias (case-insensitive), else null.</summary>
    public string? CanonicalChannel(string token);

    // internal: compiled Timestamp/BracketPrefix/BracketSuffix regexes built once in the constructor.
}

public sealed class ChatFormatCatalog
{
    public const string Auto = "auto";
    public static ChatFormatCatalog BuiltIn { get; }                 // lazy; embedded resource "Envoy.Core.Chat.chat-formats.json"
    public IReadOnlyList<ChatFormatProfile> Profiles { get; }        // generic first
    public ChatFormatProfile Generic { get; }
    public IReadOnlyDictionary<string, string> GameProfiles { get; } // game id → profile id (built-in: empty)
    public IReadOnlyList<string> Errors { get; }                     // "chat-formats.json: profile 'x': timestamp: <regex error>"

    public static ChatFormatCatalog LoadWithUserFile(string path);   // built-in + user; never throws
    public static ChatFormatCatalog Parse(string builtInJson, string? userJson); // for tests
    public ChatFormatProfile Resolve(string? setting, string? gameId);
}
```

Embedded JSON shape (`src/Envoy.Core/Chat/chat-formats.json`, `LogicalName="Envoy.Core.Chat.chat-formats.json"`):

```json
{
  "profiles": [
    {
      "id": "generic",
      "name": "Generic (most games)",
      "channels": {
        "All": ["all", "everyone", "全部", "所有人", "所有", "전체", "все", "todos", "alle", "tous", "全員"],
        "Team": ["team", "allies", "ally", "队伍", "隊伍", "团队", "團隊", "盟友", "팀", "아군", "команда", "союзники",
                 "equipo", "aliados", "equipe", "verbündete", "équipe", "alliés", "チーム", "味方"],
        "Observers": ["observers", "observer", "spectators", "观察者", "觀察者", "관전자"],
        "Private": ["private", "whisper", "私聊"]
      },
      "brackets": ["[]", "()", "<>"],
      "separators": [":", "："],
      "timestamp": "^\\s*[\\[(]?\\d{1,2}:\\d{2}(?::\\d{2})?[\\])]?\\s+",
      "maxSenderLength": 32,
      "stitching": { "enabled": true, "maxGapFactor": 1.0, "heightTolerance": 0.5, "maxContinuationLines": 4 },
      "systemPatterns": {}
    }
  ],
  "games": {}
}
```

The alias lists are exactly today's `KnownChannels` and `Canonical` mapping, moved into data. A user file has the
same shape. A profile with an existing id replaces that profile. `"inherits": "generic"` copies every field the user
profile leaves out. `games` entries are merged. A field that fails validation (bad regex, empty separators,
`maxGapFactor` outside 0.2–5, `maxContinuationLines` outside 0–10) falls back to the inherited value and adds an
`Errors` entry. Unknown properties are ignored. JSON is read case-insensitively and allows comments and trailing
commas (`JsonCommentHandling.Skip`, `AllowTrailingCommas`).

### `src/Envoy.Core/Chat/ChatLineParser.cs` (changed)

```csharp
public static ChatMessage Parse(string line);                              // = Parse(line, ChatFormatProfile.Generic)
public static ChatMessage Parse(string line, ChatFormatProfile profile);
internal static string StripTimestamp(string line, ChatFormatProfile profile);
```

The algorithm is unchanged. The constants become profile lookups: `MaxSenderLength`, `KnownChannels`/`Canonical`
become `profile.CanonicalChannel`, `FindSenderColon` searches for any of `profile.Separators` (keeping the
digit:digit and `://` exclusions), and bracket regexes are built from `profile.Brackets`.

### `src/Envoy.Core/Chat/ChatTextCleaner.cs` (new)

```csharp
public static class ChatTextCleaner
{
    /// <summary>REQ-CHAT-35..37. symbols=false only removes invisible characters and collapses whitespace.</summary>
    public static string Clean(string line, bool symbols = true);
    /// <summary>REQ-CHAT-38: trims punctuation around a sender, keeping balanced bracket pairs ("[CLAN] Bob").</summary>
    public static string CleanSender(string sender);
}
```

### `src/Envoy.Core/Chat/SystemLinePatterns.cs` (new)

```csharp
public sealed record PatternError(int Index, string Pattern, string Message);

public sealed class SystemLinePatterns
{
    public static SystemLinePatterns Empty { get; }
    public static SystemLinePatterns Compile(IEnumerable<string> patterns, out IReadOnlyList<PatternError> errors);
    public int Count { get; }
    public bool IsMatch(string cleanedLine);   // RegexMatchTimeoutException → false
    /// <summary>Settings text box helper: split on newlines, trim, drop empty lines (comments kept).</summary>
    public static List<string> SplitLines(string text);
}
```

Each pattern compiles to `^…$` with `RegexOptions.IgnoreCase | CultureInvariant`, `TimeSpan.FromMilliseconds(50)`.
Wildcard patterns are escaped and then `\*` → `.*`, `\{player}` → `.{1,32}?`, and whitespace runs → `\s+`.

### `src/Envoy.Core/Chat/ChatCaptureAnalyzer.cs` (new, pure)

```csharp
public sealed record ChatContext(
    ChatFormatProfile Profile,
    SystemLinePatterns SystemPatterns,       // user + profile patterns for MyLanguage and "*"
    IReadOnlyList<string> KnownPlayers,      // MyPlayerName + PlayerDirectory names
    bool Stitch,                             // Chat.StitchWrappedLines && Profile.Stitching.Enabled
    bool CleanSymbols,
    bool SystemByPlayerName);

public static class ChatCaptureAnalyzer
{
    public static IReadOnlyList<ChatMessage> Analyze(IReadOnlyList<OcrLine> lines, ChatContext context);
    internal static string Join(string a, string b);   // REQ-CHAT-7
}
```

Algorithm:

1. Clean each line. Drop lines that are empty after cleaning. `median` = median `Height` of lines with geometry.
   If no line has geometry, geometry is unknown.
2. Classify each line in this order:
   - (a) `SystemPatterns.IsMatch(text)` or `IsMatch(StripTimestamp(text))` → **System head**.
   - (b) `Parse(text, profile)` returns a sender → **Chat head**. The sender goes through `CleanSender`.
     REQ-CHAT-13 can demote it to a continuation.
   - (c) `SystemByPlayerName` and the timestamp-stripped text starts with a known player (OcrKey prefix match on a
     word boundary) → **System head**.
   - (d) otherwise → **continuation candidate**.
3. Walk top to bottom with `current` = the open entry. A candidate joins `current` when `Stitch` is on, `current` is
   not null, `current.Continuations < MaxContinuationLines`, and `GeometryAllows(prev, line)`. `GeometryAllows` is
   true when geometry is unknown, or when `-0.5·median ≤ line.Top − prev.Bottom ≤ MaxGapFactor·median` and
   `|line.Height − prev.Height| ≤ HeightTolerance·median`. Otherwise the candidate opens a new **Fragment** entry,
   which later candidates may also join. Heads always open a new entry.
4. Emit `ChatMessage`s. A chat entry has the parsed channel and sender, and `Text` = `Join` of the head text and
   each continuation. A system or fragment entry has `Channel = Sender = null`. Every entry sets `Raw`, `LineCount`,
   `Top`, `Bottom` and `Kind`.

Cost: O(lines) regex calls per capture. Profiles and patterns compile once per settings change, never per capture.

### `src/Envoy.Core/Chat/ChatCaptureFormatter.cs` (new)

`public static string Describe(IReadOnlyList<ChatMessage> entries)` gives one line per entry, for example
`[All] Zhang Wei: … (2 lines)`, `(notice) …` or `(fragment) …`. The Live view and the smoke-test log use it.

### `src/Envoy.Core/Chat/ChatDeduplicator.cs` (changed)

```csharp
public double SenderThreshold { get; set; } = 0.75;

/// <summary>Atomically decides which messages of one capture are new; remembers all of them.</summary>
public IReadOnlyList<ChatMessage> AcceptNew(IReadOnlyList<ChatMessage> capture);

public bool TryAccept(string line) => AcceptNew([new ChatMessage(line, null, null, line)]).Count == 1;
```

Memory entries become `(string? SenderKey, string TextKey, DateTimeOffset SeenAt, int MaxSimultaneous)`. For each
message in capture order, under the lock:

- A **Fragment** first tries containment (REQ-CHAT-15) against every entry's `TextKey`. A match means rejected.
- Otherwise find the best-matching entry by REQ-CHAT-16, or create one with `MaxSimultaneous = 0`. Increment this
  capture's hit count for that entry and refresh `SeenAt`/`TextKey` (keep the longer text). Accept only when
  `hits > MaxSimultaneous`, then set `MaxSimultaneous = hits`.

`TextSimilarity` gains `ContainsFuzzy(string haystack, string needle, double threshold)`. It slides a window of the
needle's length (±1) over the haystack. Inputs are capped at 400 characters.

### `src/Envoy.Core/Chat/TextSimilarity.cs` and `PlayerDirectory.cs` (changed)

```csharp
/// <summary>Normalize + fold OCR-confusable sequences: rn→m, vv→w, cl→d, q→g. Digits beyond Normalize's 0/1/5 are not folded.</summary>
public static string OcrKey(string text);

public sealed class PlayerDirectory
{
    public IReadOnlyList<string> KnownNames { get; }
    /// <summary>Display name of the known player with the same OcrKey, else the input. Null stays null.</summary>
    public string? Canonicalize(string? sender);
}
```

The folding table is a short fixed list, tested both ways. `Player1`/`Player2` must stay distinct, and
`Zhanq Wei`/`Zhang Wei` and `rnike`/`mike` must fold together.

### `src/Envoy.Core/Pipeline/ChatPipeline.cs` (changed)

```csharp
public sealed class ChatPipelineOptions
{
    // existing …
    public ChatFormatProfile Format { get; set; } = ChatFormatProfile.Generic;
    public bool StitchWrappedLines { get; set; } = true;
    public bool HideSystemLines { get; set; } = true;
    public bool DetectSystemLinesByPlayerName { get; set; } = true;
    public List<string> SystemLinePatterns { get; set; } = [];
    public bool CleanSymbols { get; set; } = true;
}

public Task<IReadOnlyList<IncomingMessage>> ProcessAsync(IReadOnlyList<OcrLine> lines, CancellationToken ct = default);
public Task<IReadOnlyList<IncomingMessage>> ProcessAsync(IEnumerable<string> lines, CancellationToken ct = default)
    => ProcessAsync(OcrLine.FromTexts(lines), ct);

/// <summary>Analysis only (no dedupe, no events, no translation) for the Live view. Thread-safe.</summary>
public IReadOnlyList<ChatMessage> Preview(IReadOnlyList<OcrLine> lines);

public IReadOnlyList<PatternError> SystemPatternErrors { get; }
public string? LastAnalysisError { get; }
```

- `BuildContext()` snapshots the options once per call. Compiled `SystemLinePatterns` are cached by the joined
  pattern text plus `MyLanguage` plus profile id, and swapped with `Volatile.Write`.
- `Analyze` runs inside `try`. On an unexpected exception it falls back to `lines.Select(l => Parse(l.Text, profile))`
  and sets `LastAnalysisError` (REQ-CHAT-45).
- `ShouldEmit(m)`: if `m.Kind == System`, return `!HideSystemLines && TextElementCount ≥ MinCharacters`. Otherwise
  use the existing `IsWorthShowing`. A `System` message skips `Classify`, gets `Status = Untranslated` and
  `SourceLanguage = MyLanguage`, and is not observed in `PlayerDirectory`.
- `NewMatch()` and `Reset()` are unchanged. They clear dedupe and players.

### Settings (`src/Envoy.Core/Settings/AppSettings.cs`, shared)

```csharp
public sealed class AppSettings
{
    // … existing …
    public ChatSettings Chat { get; set; } = new();   // NEW
}

public sealed class ChatSettings                      // NEW
{
    /// <summary>"auto" = per game (falls back to generic), or a profile id from chat-formats.json.</summary>
    public string FormatProfile { get; set; } = ChatFormatCatalog.Auto;
    public bool StitchWrappedLines { get; set; } = true;
    public bool HideSystemLines { get; set; } = true;
    public bool DetectSystemLinesByPlayerName { get; set; } = true;
    /// <summary>One pattern per entry; see docs/chat-formats.md. Mark [Personal] if 02 has landed (may contain names).</summary>
    public List<string> SystemLinePatterns { get; set; } = [];
    public bool CleanSymbols { get; set; } = true;
}
```

This change is additive, so no settings migration is needed. Old files without `chat` load the defaults.

### App shell changes (thin)

- **`AppHost.cs` (shared):**
  - Add `public static string ChatFormatsPath => Path.Combine(DataFolder, "chat-formats.json");`
    and `public ChatFormatCatalog ChatFormats { get; private set; }`, loaded in the constructor before `Pipeline`.
  - `PipelineOptions()` maps the six `Settings.Chat.*` values plus
    `Format = ChatFormats.Resolve(Settings.Chat.FormatProfile, CurrentGameId)`.
  - `ApplySettings()` copies the same 7 properties into `Pipeline.Options`, next to the existing copies.
  - `RefreshGame()`, after the game changes: `Pipeline.Options.Format = ChatFormats.Resolve(...)`.
  - Add `public void ReloadChatFormats()`, which reloads the catalog and re-resolves.
- **`Services/ChatWatcher.cs`:**
  - `var lines = OcrMerger.MergeLines(passes, settings().MyLanguage);`
  - `CaptureFrame` becomes `record CaptureFrame(CapturedImage Image, IReadOnlyList<OcrLine> Lines,
    IReadOnlyList<ChatMessage> Understood, TimeSpan OcrTime)`. `Understood = pipeline.Preview(lines)` is computed
    only when `PublishFrames` is set.
  - `ProcessAsync(lines, ct)` takes the geometry overload.
  - `capture-engine` may also edit this file. These three lines are the whole change.
- **`Services/OcrService.cs`** (REQ-CHAT-4): build `OcrLine(l.Text, top, bottom, left, right)` with
  `left = Words.Min(BoundingRect.Left)` and `right = Words.Max(BoundingRect.Right)`.
- **`ViewModels/MainViewModel.cs`** (or its `MainViewModel.ReadingChat.cs` partial after 01):
  - `ChatFormatOptions`: `IReadOnlyList<Option>` = `("auto", "Match the game")` followed by each profile's
    `(Id, Name)`.
  - `SystemLinePatternsText`: get joins with `\n`. Set uses `SystemLinePatterns.SplitLines` and then calls
    `Host.ApplySettings()`.
  - `ChatUnderstandingStatus`: the catalog `Errors` and `Pipeline.SystemPatternErrors`, formatted
    `Line 3: …`. Empty means hidden.
  - `HasChatUnderstandingErrors` (bool).
- **Reading Chat page** (`Windows/MainWindow.xaml` "READING CHAT" section, or `Pages/ReadingChatPage.xaml` after 01).
  Add a new `OrnateFrame` card, "Understanding chat", between "Fine tuning" and "Live view". It reuses
  `SettingRow`, `Text.H2`, `Text.Label` and `Text.Caption`, and only `DynamicResource` colours:
  - "Chat format": a `ComboBox`, `ItemsSource=ChatFormatOptions`, `SelectedValue=Settings.Chat.FormatProfile`.
  - `CheckBox` "Join messages that wrap onto the next line" → `Settings.Chat.StitchWrappedLines`.
  - `CheckBox` "Hide game notices (lines that aren't a player talking)" → `Settings.Chat.HideSystemLines`.
  - `CheckBox` "Treat lines that start with a player's name but have no ':' as notices" →
    `DetectSystemLinesByPlayerName`.
  - "Also treat these lines as notices": a `TextBox` with `AcceptsReturn=True`, `MinHeight=64`,
    `Text={Binding SystemLinePatternsText, UpdateSourceTrigger=LostFocus}`. Caption: "One per line. `*` matches
    anything, `{player}` matches a name, start with `re:` for a regular expression."
  - `CheckBox` "Remove stray symbols that OCR reads from icons" → `CleanSymbols`.
  - An error `TextBlock` with `Foreground={DynamicResource Danger}`, bound to `ChatUnderstandingStatus` and visible
    when `HasChatUnderstandingErrors`.
- **Live view**, in the code-behind that holds `OnFrame` (`MainWindow.xaml.cs` today, `ReadingChatPage.xaml.cs`
  after 01):
  - `PreviewText` shows the raw lines, a blank line, `Understood as:` and then `ChatCaptureFormatter.Describe(frame.Understood)`.
  - A `Button` "Copy as test capture" in the Live view header writes `CaptureFixture.Write(_lastLines,
    Settings.MyLanguage)` to the clipboard, and the caption reads "Copied. Contains chat text; only share it if
    you're happy to." After 01 this is a `RelayCommand` on the view model with the last frame handed over by the page.
  - `PreviewText` is already excluded from auto-apply. The new pattern `TextBox` is a setting, so it is **not**
    excluded.

### Threading

- `ChatCaptureAnalyzer`, `ChatTextCleaner`, `SystemLinePatterns.IsMatch` and profiles are immutable or pure, and are
  safe on the watcher thread and on concurrent `ProcessAsync` calls.
- `ChatDeduplicator.AcceptNew` holds its lock for the whole capture (at most about 40 entries × 200 memory entries of
  Levenshtein on short strings, well under 1 ms).
- `PlayerDirectory.Canonicalize`/`KnownNames` take the existing `_gate`.
- `Options.Format` and the pattern cache are reference swaps that `BuildContext()` reads once per capture. The UI
  thread writes them in `ApplySettings`/`RefreshGame`.

### Failure behaviour

| Failure | Behaviour |
| --- | --- |
| User `chat-formats.json` is malformed JSON | Built-in profiles stay in use. "chat-formats.json: <message>" appears on the card and in `ILog` (Warn, category `chat`) if present. |
| Profile field invalid (bad regex, out of range) | That field is inherited from the parent profile and an error is listed. |
| Profile id in settings no longer exists | `Resolve` returns `generic`. The combo shows "Match the game" on next open, and the setting is kept so it works again if the file is restored. |
| User pattern invalid | Skipped. "Line N: <regex error>" is shown on the card. Other patterns still apply. |
| Regex timeout | Counts as no match for that line. Not logged per line, to avoid spam. |
| Analyzer bug or exception | One-line-per-message fallback for that capture and `LastAnalysisError` set. The loop keeps running. |
| Geometry missing (all zero) | Adjacency-only stitching. |

## Acceptance criteria

| Id | Requirements | Verified by |
| --- | --- | --- |
| AC-CHAT-1 | REQ-CHAT-1, 2, 4 | Unit tests `OcrMergerTests.MergeLines_keeps_median_geometry_per_row`, `MergeLines_text_matches_Merge`, `MergeLines_carries_left_right`; existing `OcrMergerTests` unchanged and green. |
| AC-CHAT-2 | REQ-CHAT-3 | `tools/typecheck/typecheck.sh` passes with `ChatWatcher` calling `MergeLines` and `ProcessAsync(IReadOnlyList<OcrLine>)`; Windows CI build passes. |
| AC-CHAT-3 | REQ-CHAT-5, 6, 7, 8 | `ChatCaptureAnalyzerTests.Joins_continuation_within_gap`, `Stops_at_max_continuations`, `Joins_cjk_without_space`, `Joins_latin_and_hangul_with_space`, `Stitched_message_has_raw_linecount_and_bounds`; golden fixtures `latin-wrapped`, `zh-wrapped`, `ja-wrapped`, `ko-wrapped`, `ru-wrapped`. |
| AC-CHAT-4 | REQ-CHAT-9, 18 | Golden `own-and-muted-wrapped` (two frames: the stitched own/muted message is dropped, and its scrolled-off tail in frame 2 emits nothing); `ChatPipelineUnderstandingTests.Filtered_messages_are_remembered_for_fragments`. |
| AC-CHAT-5 | REQ-CHAT-10, 11 | `ChatCaptureAnalyzerTests.Unknown_geometry_stitches_adjacent_lines`, `Stitching_off_keeps_one_message_per_line`. |
| AC-CHAT-6 | REQ-CHAT-12, 23, 24 | `ChatCaptureAnalyzerTests.Continuation_of_notice_joins_notice`, `Pattern_with_colon_beats_sender_parse`; `SystemLinePatternsTests.Wildcard_player_and_regex_forms`, `Matches_with_and_without_timestamp`; golden `system-patterns`. |
| AC-CHAT-7 | REQ-CHAT-13 | `ChatCaptureAnalyzerTests.Full_previous_line_demotes_unknown_sender_to_continuation` (only if implemented; COULD). |
| AC-CHAT-8 | REQ-CHAT-14, 15 | `ChatDeduplicatorTests.Fragment_contained_in_known_message_is_rejected`, `Fuzzy_fragment_is_rejected`, `Unknown_fragment_is_accepted`; golden `scroll-orphan`, `orphan-unknown`, `gap-break`, `height-mismatch`. |
| AC-CHAT-9 | REQ-CHAT-16, 17, 22 | `ChatDeduplicatorTests.Same_text_from_different_senders_is_two_messages`, `Sender_jitter_is_same_message`, `Channel_and_timestamp_do_not_affect_identity`; all existing `ChatDeduplicatorTests` unchanged and green. |
| AC-CHAT-10 | REQ-CHAT-19, 20 | `ChatDeduplicatorTests.Two_visible_copies_are_two_messages`, `Rereading_copies_accepts_none`, `Third_copy_accepts_one`, `Concurrent_captures_do_not_double_accept` (parallel `AcceptNew` × 50, asserting exactly one acceptance); golden `repeat-in-capture`. |
| AC-CHAT-11 | REQ-CHAT-21 | `TextSimilarityTests.OcrKey_folds_confusables` / `OcrKey_keeps_distinct_digits`; `PlayerDirectoryTests.Canonicalize_maps_misread_to_known_name`, `Canonicalize_keeps_unknown`; golden `name-misread`. |
| AC-CHAT-12 | REQ-CHAT-25 | `SystemLinePatternsTests.Invalid_regex_reported_and_skipped`, `Catastrophic_regex_times_out_as_no_match`. |
| AC-CHAT-13 | REQ-CHAT-26, 27 | `ChatPipelineUnderstandingTests.System_lines_hidden_by_default`, `Shown_system_lines_are_untranslated_and_cost_nothing` (FakeTranslator records zero requests; `SuggestedReplyLanguage` unchanged). |
| AC-CHAT-14 | REQ-CHAT-28 | `ChatCaptureAnalyzerTests.Line_starting_with_known_player_is_notice`, `Short_names_do_not_trigger_notice`; golden `system-known-player`. |
| AC-CHAT-15 | REQ-CHAT-29, 31, 32, 33 | `ChatFormatCatalogTests.Builtin_has_generic_only`, `User_file_adds_and_replaces_profiles`, `Inherits_fills_missing_fields`, `Bad_regex_field_falls_back_with_error`, `Malformed_user_file_keeps_builtins`, `Resolve_auto_uses_game_map_then_generic`, `Resolve_unknown_id_is_generic`, `Profile_system_patterns_apply_for_my_language_and_star`. |
| AC-CHAT-16 | REQ-CHAT-30 | Every existing `ChatLineParserTests` case passes unchanged; new `ChatLineParserTests.Generic_profile_equals_legacy_parse` runs those inputs through both overloads. |
| AC-CHAT-17 | REQ-CHAT-34 | Manual, Windows: set the Game to AoE II DE, then AoE IV, with a user `chat-formats.json` mapping `aoe4` to a profile using separator `>`. The Live view "Understood as" switches parse within one capture. |
| AC-CHAT-18 | REQ-CHAT-35–39 | `ChatTextCleanerTests.Removes_invisible_and_replacement_chars`, `Strips_leading_icon_junk_not_brackets`, `Folds_fullwidth_ascii`, `Keeps_hangul_compat_jamo`, `Clean_sender_keeps_clan_brackets`, `Symbols_off_still_removes_invisibles`; golden `symbol-junk`, `fullwidth`. |
| AC-CHAT-19 | REQ-CHAT-40 | Typecheck passes (bindings compile, resource keys valid: `Danger`, `Text.Caption`); Windows smoke test navigates Reading Chat under all 5 themes with no binding errors; CI screenshot `*-settings-capture.png` shows the card. |
| AC-CHAT-20 | REQ-CHAT-41, 42 | `ChatCaptureFormatterTests.Describes_kinds_and_line_counts`. Manual: open Reading Chat with a game showing a wrapped message; "Understood as" shows `(2 lines)`. "Copy as test capture" puts text on the clipboard that `CaptureFixture.Parse` accepts. |
| AC-CHAT-21 | REQ-CHAT-43 | `CaptureFixtureTests.Round_trips_frames_passes_geometry`, `Parse_error_reports_line_number`. |
| AC-CHAT-22 | REQ-CHAT-44 | `ChatGoldenTests.Fixture_matches_expected` (a `[Theory]` over every `Golden/Captures/*.capture.txt`) passes; `ChatGoldenTests.Corpus_has_required_coverage` asserts at least 16 files and the presence of the named files listed in the Test plan. |
| AC-CHAT-23 | REQ-CHAT-45 | `ChatPipelineUnderstandingTests.Analyzer_failure_falls_back_to_line_mode` (an `internal` fault-injection hook on `ChatPipeline`, reachable via `InternalsVisibleTo`, makes `Analyze` throw; result is one message per line and `LastAnalysisError` is set). |
| AC-CHAT-24 | REQ-CHAT-46 | Review/grep: no `HttpClient` or `WebRequest` usage in `src/Envoy.Core/Chat` or `src/Envoy.Core/Ocr`; `quality-security-ci`'s no-network check (if any) passes. |
| AC-CHAT-25 | all | Windows smoke test: after host creation it runs `host.Pipeline.Preview` on a built-in sample capture (3 lines, one wrap) and asserts 2 entries, then logs `chat-understanding ok: <Describe>`. It fails the run otherwise. |
| AC-CHAT-26 | REQ-CHAT-5, 23, 27 | Manual, in game (any AoE title, English client): type a message long enough to wrap and have a teammate type one. Each shows as one overlay entry. Resign an AI game with `*resign*` in the pattern list; no notice is translated. |

## Test plan

All tests live in `tests/Envoy.Core.Tests` and run with `dotnet test`. Add
`<None Include="Golden\Captures\*.capture.txt" CopyToOutputDirectory="PreserveNewest" />` to
`Envoy.Core.Tests.csproj`.

### Unit tests to add

| File | Test | Asserts |
| --- | --- | --- |
| `OcrTests.cs` | `OcrMergerTests.MergeLines_keeps_median_geometry_per_row` | 3 passes reading the same row at slightly different Top/Bottom give the median values |
| | `MergeLines_text_matches_Merge` | Same text sequence as `Merge` for the existing merger fixture |
| | `MergeLines_carries_left_right` | Left/Right medians; zero stays zero |
| `ChatAnalyzerTests.cs` (new) | `Joins_continuation_within_gap` | Head + 1 continuation at gap 0.3·h is one message |
| | `Gap_too_large_starts_fragment` | Gap 2·h gives a Fragment |
| | `Height_mismatch_starts_fragment` | A continuation 2× as tall is not joined |
| | `Stops_at_max_continuations` | The 5th continuation starts a fragment |
| | `Joins_cjk_without_space` / `Joins_latin_and_hangul_with_space` | `Join` rules (REQ-CHAT-7) |
| | `Stitched_message_has_raw_linecount_and_bounds` | Raw has `\n`, LineCount, Top/Bottom |
| | `Unknown_geometry_stitches_adjacent_lines` | `FromTexts` input still stitches |
| | `Stitching_off_keeps_one_message_per_line` | Stitch=false gives legacy output |
| | `Continuation_of_notice_joins_notice` | System head + continuation is one System entry |
| | `Pattern_with_colon_beats_sender_parse` | `Tribute: …` matched by a pattern is System, not sender "Tribute" |
| | `Line_starting_with_known_player_is_notice` / `Short_names_do_not_trigger_notice` | Heuristic on/off and the 3-character minimum |
| | `Leading_lines_are_fragments` | Sender-less top lines give one Fragment |
| | `Full_previous_line_demotes_unknown_sender_to_continuation` | COULD, REQ-CHAT-13 |
| `ChatTests.cs` | `ChatLineParserTests.Generic_profile_equals_legacy_parse` | Existing inputs, both overloads equal |
| | `Custom_profile_separator_and_channels` | A profile with separator `>` and channel `{"Team":["eq"]}` parses accordingly |
| | `ChatDeduplicatorTests.Same_text_from_different_senders_is_two_messages` | `A: gg`, `B: gg` are both accepted |
| | `Sender_jitter_is_same_message` | `Zhang Wei`/`Zhanq Wei` with the same text is accepted once |
| | `Channel_and_timestamp_do_not_affect_identity` | `[All] 12:01 A: hi there` and `A: hi there` are accepted once |
| | `Two_visible_copies_are_two_messages` / `Rereading_copies_accepts_none` / `Third_copy_accepts_one` | Occurrence counting |
| | `Fragment_contained_in_known_message_is_rejected` / `Fuzzy_fragment_is_rejected` / `Unknown_fragment_is_accepted` | REQ-CHAT-15 |
| | `Concurrent_captures_do_not_double_accept` | `Parallel.For` 50 × `AcceptNew([A: hello])` gives exactly 1 acceptance |
| `ChatTextCleanerTests.cs` (new) | `Removes_invisible_and_replacement_chars` | ZWSP, BOM, U+FFFD, PUA and VS16 are removed |
| | `Strips_leading_icon_junk_not_brackets` | `"■ [All] Bob: hi"` → `"[All] Bob: hi"`; `"[CLAN] Bob: hi"` unchanged |
| | `Folds_fullwidth_ascii` / `Keeps_hangul_compat_jamo` | `ｇｇ` → `gg`; `ㅋㅋ` unchanged |
| | `Clean_sender_keeps_clan_brackets` / `Symbols_off_still_removes_invisibles` | — |
| `SystemLinePatternsTests.cs` (new) | `Wildcard_player_and_regex_forms` | `*`, `{player}` and `re:` forms; case-insensitive; whole-line |
| | `Matches_with_and_without_timestamp` | — |
| | `Invalid_regex_reported_and_skipped` | `PatternError.Index`, other patterns still work |
| | `Catastrophic_regex_times_out_as_no_match` | `re:(a+)+$` on 40 × `a` + `!` returns false in < 1 s |
| | `SplitLines_trims_and_drops_blanks` | — |
| `ChatFormatCatalogTests.cs` (new) | `Builtin_has_generic_only`, `User_file_adds_and_replaces_profiles`, `Inherits_fills_missing_fields`, `Bad_regex_field_falls_back_with_error`, `Malformed_user_file_keeps_builtins`, `Resolve_auto_uses_game_map_then_generic`, `Resolve_unknown_id_is_generic`, `Profile_system_patterns_apply_for_my_language_and_star` | As named |
| `PlayerAndUpdateTests.cs` | `TextSimilarityTests.OcrKey_folds_confusables` / `OcrKey_keeps_distinct_digits` | `Zhanq`≡`Zhang`, `rnike`≡`mike`; `Player1`≠`Player2` |
| | `PlayerDirectoryTests.Canonicalize_maps_misread_to_known_name` / `Canonicalize_keeps_unknown` | — |
| `ChatPipelineUnderstandingTests.cs` (new) | `System_lines_hidden_by_default`, `Shown_system_lines_are_untranslated_and_cost_nothing`, `Filtered_messages_are_remembered_for_fragments`, `Wrapped_message_costs_one_request`, `Analyzer_failure_falls_back_to_line_mode`, `String_overload_matches_legacy_for_sender_lines` | As named, with `FakeTranslator` and `FakeTimeProvider` |
| `ChatCaptureFormatterTests` (in the same file) | `Describes_kinds_and_line_counts` | Exact text |
| `CaptureFixtureTests.cs` (new) | `Round_trips_frames_passes_geometry`, `Parse_error_reports_line_number` | — |
| `Golden/ChatGoldenTests.cs` (new) | `Fixture_matches_expected` (`[Theory]`, `MemberData` = file names) | For each frame: build `OcrPass`es, `MergeLines`, `ProcessAsync`, then compare the `kind \| channel \| sender \| text` list exactly. On failure, print the fixture name, frame index and the actual rows ready to paste |
| | `Corpus_has_required_coverage` | 16+ files, and the required names below exist |

### Golden fixtures (`tests/Envoy.Core.Tests/Golden/Captures/`)

These are hand-written, using geometry and OCR quirks typical of Windows OCR on upscaled chat. Chat text is invented
player chat. Notice lines in fixtures are illustrative text matched by fixture-local `@pattern`s, not claims about
real game strings.

| File | Covers |
| --- | --- |
| `latin-basic.capture.txt` | Single-line messages, `[All]`/`(Team)`/suffix channel, timestamp |
| `latin-wrapped.capture.txt` | A 3-line wrap followed by a new sender |
| `zh-wrapped.capture.txt` | zh-CN pass with spaces between characters, `：`, a wrap joined without a space, a 1-character tail kept |
| `ja-wrapped.capture.txt` | Kana and kanji wrap |
| `ko-wrapped.capture.txt` | Hangul wrap joined with a space, `ㅋㅋㅋ` single line |
| `ru-wrapped.capture.txt` | Cyrillic `Иван (Команда): …` wrap |
| `mixed-passes.capture.txt` | en + zh-CN + ko passes on one capture; four speakers; per-row engine choice |
| `scroll-up.capture.txt` | 3 frames shifting up one line each, with one new bottom message per frame |
| `scroll-orphan.capture.txt` | Frame 2: the head scrolled out and only its tail remains, so nothing is emitted |
| `orphan-unknown.capture.txt` | Frame 1 starts mid-message: one `fragment` emitted |
| `gap-break.capture.txt` | A sender-less line 2.5 line heights down is a fragment, not joined |
| `height-mismatch.capture.txt` | A taller sender-less line is not joined |
| `system-patterns.capture.txt` | `@pattern` notice (wrapped) hidden; `@set hideSystem=false` variant emits `system` |
| `system-known-player.capture.txt` | `@players`, and a sender-less line starting with that name is hidden |
| `own-and-muted-wrapped.capture.txt` | `@me`/`@muted` wrapped messages and their scrolled tails emit nothing |
| `repeat-in-capture.capture.txt` | Two `gg` from one player gives 2; re-read gives 0; a third gives 1 |
| `name-misread.capture.txt` | `Zhanq Wei` in frame 2 is canonicalised to `Zhang Wei`; same text is not re-emitted |
| `symbol-junk.capture.txt` | Leading `■`, `©`, ZWSP and U+FFFD give clean senders |
| `fullwidth.capture.txt` | `ｇｇ ｗｐ` is emitted as `gg wp` |

### Smoke-test additions (`src/Envoy.App/SmokeTest.cs`, shared)

After `log.Add("host created")`:

```csharp
var sample = OcrLine.FromTexts(["[All] Zhang Wei: this message is long enough that it", "wraps onto a second line", "Kim: ㅋㅋㅋ"], 18, 4);
var understood = host.Pipeline.Preview(sample);
if (understood.Count != 2) throw new InvalidOperationException("chat understanding: expected 2 entries, got " + understood.Count);
log.Add("chat-understanding ok: " + ChatCaptureFormatter.Describe(understood).Replace('\n', ' '));
```

The Reading Chat card is exercised by the existing loop that navigates every page under every theme. No new window
is added.

### Manual checks (Windows, in game)

1. In AoE II DE or AoE IV, get a long message from an AI ally or a second account. The overlay shows one entry, and
   Live view says `(2 lines)`.
2. Scroll the chat with new messages until the wrapped message's first line leaves the region. Nothing new appears.
3. Add `*resign*` (or the client-language equivalent) to the pattern list and resign an AI. Nothing is translated,
   and the Translation usage counter does not move.
4. Type the same short message twice quickly from the second account. Two overlay entries appear.
5. Click "Copy as test capture", paste into a new fixture file, fill in `@expect`, and run `dotnet test`. It parses.

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Ocr/OcrResults.cs` | `OcrLine` Left/Right, `HasGeometry`, `FromTexts`; `OcrMerger.MergeLines`; `Merge` delegates | |
| `src/Envoy.Core/Ocr/CaptureFixture.cs` | new | |
| `src/Envoy.Core/Chat/ChatMessage.cs` | `ChatLineKind`, `Kind`/`LineCount`/`Top`/`Bottom` init props | |
| `src/Envoy.Core/Chat/ChatLineParser.cs` | profile-driven; `Parse(line, profile)` | |
| `src/Envoy.Core/Chat/ChatFormatProfile.cs`, `ChatFormatCatalog.cs`, `chat-formats.json` | new | |
| `src/Envoy.Core/Chat/ChatTextCleaner.cs`, `SystemLinePatterns.cs`, `ChatCaptureAnalyzer.cs`, `ChatCaptureFormatter.cs` | new | |
| `src/Envoy.Core/Chat/ChatDeduplicator.cs` | `AcceptNew`, sender-aware keys, occurrence counts, fragment containment | |
| `src/Envoy.Core/Chat/TextSimilarity.cs` | `OcrKey`, `ContainsFuzzy` | |
| `src/Envoy.Core/Chat/PlayerDirectory.cs` | `KnownNames`, `Canonicalize` | |
| `src/Envoy.Core/Pipeline/ChatPipeline.cs` | new options, geometry overload, `Preview`, system handling, fallback | |
| `src/Envoy.Core/Envoy.Core.csproj` | `<EmbeddedResource Include="Chat\chat-formats.json" LogicalName="Envoy.Core.Chat.chat-formats.json" />` | |
| `src/Envoy.Core/Settings/AppSettings.cs` | add `Chat` property and `ChatSettings` class (see Design) | **yes** |
| `src/Envoy.App/AppHost.cs` | `ChatFormatsPath`, `ChatFormats`, `ReloadChatFormats()`; 7 lines in `PipelineOptions()`/`ApplySettings()`; 1 line in `RefreshGame()` | **yes** |
| `src/Envoy.App/Services/ChatWatcher.cs` | `MergeLines`, `CaptureFrame` shape, geometry overload (3 lines) | (also `capture-engine`) |
| `src/Envoy.App/Services/OcrService.cs` | Left/Right on `OcrLine` | (also `capture-engine`) |
| `src/Envoy.App/ViewModels/MainViewModel.cs` (or `MainViewModel.ReadingChat.cs`) | `ChatFormatOptions`, `SystemLinePatternsText`, `ChatUnderstandingStatus`, `HasChatUnderstandingErrors`, copy-capture command | (also 01) |
| `src/Envoy.App/Windows/MainWindow.xaml` (or `Pages/ReadingChatPage.xaml` after 01) | "Understanding chat" card; Live view button | **yes (MainWindow*)** |
| `src/Envoy.App/Windows/MainWindow.xaml.cs` (or `Pages/ReadingChatPage.xaml.cs`) | `OnFrame` shows "Understood as", keeps `_lastLines` | **yes (MainWindow*)** |
| `src/Envoy.App/SmokeTest.cs` | chat-understanding sample assertion (see Test plan) | **yes** |
| `tests/Envoy.Core.Tests/*.cs` | new and extended tests listed above | |
| `tests/Envoy.Core.Tests/Golden/**` | harness and 19 fixtures | |
| `tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj` | copy fixtures to output | |
| `docs/chat-formats.md` | new: profiles JSON, pattern syntax, contributing captures, privacy note | |
| `docs/architecture.md` | flow diagram: `MergeLines` → analyzer → dedupe; `Chat/` table rows | |
| `docs/troubleshooting.md` | "Messages are split / notices are translated" section pointing at the card | |
| `CHANGELOG.md` | user-facing entry | |

Not touched: `App.xaml.cs`, `OverlayWindow*`, `ComposeWindow*`, `LookupWindow*`, palettes, `Controls.xaml`.

## Risks & open questions

- **Wrong-speaker stitching.** If OCR drops a sender's colon (for example reading it as `;` or `.`), that line is
  stitched into the previous speaker's message. Mitigations: the geometry and height checks, the 4-continuation
  cap, a user-editable `separators` list, the "Join wrapped lines" toggle, and REQ-CHAT-13 if implemented. The golden
  corpus should grow from real "Copy as test capture" reports.
- **Colons inside wrapped text** (`…we push at min 5: go`) can still be read as a new sender `we push at min 5`
  unless REQ-CHAT-13 lands. The 32-character sender cap limits this. We accept it for 1.0.
- **Line-spacing assumptions.** `MaxGapFactor` 1.0 and `HeightTolerance` 0.5 are guesses, not measured on real
  games. They are per-profile and calibratable via `chat-formats.json`. Open question: should the Live view offer an
  "auto-calibrate from this frame" button? (Out of scope; candidate for later.)
- **Korean wrap separator.** If a game wraps Hangul mid-word, joining with a space splits that word. Translation
  usually survives this. A per-profile `joinWithoutSpaceScripts` field could be added later.
- **System heuristic false positives.** A chat continuation that starts with a known player's name ("Bob will
  boom") is hidden as a notice. It is limited to sender-less lines with names of 3+ characters, and it can be
  switched off.
- **No built-in notice patterns.** By design we do not ship guessed game strings, so out of the box only the
  player-name heuristic catches notices. Open question: should the community-maintained `chat-formats.json` in the
  repo later carry verified per-language patterns, reviewed with screenshots?
- **Short repeated messages across time.** A `gg` repeated after the first copy has left the screen, but within the
  3-minute memory, is still suppressed (as today). Occurrence counting only fixes copies visible at the same time.
  Open question: should `Memory` shorten to the game's chat fade time, once `capture-engine` can detect an empty
  chat region?
- **Truncated head message.** If the region cuts off a message's last line, the emitted message is truncated. The
  later full read is suppressed by the prefix rule, and no update or re-translation happens (non-goal).
- **Canonicalisation collisions.** Two real players whose names fold to the same `OcrKey` (for example `Barn` and
  `Bam`) display as one name. This is rare. The folding table is deliberately small.
- **Overlap with other areas.** `capture-engine` may restructure `ChatWatcher`/`OcrService`, so we keep our edits to
  the listed lines. `01-ui-foundation` moves the Reading Chat page and its code-behind, and our UI edits follow
  whichever layout exists at merge time. `02` provides `ILog`/`[Personal]`, which we use only if present.
  `translation-resilience` sees fewer, longer requests, which is a benefit, but the length budget per request may
  need checking.
- **Privacy.** Fixtures copied from the Live view contain other players' names and chat. The button copies to the
  clipboard only and its caption warns about this. `docs/chat-formats.md` tells contributors to anonymise names
  before opening a PR.
