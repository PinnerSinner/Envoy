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
}
