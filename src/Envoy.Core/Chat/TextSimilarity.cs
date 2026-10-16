using System.Globalization;
using System.Text;

namespace Envoy.Core.Chat;

public static class TextSimilarity
{
    /// <summary>
    /// Strips what OCR tends to get wrong between frames (spacing, punctuation, case, look-alike glyphs)
    /// so the same chat line read twice compares equal.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            var c = char.ToLowerInvariant(ch);
            sb.Append(c switch
            {
                '0' => 'o',
                '1' or 'l' or '|' => 'i',
                '5' => 's',
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>1.0 for identical strings, 0.0 for completely different ones (normalised Levenshtein).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    public static int TextElementCount(string s) => new StringInfo(s).LengthInTextElements;

    /// <summary>Longest input <see cref="ContainsFuzzy"/> looks at; longer strings are cut.</summary>
    public const int MaxFuzzyLength = 400;

    /// <summary>
    /// True when <paramref name="needle"/> occurs in <paramref name="haystack"/> exactly, or some stretch of the haystack
    /// is at least <paramref name="threshold"/> similar to it (1 − edit distance ÷ needle length). Approximate substring
    /// matching (Sellers), so it costs O(haystack × needle) rather than a window per position. Inputs are capped at
    /// <see cref="MaxFuzzyLength"/> characters. Compare normalised strings.
    /// </summary>
    public static bool ContainsFuzzy(string haystack, string needle, double threshold)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(needle);
        if (needle.Length == 0) return false;
        if (haystack.Length > MaxFuzzyLength) haystack = haystack[..MaxFuzzyLength];
        if (needle.Length > MaxFuzzyLength) needle = needle[..MaxFuzzyLength];
        if (haystack.Contains(needle, StringComparison.Ordinal)) return true;

        var allowed = (int)Math.Floor((1 - threshold) * needle.Length + 1e-9);
        if (allowed <= 0 || haystack.Length < needle.Length - allowed) return false;

        // column[i] = the smallest edit distance between needle[..i] and any substring of the haystack ending here.
        var prev = new int[needle.Length + 1];
        var curr = new int[needle.Length + 1];
        for (var i = 0; i <= needle.Length; i++) prev[i] = i;
        for (var j = 1; j <= haystack.Length; j++)
        {
            curr[0] = 0; // a match may start anywhere in the haystack
            for (var i = 1; i <= needle.Length; i++)
            {
                var cost = needle[i - 1] == haystack[j - 1] ? 0 : 1;
                curr[i] = Math.Min(Math.Min(curr[i - 1] + 1, prev[i] + 1), prev[i - 1] + cost);
            }
            if (curr[needle.Length] <= allowed) return true;
            (prev, curr) = (curr, prev);
        }
        return false;
    }

    /// <summary>
    /// <see cref="Normalize"/> plus folding of the letter pairs OCR confuses in names: rn→m, vv→w, cl→d, q→g
    /// (REQ-CHAT-21). Digits beyond Normalize's 0/1/5 are not folded, so "Player1" and "Player2" stay distinct.
    /// </summary>
    public static string OcrKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = text.ToLowerInvariant()
            .Replace("rn", "m", StringComparison.Ordinal)
            .Replace("vv", "w", StringComparison.Ordinal)
            .Replace("cl", "d", StringComparison.Ordinal)
            .Replace('q', 'g');
        return Normalize(folded);
    }
}
