using System.Text;
using Envoy.Core.Languages;
using Envoy.Core.Ocr;

namespace Envoy.Core.Chat;

/// <summary>
/// Stitching limits (REQ-CHAT-5). Provisional until the release calibration step (M-CHAT-5) confirms them against real
/// captures.
/// </summary>
/// <param name="MaxGapFactor">Largest gap between a line and the one above it, in median line heights.</param>
/// <param name="HeightTolerance">Largest height difference between the two lines, in median line heights.</param>
/// <param name="MaxContinuationLines">Most lines joined below one head; further lines start a fragment.</param>
public sealed record StitchOptions(double MaxGapFactor = 1.0, double HeightTolerance = 0.5, int MaxContinuationLines = 4);

/// <summary>Everything <see cref="ChatCaptureAnalyzer"/> needs, as one immutable snapshot.</summary>
/// <param name="SystemPatterns">The user's notice patterns plus the profile's for your language and <c>*</c>.</param>
/// <param name="KnownPlayers">Your name and the names seen this match (used only by <paramref name="SystemByPlayerName"/>).</param>
/// <param name="Stitch">Join wrapped lines (the setting, and the profile's stitching switch).</param>
/// <param name="Profile">The chat format; null means <see cref="ChatFormatProfile.Generic"/>.</param>
public sealed record ChatContext(
    SystemLinePatterns SystemPatterns,
    IReadOnlyList<string> KnownPlayers,
    bool Stitch,
    StitchOptions Stitching,
    bool CleanSymbols,
    bool SystemByPlayerName,
    ChatFormatProfile? Profile)
{
    /// <summary>The defaults: no notice patterns, stitching and cleanup on, generic format.</summary>
    public static ChatContext Default { get; } = new(SystemLinePatterns.Empty, [], true, new StitchOptions(), true, false, null);
}

/// <summary>
/// Turns one capture's lines into messages (spec 04): cleans each line, recognises notices and chat heads, joins
/// wrapped continuation lines to the message above them using the line geometry, and turns orphaned lines into
/// fragments. Pure and stateless, so it is safe on any thread.
/// </summary>
public static class ChatCaptureAnalyzer
{
    public static IReadOnlyList<ChatMessage> Analyze(IReadOnlyList<OcrLine> lines, ChatContext context)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(context);
        var profile = context.Profile ?? ChatFormatProfile.Generic;

        var cleaned = new List<(OcrLine Line, string Text)>(lines.Count);
        foreach (var line in lines)
        {
            var text = ChatTextCleaner.Clean(line.Text ?? "", context.CleanSymbols);
            if (text.Length > 0) cleaned.Add((line, text));
        }
        var median = MedianHeight(cleaned.Select(c => c.Line));

        var entries = new List<Entry>();
        Entry? current = null;
        foreach (var (line, text) in cleaned)
        {
            if (context.SystemPatterns.IsMatch(text))
            {
                current = new Entry(ChatLineKind.System, null, null, profile.StripTimestamp(text).Trim(), text, line);
                entries.Add(current);
                continue;
            }

            var parsed = ChatLineParser.Parse(text, profile);
            if (parsed.Sender is { } sender)
            {
                var name = context.CleanSymbols ? ChatTextCleaner.CleanSender(sender) : sender;
                current = new Entry(ChatLineKind.Chat, parsed.Channel, name, parsed.Text, text, line);
                entries.Add(current);
                continue;
            }

            var withoutTime = profile.StripTimestamp(text).Trim();
            if (context.SystemByPlayerName && StartsWithKnownPlayer(withoutTime, context.KnownPlayers))
            {
                current = new Entry(ChatLineKind.System, null, null, withoutTime, text, line);
                entries.Add(current);
                continue;
            }

            // A continuation candidate: no sender and not a notice.
            if (parsed.Text.Length == 0) continue; // e.g. a lone "[All]"
            if (context.Stitch && current is not null && current.Continuations < context.Stitching.MaxContinuationLines
                && GeometryAllows(current.LastLine, line, median, context.Stitching))
            {
                current.Add(parsed.Text, text, line);
                continue;
            }
            current = new Entry(ChatLineKind.Fragment, null, null, parsed.Text, text, line);
            entries.Add(current);
        }

