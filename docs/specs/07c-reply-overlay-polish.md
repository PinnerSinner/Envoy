# Reply box and overlay polish: reply history, length counter, phrase order, compact overlay, copy

Status: Proposed — revised against `00-integration.md` (binding). Lands as step 10, after 01, 08a, 02, 03, 05, 04,
06, 07a and 07b.

Area id: `07c-reply-overlay-polish` · Requirement prefix: `OUXC` · Split from `07-onboarding-ux-accessibility.md`.
Requirement numbers keep the original `REQ-OUX-n` numbering (contract preamble), so `REQ-OUXC-39` is the old
`REQ-OUX-39` and the contract §9 budget row "07c" maps directly. The other parts are `07a-onboarding-notices.md` and
`07b-accessibility.md`.

## Context

### What exists today

| Topic | Today | Where |
| --- | --- | --- |
| Reply box | A single-line input with no history, so you cannot recall a reply you sent a moment ago. There is no length feedback. Quick-phrase chips are always in phrasebook order, whatever the target language, so phrases with no translation in the target language sit among the useful ones. The clipboard retry helper (5 × 40 ms) is private to `ComposeWindow`. `App` keeps one `ComposeWindow` and reuses it (`_compose ??= new ComposeWindow(Host)`). | `Windows/ComposeWindow.xaml(.cs)`, `Core/Phrases/PhraseBook.cs` |
| Overlay | One layout (two to four lines per message). Click-through means you cannot select or copy a message. Edit mode (`Ctrl+Shift+E`) only moves and resizes. | `Windows/OverlayWindow.xaml(.cs)`, `Windows/OverlayItem.cs` |

### What earlier steps deliver (contract §1)

- **01:** `Pages/ReplyPage.xaml`, `Pages/OverlayPage.xaml`, `SettingsAutoApplier`, the smoke "other windows" stage.
- **08a:** `OutgoingText.Sanitize` (with `HardMaxLength = 1000` UTF-16 code units) in the compose send and copy paths;
  `SendTargetGuard`.
