using System.Text;

namespace Envoy.Core.Security;

/// <summary>
/// Cleans text before Envoy types or pastes it into another window (REQ-QSC-28). A stray newline would end the chat
/// line early and the rest would arrive with chat closed, where letters are game hotkeys; bidi overrides would make
/// the message read differently to other players. Pure, static and idempotent, so 07c's character counter can
/// measure exactly what will be sent.
/// </summary>
public static class OutgoingText
{
    /// <summary>A safety ceiling against runaway provider output, not a claim about any game's chat limit.</summary>
    public const int HardMaxLength = 1000;

    /// <summary>
    /// Removes C0/C1 control characters (CR, LF and TAB become a space), removes the bidi embedding, override and
    /// isolate controls U+202A–U+202E and U+2066–U+2069 and unpaired surrogates, collapses whitespace runs to one
    /// space, trims, and truncates to <paramref name="maxLength"/> (at most <see cref="HardMaxLength"/>) UTF-16 code
    /// units without splitting a surrogate pair. ZWJ/ZWNJ are kept (emoji, Indic scripts). Null gives "".
    /// </summary>
    public static string Sanitize(string? text, int maxLength = HardMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (string.IsNullOrEmpty(text)) return "";
        var max = Math.Min(maxLength, HardMaxLength);

        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var pendingSpace = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n' or '\t' || (char.IsWhiteSpace(c) && !IsUnsafe(c)))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (IsUnsafe(c)) continue;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) continue;
                AppendPendingSpace(sb, ref pendingSpace);
                sb.Append(c).Append(text[++i]);
            }
            else if (char.IsLowSurrogate(c))
            {
                continue;
            }
            else
            {
                AppendPendingSpace(sb, ref pendingSpace);
                sb.Append(c);
            }
            if (sb.Length > max) break;
        }

        if (sb.Length <= max) return sb.ToString();
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(sb[cut - 1])) cut--;
        return sb.ToString(0, cut).TrimEnd();
    }

    /// <summary>True for characters <see cref="Sanitize"/> removes or replaces: C0/C1 controls and bidi controls.</summary>
    public static bool IsUnsafe(char c) =>
        char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';

    private static void AppendPendingSpace(StringBuilder sb, ref bool pendingSpace)
    {
        if (pendingSpace) sb.Append(' ');
        pendingSpace = false;
    }
}
