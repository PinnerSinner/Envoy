# Chat formats and notices

Envoy reads the chat area as lines of text. This page explains how it turns those lines into messages, how to tell it
which lines are game notices, how to describe a chat format it doesn't know, and how to send a recorded capture with
a bug report.

## How Envoy understands chat

Each capture goes through these steps (Reading Chat › **Understanding chat** has the switches):

1. **Cleaning.** Invisible characters (zero-width spaces, byte-order marks, the `�` replacement glyph, private-use and
   variation-selector characters) are removed and spaces are collapsed. With **Remove stray symbols that OCR reads from
   icons** on (the default), symbols that rank, civilisation or platform icons turn into at the start of a line
   (`■`, `©`, `•`, `|`, …) are removed, full-width letters and digits (`Ｐｌａｙｅｒ１`) become ordinary ones, and
   punctuation around a name is trimmed. Brackets such as a clan tag `[CLAN]` are kept, and Korean `ㅋㅋ`/`ㄱㄱ` stay
   as they are. A channel tag whose closing bracket OCR read as `J`, `I`, `l`, `1` or `|` (`[AllJ Bob: hi`) still
   counts as the channel when the word is one of the profile's channel words.
2. **Notices.** A line that matches one of your notice patterns is a game notice, even if it contains a `:`.
3. **Messages.** A line with a sender (`Name: text`, `[All] Name: text`, `Name (Team): text`, `12:04 Name: text`, and
   the full-width `：`) starts a message.
4. **Wrapped lines.** With **Join messages that wrap onto the next line** on (the default), a line without a sender
   that sits directly below a message joins it. "Directly below" means the gap is at most one typical line height and
   the line is about as tall as the one above; at most 4 lines join one message. Chinese and Japanese text is joined
   without a space, everything else with one space.
5. **Leftovers.** Lines without a sender and with nothing above them (usually the end of a message whose first line
   has scrolled out of the chat area) are "part of an earlier message". If Envoy has already seen that message, the
   leftover is ignored; otherwise it is shown once without a sender.

A message is the same message when its sender and text match (small OCR differences are allowed), whatever its
channel or timestamp. So two players writing `gg` are two messages, and one player writing `gg` twice, both visible at
once, is shown twice.

The joining limits (gap, height and line count) are provisional until they are checked against real captures before
1.0 (step M-CHAT-5 below).

## Notice patterns

Under **Also treat these lines as notices**, write one pattern per line. A pattern must match the **whole** line,
ignoring upper and lower case, and a leading timestamp such as `12:04` or `[12:04:33]` is allowed in front of it.

| Pattern | Matches |
| --- | --- |
| `*` | any run of characters, including none |
| `{player}` | a name of 1 to 32 characters |
| a space | one or more spaces |
| `re:` at the start | the rest of the line is a .NET regular expression |
| `#` at the start | a comment; the line is ignored |

Examples (illustrative; copy the exact wording your game shows):

```text
# AoE II, English client
*has resigned*
{player} advanced to the * Age
re:^Tribute: \d+ (gold|food|wood|stone)$
```

Notices are never translated and never change what Envoy thinks a player's language is. While **Hide game notices**
is on (the default) they are not shown at all; turn it off to see them in the overlay, untranslated.

A pattern Envoy can't use (for example a regular expression with a missing bracket) is skipped, and the card says
which line it is. A regular expression that takes longer than 50 ms on a line counts as "no match".

With **Treat lines that start with a player's name but have no ':' as notices** on (off by default), a line such as
`Zhang Wei advanced to the Castle Age` counts as a notice when it starts with your name or the name of a player seen
this match (3 or more letters). It can hide real chat that happens to start with a name, which is why it is off.

Envoy ships no built-in notice patterns: their exact wording differs by game and client language, and Envoy only
includes text that has been checked against the real game.

## chat-formats.json

Envoy's built-in **generic** format covers the usual Age of Empires chat styles in many languages. If your game or
client writes chat differently, create `chat-formats.json` in the Envoy folder (About › **Open settings folder**). Envoy
reads it at start-up and never writes it. **Chat format** on the Reading Chat page picks a profile; **Match the game**
uses the `games` mapping, and anything unknown uses `generic`.