- **02:** problem toasts for send failures (`ComposeWindow`'s catch).
- **03:** the compose translation lane (`ComposeTranslator`, SHOULD).
- **04:** the pending-message lifecycle (contract §2.6): every `Pending` message reaches `Translated`, `Failed` or
  `Skipped`, so the overlay never needs a timeout of its own.
- **05:** `OverlayWindow.Reposition` in physical pixels; 05 left the `WDA_EXCLUDEFROMCAPTURE` question to this area.
- **07a:** the privacy gate (overlay lines can end `Failed` with `PrivacyText.TranslationOff`).
- **07b:** names and tab order in `ComposeWindow`; overlay card alpha through `ContrastMath`; palette keys only in
  `OverlayWindow.xaml`; `check_a11y.py`.

### What is weak or missing for production

Small, frequent annoyances in the two windows used in game: no reply recall, no length feedback, chip order that
ignores the language you are writing to, an overlay that cannot be made compact, and messages that cannot be copied.

## Goals / Non-goals

**Goals**

- Reply box: **history** with Up/Down and a **length counter** that measures exactly what will be sent.
- Overlay: an optional **compact layout**, applied live.
- SHOULD: per-target-language chip ordering by use, and per-message copy in overlay edit mode.
- Logic in `Envoy.Core` with unit tests; both overlay layouts in the smoke test under every palette.

**Non-goals**

- Game-specific chat length limits. They are not known; the warning limit is user-configurable.
- Persisting reply history. It stays in memory, so no chat text is written to disk unless transcripts are on.
- Hiding the overlay from screen capture (`WDA_EXCLUDEFROMCAPTURE`); see Risks for the decision.
- Accessibility attributes (07b) and notices (07a).

## Requirements

### Reply box

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUXC-39 | MUST | `ReplyHistory` (capacity 50, newest first; an exact duplicate moves to the front; blank ignored) records the user's typed input on each successful send or copy. It lives in memory only, on the reused `ComposeWindow`, and is never written to disk or logged. |
| REQ-OUXC-40 | MUST | In the reply box, Up recalls the next older entry (saving the unsent draft on the first press) and Down the next newer one. Moving past the newest restores the draft. Typing while browsing ends browsing and keeps the text. |
| REQ-OUXC-41 | MUST | When `Compose.ShowCharacterCount` is on (default), the reply box shows the length of `OutgoingText.Sanitize(AppendOriginal(translated))` (contract §8.8), counted in text elements. It shows "n / limit" in `Danger` when it exceeds `Compose.MaxMessageLength` (0 = no limit, the default), and says when the text will be cut at 08a's hard cap. Sending is never blocked. The Replying page has the toggle and the limit box. |
| REQ-OUXC-42 | SHOULD | With `Compose.PhraseOrder = MostUsed` (default), chips with a translation in the current target language come first, then the rest; within each group by use count for that target language, descending, then phrasebook order. `BookOrder` keeps today's order. |
| REQ-OUXC-43 | SHOULD | A send or copy whose input exactly matches a phrase (`PhraseBook.FindByText`) increments `Compose.PhraseUsage[target][phraseId]`. Counts saturate at 999, and each language keeps at most 100 ids (lowest dropped). |

### Overlay

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-OUXC-44 | MUST | With `Overlay.Compact` on, each message renders on one wrapped line (badge, sender, translation) with no original, provider or "translating…" line. A pending message shows a trailing "…" and a failed one a trailing "⚠" in `Danger`. Padding is `8,3`, the gap 2 px, and the font size `max(11, FontSize - 2)`. A "Compact layout" checkbox on the Overlay page toggles it live, and both layouts render in the smoke test under every `ThemeManager.PaletteIds` entry. *(Absorbs REQ-OUX-46.)* |
| REQ-OUXC-45 | SHOULD | In overlay edit mode each message card shows a Copy button that puts `OverlayCopyText.Format(message)` on the clipboard (sender and translation, and the original on a second line when different). For 1.5 s it shows "Copied" or "Couldn't copy". |
| REQ-OUXC-46 | — | Folded into REQ-OUXC-44. |

**MUST count: 4** (REQ-OUXC-39, 40, 41, 44), matching contract §9.

## Design

### Settings (`src/Envoy.Core/Settings/AppSettings.cs`, shared file)

```csharp
// OverlaySettings
public bool Compact { get; set; }                                // false

// ComposeSettings
public bool ShowCharacterCount { get; set; } = true;
public int MaxMessageLength { get; set; }                       // 0 = no limit known
public PhraseOrder PhraseOrder { get; set; } = PhraseOrder.MostUsed;            // SHOULD
/// <summary>Target language → phrase id → times sent. Phrase ids only, never free text. UI thread only.</summary>
public Dictionary<string, Dictionary<string, int>> PhraseUsage { get; set; } = new();   // SHOULD

public enum PhraseOrder { MostUsed, BookOrder }
```

Contract §7:

- Additive with defaults. `Compact = false` keeps today's overlay. The counter is new UI but changes nothing sent.
  `MostUsed` with empty usage gives "translated first, then book order", a deliberate small change from today.
- `PhraseUsage` is UI-thread-only state, read and written only by `ComposeWindow` (contract §7 names it). It holds
  phrase ids, not names, chat text or positions, so it needs no `[Personal]` marker.
- No data file is added; `DataInventory` is unchanged.

### Core (`src/Envoy.Core/Compose/`, new; `src/Envoy.Core/Pipeline/OverlayCopyText.cs`, new)

```csharp
namespace Envoy.Core.Compose;

public sealed class ReplyHistory(int capacity = 50)
{
    public IReadOnlyList<string> Entries { get; }   // newest first
    public bool IsBrowsing { get; }
    public void Add(string text);                   // trim; ignore blank; remove equal (Ordinal); insert at 0; cap; ends browsing
    public string? Older(string currentText);       // first call saves currentText as draft; null at oldest or when empty
    public string? Newer();                         // newer entry; past newest → draft and ends browsing; null if not browsing
    public void EndBrowsing();                      // called when the user types
}

public sealed record ReplyLengthInfo(int Count, int Limit, bool OverLimit, bool WillBeCut, string Text);

public static class ReplyLength
{
    /// sent = OutgoingText.Sanitize(composed). Count = StringInfo.LengthInTextElements(sent) (an emoji or a CJK
    /// character = 1). WillBeCut = OutgoingText.Sanitize(composed, int.MaxValue).Length > sent.Length.
    /// OverLimit = WillBeCut || (limit > 0 && Count > limit). Text from ReplyText.
    public static ReplyLengthInfo Measure(string composed, int limit);
}

public static class ReplyText                        // contract §8.7
{
    public const string Count = "{count}";
    public const string CountOfLimit = "{count} / {limit}";
    public const string WillBeCut = "{count} · longer text is cut";
}

public static class PhraseOrdering                   // SHOULD
{
    public const int MaxCount = 999, MaxIdsPerLanguage = 100;
    public static IReadOnlyList<Phrase> Order(IReadOnlyList<Phrase> phrases, string targetLanguage,
        IReadOnlyDictionary<string, Dictionary<string, int>> usage, PhraseOrder mode);
    /// Increments usage[Normalize(target)][phraseId], saturating at MaxCount; trims the lowest counts beyond MaxIdsPerLanguage.
    public static void RecordUse(Dictionary<string, Dictionary<string, int>> usage, string targetLanguage, string phraseId);
}
```

```csharp
namespace Envoy.Core.Pipeline;
public static class OverlayCopyText                  // SHOULD
{
    /// "{Sender}: {DisplayText}" (no prefix without a sender), plus "\n{Chat.Text}" when Translated and the original
    /// differs; Pending, Failed and Skipped give the original only.
    public static string Format(IncomingMessage message);
}
```

### App: reply box (`Windows/ComposeWindow.xaml(.cs)`)

- **History.** `internal ReplyHistory History { get; } = new();` on `ComposeWindow`, which `App` reuses for the whole
  session. `OnInputKey` handles `Key.Up` with `if (History.Older(Input.Text) is { } t) SetInputFromHistory(t)` and
  `Key.Down` with `Newer()`, both `e.Handled = true`. `SetInputFromHistory` sets `_recalling`, sets the text, moves the
  caret to the end and clears the flag. `OnInputChanged` calls `History.EndBrowsing()` when `!_recalling`.
- **On successful send or copy**, before `Hide()`: `History.Add(Input.Text)`; (SHOULD) if
  `_host.Phrases.FindByText(Input.Text, MyLanguage)` matches, `PhraseOrdering.RecordUse(...)` and `_host.ScheduleSave()`.
  A send blocked by 08a's `SendTargetGuard` or failed (02's problem toast) adds nothing.
