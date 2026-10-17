namespace Envoy.Core.Compose;

/// <summary>The reply box's length counter (REQ-OUXC-41, contract §8.7). Filled only by <c>TextTemplate.Format</c>.</summary>
public static class ReplyText
{
    public const string Count = "{count}";
    public const string CountOfLimit = "{count} / {limit}";
    public const string WillBeCut = "{count} · longer text is cut";
}
