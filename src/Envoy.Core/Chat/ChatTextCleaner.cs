using System.Globalization;
using System.Text;

namespace Envoy.Core.Chat;

/// <summary>
/// Removes what OCR reads from things that aren't text: invisible characters, replacement glyphs and the rank,
/// civilisation or platform icons next to names (REQ-CHAT-35 to 39). Runs before parsing, so stray glyphs never become
/// part of a sender name.
/// </summary>
public static class ChatTextCleaner
{
    /// <summary>Characters treated as icon junk besides the Unicode symbol categories (REQ-CHAT-36).</summary>
    private const string JunkCharacters = "•·»›■□●○◆◇▶►|*#~";

    private const string OpeningBrackets = "[(<{【「『（［〈《";
    private const string ClosingBrackets = "])>}】」』）］〉》";

    /// <summary>
    /// Removes control and format characters (zero-width characters, BOM), U+FFFD, private-use characters, variation
    /// selectors and lone surrogates, collapses whitespace and trims (always). With <paramref name="symbols"/>, also
    /// folds full-width ASCII letters and digits and strips leading icon junk before the channel or sender.
    /// </summary>
    public static string Clean(string line, bool symbols = true)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = RemoveInvisible(line);
        if (symbols)
        {
            text = FoldFullWidth(text);
            text = StripLeadingJunk(text);
        }
        return text;
    }

    /// <summary>
    /// Trims punctuation and symbols around a sender, keeping balanced bracket pairs ("[CLAN] Bob") and underscores
    /// (REQ-CHAT-38). Returns the trimmed input when nothing would be left.
    /// </summary>
    public static string CleanSender(string sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var s = sender.Trim();
        var start = 0;
        var end = s.Length;
        while (start < end && IsTrimmable(s[start]) && !OpeningBrackets.Contains(s[start], StringComparison.Ordinal)) start++;
        while (end > start && IsTrimmable(s[end - 1]) && !IsBalancedCloser(s, start, end - 1)) end--;
        var result = s[start..end].Trim();
        return result.Length > 0 ? result : s;
    }

    /// <summary>True for the characters <see cref="Clean"/> removes as icon junk at the start of a line.</summary>
    internal static bool IsJunk(char c)
    {
        if (OpeningBrackets.Contains(c, StringComparison.Ordinal)) return false;
        if (JunkCharacters.Contains(c, StringComparison.Ordinal)) return true;
        return char.GetUnicodeCategory(c) is UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol
            or UnicodeCategory.ModifierSymbol or UnicodeCategory.CurrencySymbol;
    }

    private static bool IsTrimmable(char c) =>
        char.IsWhiteSpace(c) || char.IsSymbol(c) || (char.IsPunctuation(c) && char.GetUnicodeCategory(c) != UnicodeCategory.ConnectorPunctuation);

    private static bool IsBalancedCloser(string s, int start, int index)
    {
        var closer = ClosingBrackets.IndexOf(s[index], StringComparison.Ordinal);
        if (closer < 0) return false;
        var opener = OpeningBrackets[closer];
        return s.IndexOf(opener, start, index - start) >= 0;
    }

    private static string RemoveInvisible(string line)
    {
        var sb = new StringBuilder(line.Length);
        var pendingSpace = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, line[i + 1]);
                var category = CharUnicodeInfo.GetUnicodeCategory(cp);
                i++;
                if (category is UnicodeCategory.PrivateUse or UnicodeCategory.Format || cp is >= 0xE0100 and <= 0xE01EF) continue;
                AppendPending(sb, ref pendingSpace);
                sb.Append(c).Append(line[i]);
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (char.IsSurrogate(c) || c == '�' || c is >= '︀' and <= '️') continue;
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse) continue;
            AppendPending(sb, ref pendingSpace);
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void AppendPending(StringBuilder sb, ref bool pendingSpace)
    {
        if (pendingSpace && sb.Length > 0) sb.Append(' ');
        pendingSpace = false;
    }

    /// <summary>Full-width ASCII digits and letters to ASCII (REQ-CHAT-37). No NFKC: Hangul jamo such as ㅋㅋ stay.</summary>
    private static string FoldFullWidth(string text)
    {
        if (!text.Any(IsFullWidthAlnum)) return text;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = IsFullWidthAlnum(c) ? (char)(c - 0xFEE0) : c;
            }
        });
    }

    private static bool IsFullWidthAlnum(char c) => c is >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ';

    /// <summary>
    /// Strips runs of icon junk that are followed by whitespace at the start of the line, and again after a leading
    /// timestamp or bracketed tag (so "12:04 ■ Bob: hi" and "[All] © Bob: hi" lose the icon too).
    /// </summary>
    private static string StripLeadingJunk(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        for (var guard = 0; guard < 4 && i < text.Length; guard++)
        {
            i = SkipJunkRuns(text, i);
            var token = LeadingToken(text, i);
            if (token == 0) break;
            sb.Append(text, i, token);
            i += token;
        }
        sb.Append(text, i, text.Length - i);
        return sb.ToString().Trim();
    }

    /// <summary>Skips "junk+ whitespace+" groups from <paramref name="i"/>; returns where the real text starts.</summary>
    private static int SkipJunkRuns(string text, int i)
    {
        while (true)
        {
            var j = i;
            while (j < text.Length && IsJunk(text[j])) j++;
            if (j == i || j >= text.Length || text[j] != ' ') return i;
            while (j < text.Length && text[j] == ' ') j++;
            if (j >= text.Length) return i; // nothing but junk: leave the line alone
            i = j;
        }
    }

    /// <summary>Length of a leading timestamp or bracketed tag plus the following space at <paramref name="i"/>, or 0.</summary>
    private static int LeadingToken(string text, int i)
    {
        if (i >= text.Length) return 0;
        var j = i;
        var opener = OpeningBrackets.IndexOf(text[j], StringComparison.Ordinal);
        if (opener >= 0)
        {
            var close = text.IndexOf(ClosingBrackets[opener], j + 1);
            if (close < 0 || close - j > 24) return 0;
            j = close + 1;
        }
        else if (char.IsDigit(text[j]))
        {
            while (j < text.Length && (char.IsDigit(text[j]) || text[j] == ':')) j++;
            if (!text.AsSpan(i, j - i).Contains(':')) return 0;
        }
        else
        {
            return 0;
        }
        if (j >= text.Length || text[j] != ' ') return 0;
        while (j < text.Length && text[j] == ' ') j++;
        return j - i;
    }
}
