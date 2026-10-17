namespace Envoy.Core.Pipeline;

/// <summary>What the overlay's Copy button puts on the clipboard (REQ-OUXC-45). Pure.</summary>
public static class OverlayCopyText
{
    /// <summary>Shown next to the Copy button for 1.5 s.</summary>
    public const string Copied = "Copied";
    public const string CopyFailed = "Couldn't copy";

    /// <summary>
    /// "Sender: translation" (no prefix without a sender), plus the original on a second line when the message is
    /// translated and the original differs. Pending, failed and untranslated (skipped) messages give the original only.
    /// </summary>
    public static string Format(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var prefix = string.IsNullOrEmpty(message.Chat.Sender) ? "" : message.Chat.Sender + ": ";
        if (message.Status != IncomingStatus.Translated || string.IsNullOrEmpty(message.Translation))
            return prefix + message.Chat.Text;
        return string.Equals(message.Translation, message.Chat.Text, StringComparison.Ordinal)
            ? prefix + message.Translation
            : $"{prefix}{message.Translation}\n{message.Chat.Text}";
    }
}
