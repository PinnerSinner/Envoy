using Envoy.Core.Chat;

namespace Envoy.Core.Pipeline;

public enum IncomingStatus
{
    /// <summary>Translation in flight.</summary>
    Pending,
    Translated,
    /// <summary>Already in your language; shown as-is.</summary>
    Untranslated,
    Failed,
}

/// <summary>A chat message as shown in the overlay. Mutable so the overlay can show it before translation finishes.</summary>
public sealed class IncomingMessage(ChatMessage chat, DateTimeOffset receivedAt)
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChatMessage Chat { get; } = chat;
    public DateTimeOffset ReceivedAt { get; } = receivedAt;

    public IncomingStatus Status { get; internal set; } = IncomingStatus.Pending;
    public string? Translation { get; internal set; }
    public string? SourceLanguage { get; internal set; }
    public string? ProviderId { get; internal set; }
    public bool FromGlossary { get; internal set; }
    public string? Error { get; internal set; }

    /// <summary>
    /// True when the translation was cancelled by a new match, a region change or exit (contract §2.6). The message is
    /// then <see cref="IncomingStatus.Failed"/> with <see cref="PipelineText.Cancelled"/>, and the overlay shows the
    /// original line with no error row.
    /// </summary>
    public bool WasCancelled { get; internal set; }

    /// <summary>English meaning of a slang message, waiting to be translated into a non-English reader's language.</summary>
    internal string? GlossaryMeaning { get; set; }

    /// <summary>A ready-made translated message, used for the overlay preview and demo.</summary>
    public static IncomingMessage Preview(string sender, string original, string translation, string sourceLanguage) =>
        new(new ChatMessage($"{sender}: {original}", "All", sender, original), DateTimeOffset.UtcNow)
        {
            Status = IncomingStatus.Translated,
            Translation = translation,
            SourceLanguage = sourceLanguage,
            ProviderId = "preview",
        };

    /// <summary>The text to show: the translation if there is one, otherwise the original.</summary>
    public string DisplayText => Translation ?? Chat.Text;
}