```jsonc
{
  "profiles": [
    {
      "id": "aoe4-custom",               // required; a profile with an existing id replaces it
      "name": "AoE IV (my client)",       // shown in the Chat format list
      "inherits": "generic",              // fields left out come from this profile (default: generic)
      "channels": {                       // canonical channel → words the game shows in brackets
        "All": ["all", "global"],
        "Team": ["team", "squad"],
        "Observers": ["observers"],
        "Private": ["whisper"]
      },
      "brackets": ["[]", "()", "<>"],     // open/close pairs around a channel tag
      "separators": [":", "："],           // between the sender and the message (1 to 8 characters each)
      "timestamp": "^\\s*\\d{1,2}:\\d{2}\\s+", // a .NET regex for a leading timestamp, including the space after it
      "maxSenderLength": 32,              // 1 to 64
      "stitching": { "enabled": true, "maxGapFactor": 1.0, "heightTolerance": 0.5, "maxContinuationLines": 4 },
      "systemPatterns": {                 // notice patterns by language code, or "*" for every language
        "en": ["*has resigned*"],
        "*": []
      }
    }
  ],
  "games": { "aoe4": "aoe4-custom" }      // game id → profile id, used by "Match the game"
}
```

- Comments and trailing commas are allowed; property names are not case-sensitive; unknown properties are ignored.
- A field that isn't valid (a bad regular expression, an empty `separators` list, `maxGapFactor` outside 0.2 to 5,
  `heightTolerance` outside 0 to 5, `maxContinuationLines` outside 0 to 10, a notice pattern that can't be used) is replaced by
  the inherited value, and the Reading Chat card names the profile and field.
- A file that isn't valid JSON, or is larger than 256 KiB, is ignored as a whole and the built-in formats are used.
- A profile's notice patterns for your language and for `*` are added to your own list.
- Game ids: `aoe4`, `aoe2de`, `aoe3de`, `aom`, `aoe1de`, `aoe2hd`, `aoe3`, and `any` (no game detected).

Envoy reads the file again when it starts. `chat-formats.json` is listed in [privacy](privacy.md) and removed by
"Delete all Envoy data".

## Recorded captures

Reading Chat › Live view › **Copy as test capture** copies what OCR read from the current capture, with each line's
position, in the format below. It contains chat and player names: **anonymise it before you share it** (replace every
name and message with invented ones of a similar length and script, and keep the positions as they are).

```text
# Free comment lines start with '#'.
@me Hoplite                      ← your player name (optional)
@language en                     ← your language (default en)
@muted Spammer, Toxic99          ← optional
@players Zhang Wei, Kim          ← optional: names Envoy already knows
@set hideSystem=false stitch=true clean=true byName=true profile=generic
@pattern *resigned*              ← optional, repeatable notice patterns
@frame                           ← one capture; frames are one second apart
@pass zh-CN                      ← the OCR engine for the rows below (default: @language)
 10  28 | [全 部] 天 下 第 一 ： 这 张 地 图 的 金 矿 在 中 间 ， 我 们
 32  50 | 应 该 先 占 领 它
@expect                          ← what this frame should show; "(none)" for nothing
chat | All | 天下第一 | 这张地图的金矿在中间，我们应该先占领它
```

(The `←` notes are explanations here; a real file has no inline comments.)

- A row is `top bottom [left right] | text`, with the text exactly as the OCR engine returned it.
- An expect row is `kind | channel | sender | text`, where kind is `chat`, `system` (a notice) or `fragment` (part
  of an earlier message), and `-` means none.
- An `@set` inside a frame applies from that frame on. An unknown `@set` key is an error.

To contribute one: anonymise it, fill in `@expect`, save it as
`tests/Envoy.Core.Tests/Golden/Captures/real/<game>-<what>.capture.txt` and run `dotnet test`. The test prints the
rows Envoy actually produced, ready to compare.

## Manual release checks

These are part of the pre-release pass in [releasing](releasing.md).

- **M-CHAT-1** In AoE II: DE or AoE IV (English client), get a message long enough to wrap from an AI ally or a second
  account. The overlay shows one entry.
- **M-CHAT-2** Keep chatting until the wrapped message's first line scrolls out of the chat area. Nothing new appears.
- **M-CHAT-3** Add `*resign*` (or the client-language equivalent) to the notice list and resign an AI game. Nothing is
  translated, and the Translation usage counter does not move.
- **M-CHAT-4** While a translation is in flight (use a slow provider or pull the network), click **New match**, and
  separately pause capture. No overlay line stays on "translating…".
- **M-CHAT-5** (calibration, before 1.0) Collect at least 3 anonymised "Copy as test capture" fixtures from at least 2
  games into `Golden/Captures/real/`, fill in `@expect`, and run `dotnet test`. If any needs different gap, height or
  line-count limits, change the defaults (`StitchOptions` and the `generic` profile) in the same change, then remove
  "provisional" from this page and REQ-CHAT-5.
- **M-CHAT-6** On the Reading Chat page with a wrapped message visible, "Understood as" shows `(2 lines)`. "Copy as
  test capture" puts text on the clipboard that `CaptureFixture.Parse` accepts.
