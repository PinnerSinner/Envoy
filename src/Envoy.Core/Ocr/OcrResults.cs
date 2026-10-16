using System.Text;
using Envoy.Core.Languages;

namespace Envoy.Core.Ocr;

/// <summary>
/// One line of text as an OCR engine read it, with its position in the captured image (contract §2.7). Left and Right
/// are the leftmost and rightmost word edges (REQ-CHAT-4), or 0 when unknown.
/// </summary>
public sealed record OcrLine(string Text, double Top, double Bottom, double Left = 0, double Right = 0)
{
    public double Center => (Top + Bottom) / 2;
    public double Height => Bottom - Top;

    /// <summary>True when the line has a real vertical extent (spec 04 stitching uses it).</summary>
    public bool HasGeometry => Bottom > Top;

    /// <summary>
    /// Uniform synthetic geometry for callers and tests that only have strings: line i spans
    /// <c>[i·(lineHeight+gap), i·(lineHeight+gap)+lineHeight]</c>. A <paramref name="lineHeight"/> of 0 means unknown.
    /// </summary>
    public static IReadOnlyList<OcrLine> FromTexts(IEnumerable<string> texts, double lineHeight = 0, double gap = 0)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var step = lineHeight + gap;
        return texts.Select((t, i) => lineHeight > 0 ? new OcrLine(t, i * step, i * step + lineHeight) : new OcrLine(t, 0, 0)).ToList();
    }
}

/// <summary>Everything one OCR engine (one language) read from a capture.</summary>
public sealed record OcrPass(string LanguageCode, IReadOnlyList<OcrLine> Lines);

public static class OcrText
{
    /// <summary>
    /// Windows OCR puts a space between every Chinese/Japanese character ("你 好 吗"). Remove spaces that sit
    /// between two CJK characters, or between CJK and CJK punctuation, while keeping spaces in Korean and
    /// Latin text where they're meaningful.
    /// </summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == ' ' && sb.Length > 0 && i + 1 < text.Length && IsCjk(sb[^1]) && IsCjk(NextNonSpace(text, i)))
                continue;
            sb.Append(c);
        }
        return Collapse(sb.ToString()).Trim();
    }

    private static char NextNonSpace(string s, int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
        return i < s.Length ? s[i] : ' ';
    }

    private static bool IsCjk(char c)
    {
        var script = ScriptDetector.Classify(c);
        return script is Script.Han or Script.Kana || c is >= '　' and <= '〿' || c is >= '＀' and <= '￯';
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Combines the output of several OCR engines run over the same image. Windows OCR engines only read their own
/// script well: the English engine turns 你好 into junk, the Chinese engine reads English fine but not Hangul.
/// For each physical line we keep the reading from the engine that recognised the most non-Latin characters
/// (the only way a non-Latin engine wins), falling back to the preferred (your-language) engine.
/// </summary>
public static class OcrMerger
{
    /// <summary>The picked text of each row, top to bottom: <see cref="MergeLines"/> without the geometry.</summary>
    public static IReadOnlyList<string> Merge(IReadOnlyList<OcrPass> passes, string preferredLanguage) =>
        MergeLines(passes, preferredLanguage).Select(l => l.Text).ToList();

    /// <summary>
    /// One line per physical row, top to bottom (REQ-CAP-42): the picked text, and the median <c>Top</c>/<c>Bottom</c>
    /// of every engine that read that row. <c>Left</c>/<c>Right</c> are the medians of the readings that have them
    /// (REQ-CHAT-4), else 0.
    /// </summary>
    public static IReadOnlyList<OcrLine> MergeLines(IReadOnlyList<OcrPass> passes, string preferredLanguage)
    {
        ArgumentNullException.ThrowIfNull(passes);
        var candidates = passes
            .SelectMany(p => p.Lines.Select(l => (Pass: p, Line: l with { Text = OcrText.Clean(l.Text) })))
            .Where(c => c.Line.Text.Length > 0)
            .OrderBy(c => c.Line.Center)
            .ToList();

        var groups = new List<List<(OcrPass Pass, OcrLine Line)>>();
        foreach (var c in candidates)
        {
            var group = groups.LastOrDefault();
            if (group is not null && SameRow(group[0].Line, c.Line)) group.Add(c);
            else groups.Add([c]);
        }

        return groups.Select(g => new OcrLine(Pick(g, preferredLanguage), Median(g.Select(c => c.Line.Top)), Median(g.Select(c => c.Line.Bottom)),
            Median(g.Where(c => c.Line.Right > c.Line.Left).Select(c => c.Line.Left)),
            Median(g.Where(c => c.Line.Right > c.Line.Left).Select(c => c.Line.Right)))).ToList();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static bool SameRow(OcrLine a, OcrLine b)
    {
        var tolerance = Math.Max(Math.Min(a.Height, b.Height) * 0.5, 2);
        return Math.Abs(a.Center - b.Center) <= tolerance;
    }

    private static string Pick(List<(OcrPass Pass, OcrLine Line)> group, string preferredLanguage)
    {
        var best = group
            .Select(c => (c.Line.Text, Score: ScriptScore(c.Line.Text, c.Pass.LanguageCode), Preferred: LanguageCatalog.SameLanguage(c.Pass.LanguageCode, preferredLanguage)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Preferred)
            .ThenByDescending(x => x.Text.Length)
            .First();
        return best.Text;
    }

    /// <summary>Count of characters in the engine's own (non-Latin) script; Latin engines score 0.</summary>
    private static int ScriptScore(string text, string languageCode)
    {
        var lang = LanguageCatalog.Find(languageCode);
        if (lang is null || lang.Script == Script.Latin) return 0;
        return text.Count(ch =>
        {
            var s = ScriptDetector.Classify(ch);
            return s == lang.Script || (lang.Script == Script.Kana && s == Script.Han);
        });
    }
}
