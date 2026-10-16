namespace Envoy.Core.Pipeline;

/// <summary>Text the chat pipeline writes into messages (contract §8.7).</summary>
public static class PipelineText
{
    /// <summary>
    /// <see cref="IncomingMessage.Error"/> of a translation cancelled by a new match, a region change or exit. The overlay
    /// never shows it; the transcript and the log do.
    /// </summary>
    public const string Cancelled = "cancelled";
}
