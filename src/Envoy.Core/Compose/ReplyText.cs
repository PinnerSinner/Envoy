using Envoy.Core.Text;

namespace Envoy.Core.Compose;

/// <summary>The reply box's length counter (REQ-OUXC-41, contract §8.7). Filled only by <c>TextTemplate.Format</c>.</summary>
public static class ReplyText
{
    public const string Count = "{count}";
    public const string CountOfLimit = "{count} / {limit}";
    public const string WillBeCut = "{count} · longer text is cut";

    /// <summary>The scribe's desk heading (spec 09a): the blackletter R, then the rest of the words.</summary>
    public const string HeadingTo = "Reply to {name}";
    public const string Heading = "Reply";
}

/// <summary>The reply box's heading, split for its drop cap (spec 09a, REQ-HRD-9): "R" + "eply to 龙骑士".</summary>
public sealed record ReplyHeading(string DropCap, string Rest, string AccessibleName)
{
    /// <summary>"Reply to {name}" for the last sender, or "Reply" when nobody has written yet.</summary>
    public static ReplyHeading For(string? sender)
    {
        var whole = string.IsNullOrWhiteSpace(sender)
            ? ReplyText.Heading
            : TextTemplate.Format(ReplyText.HeadingTo, null, ("name", sender.Trim()));
        return new ReplyHeading(whole[..1], whole[1..], whole);
    }
}