- **Counter.** A `TextBlock x:Name="CharCount"` in the footer, left of the buttons, `Text.Caption` style,
  `AutomationProperties.Name="Message length"`, `AutomationProperties.LiveSetting="Polite"`. After each translation and
  on an `AppendOriginal` change it shows `ReplyLength.Measure(AppendOriginal(_translated), Compose.MaxMessageLength)`;
  `Foreground` uses `SetResourceReference(ForegroundProperty, info.OverLimit ? "Danger" : "Text.Muted")`. It is
  collapsed when `ShowCharacterCount` is off or nothing has been translated yet.
- **Chips** (SHOULD). `FillPhrases` orders with `PhraseOrdering.Order(_host.Phrases.All, Target, Compose.PhraseUsage, Compose.PhraseOrder)`.
- **Clipboard.** `SetClipboard` → `ClipboardHelper.TrySetText` (below). Behaviour is unchanged.
- **`Pages/ReplyPage.xaml`:** "Show message length" `CheckBox` → `Compose.ShowCharacterCount`; "Warn above"
  `TextBox` → `Compose.MaxMessageLength`, caption "0 = no limit. Envoy doesn't know each game's limit; set it if long
  messages get cut off."; (SHOULD) "Quick phrase order" `ComboBox` (Most used for this language / Phrasebook order).
  Every control has `AutomationProperties.Name` (07b's lint).

### App: overlay (`Windows/OverlayWindow.xaml(.cs)`, `OverlayItem.cs`)

- **`OverlayWindow.xaml`:** move today's item `DataTemplate` to `Window.Resources` as `x:Key="FullItem"`, and add
  `x:Key="CompactItem"` (REQ-OUXC-44) using the same card brush as `FullItem` (07b's readable alpha) and palette keys
  only. `ApplySettings` sets `Items.ItemTemplate = (DataTemplate)FindResource(S.Compact ? "CompactItem" : "FullItem")`.
- (SHOULD) Both templates contain a Copy button (`Button.Ghost`, `Font.Icons` glyph `&#xE8C8;`,
  `AutomationProperties.Name="{Binding Sender, StringFormat='Copy message from {0}'}"`), visible only while the
  window's new `IsEditing` property is true, with a command bound to `OverlayItem.CopyCommand`, plus a feedback
  `TextBlock` bound to `OverlayItem.CopyFeedback`. The `EditBar` text becomes "Drag to move · edge to resize ·
  ⧉ copies a message · Done to lock".
- **`OverlayWindow.xaml.cs`:** `IsEditing` (notify), set from `ToggleEditMode`; `CompactFontSize => Math.Max(11, S.FontSize - 2)`.
  05's `Reposition` code is untouched.
- **`OverlayItem.cs`** (SHOULD): `CopyFeedback` (string, notify), `CopyCommand` →
  `ClipboardHelper.TrySetText(OverlayCopyText.Format(Message))` → "Copied" or "Couldn't copy", cleared by a 1.5 s
  `DispatcherTimer`.
- **`Services/ClipboardHelper.cs`** (new): `public static bool TrySetText(string text)`, today's 5 × 40 ms COM retry
  moved from `ComposeWindow`, now returning success. Overlay copy is not "text typed into another window", so it does
  not go through `OutgoingText` (08a's rule covers typing and the reply box's paste path, which still sanitises).
- **`Pages/OverlayPage.xaml`:** "Compact layout" `CheckBox` → `Overlay.Compact`, next to 07b's "Keep text readable".
  The change reaches the overlay through `SettingsAutoApplier` → `ApplySettings` → the existing `SettingsApplied`
  subscription.

### Allowlists and policy (contract §5)

- **P/Invoke additions:** none. **Network:** none. **Process launching:** none.
- The `WDA_EXCLUDEFROMCAPTURE` decision (Risks) adds nothing. If it is ever built, it adds
  `user32!SetWindowDisplayAffinity` to `Native.cs` and `policy.json → native` in the same PR.

### Threading

- `ReplyHistory`, `PhraseOrdering` and `PhraseUsage` are UI-thread only (the compose window). `ReplyLength` and
  `OverlayCopyText` are pure.
- `ClipboardHelper` runs on the UI thread (STA), as today.

### Failure behaviour

| Situation | Behaviour |
| --- | --- |
| Clipboard locked | Overlay copy shows "Couldn't copy". The compose window behaves as today. |
| Provider returns a huge translation | The counter says the text will be cut; 08a's sanitiser caps it at send. |
| Send blocked or failed | Nothing is added to history or usage; 02's problem toast explains. |
| Message never leaves `Pending` | Cannot happen after 04 (contract §2.6); compact mode shows "…" until it does. |

## Acceptance criteria

| Id | REQ | Verified by |
| --- | --- | --- |
| AC-OUXC-1 | 39 | Unit `ReplyHistoryTests.Add_ignores_blank_and_moves_duplicate_to_front`, `Capacity_drops_oldest`. Typecheck: a `typecheck.sh` grep fails if `ReplyHistory` appears in `SettingsStore.cs`, `AppSettings.cs` or any logging call in `ComposeWindow.xaml.cs`. |
| AC-OUXC-2 | 40 | Unit `ReplyHistoryTests.Older_walks_back_and_stops_at_oldest`, `Newer_past_newest_restores_draft`, `Add_and_EndBrowsing_reset_cursor`, `Newer_without_browsing_returns_null`. Smoke `compose-history` (first palette, other-windows phase): `History.Add("gg wp")`; `Open`; type "dr"; raise the key event the input handler listens to with `Key.Up` → `Input.Text == "gg wp"`; `Key.Down` → `"dr"`. M-OUXC-1. |
| AC-OUXC-3 | 41 | Unit `ReplyLengthTests.Counts_text_elements` ("👍🏽" = 1, "你好" = 2, "é" = 1), `Counts_sanitised_text` ("gl\r\nhf" = 5), `Limit_zero_never_over`, `Over_limit_flagged_with_slash_text`, `Text_over_hard_cap_will_be_cut`. Smoke `compose-history`: `CharCount` exists and is collapsed before any translation. M-OUXC-1. |
| AC-OUXC-4 | 44 | Smoke `overlay-compact({palette})` (other-windows phase, every palette): `Compact = true`, `ApplySettings()`, demo messages; `Items.ItemTemplate` is `CompactItem`; each realised card has one text line element; with `--screenshots`, `{palette}-overlay-compact.png`; `Compact = false` restores `FullItem`. Typecheck: `check_a11y.py` passes on the new templates. M-OUXC-2 (live toggle in game). |

SHOULD items: `PhraseOrderingTests.*` (42, 43), `OverlayCopyTextTests.*` and the smoke edit-mode copy-button count
(45), M-OUXC-3.

## Test plan

### Unit tests (`tests/Envoy.Core.Tests/Compose/`, `Pipeline/`)

| File | Tests |
| --- | --- |
| `ReplyHistoryTests.cs` | the six tests in AC-OUXC-1 and AC-OUXC-2 |
| `ReplyLengthTests.cs` | the five tests in AC-OUXC-3 |
| `PhraseOrderingTests.cs` (SHOULD) | `Book_order_unchanged`, `Translated_first_then_untranslated`, `Most_used_first_within_group_ties_keep_book_order`, `Usage_is_per_target_language`, `RecordUse_normalises_language_saturates_and_caps` |
| `OverlayCopyTextTests.cs` (SHOULD) | `Translated_includes_sender_translation_and_original`, `No_sender_no_prefix`, `Original_omitted_when_identical`, `Pending_failed_and_skipped_use_original` |

### Smoke-test sections (contract §6, per palette, step 5 "other windows")

1. `overlay-compact({palette})` as in AC-OUXC-4, after the existing full-layout overlay check; then, in edit mode
   (SHOULD), the visible copy-button count equals the item count.
2. `compose-history` (first palette only) as in AC-OUXC-2, inside the existing compose block, before the compose
   preview poll.

Estimated added time: under 0.5 s per palette.

### Manual pass (07c appends to `docs/releasing.md` under "Reply box and overlay"; 08b consolidates)

1. **M-OUXC-1** (REQ 39, 40, 41). In game, send two replies; open the reply box; Up, Up, Down recall them in order;
   set "Warn above" to 10 and type a long reply: the counter turns red and Send still works.
2. **M-OUXC-2** (REQ 44). Toggle "Compact layout" on the Overlay page during a match: the overlay switches without a
   restart; pending lines show "…" and then the translation.
3. **M-OUXC-3** (SHOULD). Send "gg" twice to a Korean target, reopen: the "gg" chip is first for Korean but not for
   Russian. `Ctrl+Shift+E` → ⧉ on a message → paste into Notepad shows "Sender: translation" and the original.

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Settings/AppSettings.cs` | `Overlay.Compact`; `Compose.ShowCharacterCount`, `MaxMessageLength`, `PhraseOrder`, `PhraseUsage`; `PhraseOrder` enum | **yes** |
| `src/Envoy.Core/Compose/{ReplyHistory,ReplyLength,ReplyText,PhraseOrdering}.cs` | new | |
| `src/Envoy.Core/Pipeline/OverlayCopyText.cs` | new (SHOULD) | |
| `tests/Envoy.Core.Tests/Compose/*`, `Pipeline/OverlayCopyTextTests.cs` | new | |
| `src/Envoy.App/Windows/ComposeWindow.xaml(.cs)` | history keys, counter, chip ordering, `ClipboardHelper` | yes (02, 08a, 07b) |
| `src/Envoy.App/Windows/OverlayWindow.xaml(.cs)`, `OverlayItem.cs` | `FullItem`/`CompactItem`, `IsEditing`, copy (SHOULD) | yes (05, 07b) |
| `src/Envoy.App/Services/ClipboardHelper.cs` | new | |
| `src/Envoy.App/Pages/ReplyPage.xaml`, `OverlayPage.xaml` | counter and order settings; compact checkbox | yes |
| `src/Envoy.App/SmokeTest.cs` | two sections | **yes** |
| `tools/typecheck/typecheck.sh` | the `ReplyHistory` persistence grep | yes (07b) |
| `docs/getting-started.md`, `docs/releasing.md`, `CHANGELOG.md` | reply history, counter, compact overlay; M-OUXC-1…3 | yes |

Estimated size: Core about 200 lines plus tests about 200; App about 200.

## Risks & open questions

- **Overlay capture exclusion (decision for 05's open question).** Not built for 1.0. `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`
  would stop GDI capture from reading Envoy's own overlay, but it would also hide the overlay from streams, recordings
  and screenshots that users want to share, and it needs a new P/Invoke. The default anchors keep the overlay off the
  chat area, and 05's Live view shows the problem if a user moves it there. Revisit if bug reports show self-capture.
- **Phrase usage in `settings.json`.** Only phrase ids and counts, never free text, so the "no chat on disk unless
  transcripts are on" promise holds. Would players want history across sessions? If so, add an opt-in later, off by
  default.
- **Counter vs. game limits.** Envoy does not know each game's limit; the user-set warning is the only signal, and the
  counter never blocks sending.
- **Compact density.** Very long translations still wrap. `MaxHeight` and `MaxMessages` keep the overlay bounded.

## Revision notes

Changes made when splitting `07-onboarding-ux-accessibility.md` and revising against `00-integration.md` and the set
critique:

- **Split (critique major: scope).** Reply box and overlay polish became their own small step that lands last of the
  07 parts. Ids keep their numbers with the `OUXC` prefix.
- **Counter measures what is sent (critique minor on 08; contract §8.8).** `ReplyLength.Measure` now measures
  `OutgoingText.Sanitize(AppendOriginal(text))` and reports when 08a's 1000-unit cap will cut the text.
- **Reply history location.** Moved from `AppHost` to the reused `ComposeWindow`. This removes a shared-file edit to
  `AppHost.cs`, a constructor-order question (contract §3), and any chance of the history reaching settings or logs.
  A typecheck grep enforces that.
- **Overlay readability and literal colours** moved to 07b (`EnsureReadableText`, palette keys), which owns those MUSTs
  in contract §9. 07c's compact template uses 07b's card brush.
- **`WDA_EXCLUDEFROMCAPTURE`.** 05 left this to 07c. Decided: not for 1.0, with the P/Invoke that a later change would
  need named in advance.
- **Pending lines.** Compact mode relies on 04's pending-message lifecycle (contract §2.6) instead of assuming lines
  can stay "translating…".
- **Smoke (contract §6).** The compact overlay runs under every `PaletteIds` entry in the other-windows stage. The
  history check runs once and drives the real key handler instead of test-only hooks.
- **Scope (contract §9).** MUSTs cut from 8 to 4. REQ-OUX-46 is folded into 44; 42, 43 and 45 are SHOULD. Every MUST has
  a unit test, a smoke section or a typecheck check, plus a numbered manual step.
- **Allowlists.** No P/Invoke, network or process-launch additions.