        return entries.Select(e => e.ToMessage()).ToList();
    }

    /// <summary>
    /// Joins a wrapped line to the text above it: no separator when the character before the join or the first one
    /// after it is Han, Kana, or CJK or full-width punctuation, otherwise one space (REQ-CHAT-7).
    /// </summary>
    internal static string Join(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        Rune.DecodeLastFromUtf16(a, out var last, out _);
        Rune.DecodeFromUtf16(b, out var first, out _);
        return IsCjkJoin(last) || IsCjkJoin(first) ? a + b : a + " " + b;
    }

    private static bool IsCjkJoin(Rune r)
    {
        if (!r.IsBmp) return r.Value is >= 0x20000 and <= 0x3FFFF; // CJK extension planes
        var c = (char)r.Value;
        return ScriptDetector.Classify(c) is Script.Han or Script.Kana
            || c is >= '　' and <= '〿'   // CJK symbols and punctuation
            || c is >= '＀' and <= '￯';  // half-width and full-width forms
    }

    /// <summary>
    /// The REQ-CHAT-5 geometry test. With no usable geometry (all heights zero, or either line without one), adjacency
    /// alone decides (REQ-CHAT-10).
    /// </summary>
    private static bool GeometryAllows(OcrLine previous, OcrLine line, double median, StitchOptions stitching)
    {
        if (median <= 0 || !previous.HasGeometry || !line.HasGeometry) return true;
        var gap = line.Top - previous.Bottom;
        if (gap < -0.5 * median || gap > stitching.MaxGapFactor * median) return false;
        return Math.Abs(line.Height - previous.Height) <= stitching.HeightTolerance * median;
    }

    private static double MedianHeight(IEnumerable<OcrLine> lines)
    {
        var heights = lines.Where(l => l.HasGeometry).Select(l => l.Height).Order().ToArray();
        if (heights.Length == 0) return 0;
        var mid = heights.Length / 2;
        return heights.Length % 2 == 1 ? heights[mid] : (heights[mid - 1] + heights[mid]) / 2;
    }

    /// <summary>
    /// REQ-CHAT-28: the line starts with a known player name (3+ normalised characters, compared by
    /// <see cref="TextSimilarity.OcrKey"/>) followed by whitespace.
    /// </summary>
    private static bool StartsWithKnownPlayer(string text, IReadOnlyList<string> players)
    {
        foreach (var player in players)
        {
            var name = player.Trim();
            var key = TextSimilarity.OcrKey(name);
            if (key.Length < 3 || text.Length <= name.Length || !char.IsWhiteSpace(text[name.Length])) continue;
            if (TextSimilarity.OcrKey(text[..name.Length]) == key) return true;
        }
        return false;
    }

    private sealed class Entry
    {
        private readonly ChatLineKind _kind;
        private readonly string? _channel;
        private readonly string? _sender;
        private readonly List<string> _raw = [];
        private readonly OcrLine _first;
        private string _text;

        public Entry(ChatLineKind kind, string? channel, string? sender, string text, string raw, OcrLine line)
        {
            _kind = kind;
            _channel = channel;
            _sender = sender;
            _text = text;
            _raw.Add(raw);
            _first = line;
            LastLine = line;
        }

        public OcrLine LastLine { get; private set; }
        public int Continuations => _raw.Count - 1;

        public void Add(string text, string raw, OcrLine line)
        {
            _text = Join(_text, text);
            _raw.Add(raw);
            LastLine = line;
        }

        public ChatMessage ToMessage() => new(string.Join('\n', _raw), _channel, _sender, _text)
        {
            Kind = _kind,
            LineCount = _raw.Count,
            Top = _first.Top,
            Bottom = LastLine.Bottom,
        };
    }
}
