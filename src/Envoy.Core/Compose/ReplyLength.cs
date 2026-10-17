using System.Globalization;
using Envoy.Core.Security;
using Envoy.Core.Text;

namespace Envoy.Core.Compose;

/// <summary>What the counter shows: <paramref name="Count"/> text elements of what will be sent, and whether that is too long.</summary>
public sealed record ReplyLengthInfo(int Count, int Limit, bool OverLimit, bool WillBeCut, string Text);

/// <summary>Measures exactly what the reply box will send (REQ-OUXC-41, contract §8.8). Pure.</summary>
public static class ReplyLength
{
    /// <summary>
    /// Measures <c>OutgoingText.Sanitize(composed)</c> in text elements (an emoji with its modifier or a CJK character
    /// counts 1). <see cref="ReplyLengthInfo.WillBeCut"/> is true when 08a's hard cap will cut the text;
    /// <see cref="ReplyLengthInfo.OverLimit"/> when it will be cut or exceeds <paramref name="limit"/> (0 or less = no limit).
    /// </summary>
    public static ReplyLengthInfo Measure(string? composed, int limit)
    {
        var sent = OutgoingText.Sanitize(composed);
        var count = new StringInfo(sent).LengthInTextElements;
        var limitOrZero = Math.Max(0, limit);
        var willBeCut = OutgoingText.ExceedsHardMax(composed);
        var over = willBeCut || (limitOrZero > 0 && count > limitOrZero);
        var culture = CultureInfo.CurrentCulture;
        var text = willBeCut
            ? TextTemplate.Format(ReplyText.WillBeCut, culture, ("count", count))
            : limitOrZero > 0
                ? TextTemplate.Format(ReplyText.CountOfLimit, culture, ("count", count), ("limit", limitOrZero))
                : TextTemplate.Format(ReplyText.Count, culture, ("count", count));
        return new ReplyLengthInfo(count, limitOrZero, over, willBeCut, text);
    }
}
