using Envoy.Core.Text;

namespace Envoy.Core.Translation;

/// <param name="Text">Text to translate.</param>
/// <param name="TargetLanguage">Envoy language code to translate into.</param>
/// <param name="SourceLanguage">Envoy language code, or null to let the provider detect it.</param>
public sealed record TranslationRequest(string Text, string TargetLanguage, string? SourceLanguage = null);

/// <param name="DetectedSourceLanguage">Envoy language code the provider detected, if it reported one.</param>
public sealed record TranslationResult(
    string Text,
    string? DetectedSourceLanguage,
    string ProviderId,
    bool FromCache = false,
    bool FromGlossary = false);

public interface ITranslator
{
    /// <summary>Stable id stored in settings, e.g. "google-free".</summary>
    string Id { get; }

    /// <summary>True when every character sent costs the user money (or free-tier quota).</summary>
    bool IsMetered { get; }

    /// <summary>
    /// Translates <paramref name="request"/>. Throws only <see cref="TranslationException"/> (with a
    /// <see cref="TranslationException.Kind"/>), or <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> was cancelled (REQ-TRR-1).
    /// </summary>
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A translation failure. <see cref="Exception.Message"/> is a whole sentence from <see cref="TranslationErrorText"/>
/// (provider name plus what happened): never a response body, raw exception text, chat text or a key (REQ-TRR-1, 17).
/// Optional parameters only, so <c>new TranslationException(msg)</c> and <c>(msg, inner)</c> compile unchanged.
/// </summary>
public class TranslationException(
    string message,
    Exception? inner = null,
    TranslationErrorKind kind = TranslationErrorKind.Unknown,
    int? statusCode = null,
    TimeSpan? retryAfter = null) : Exception(message, inner)
{
    public TranslationErrorKind Kind { get; } = kind;

    /// <summary>The HTTP status (or SDK status) that caused the failure, if there was one.</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>The provider's <c>Retry-After</c>, clamped to 0–3600 s (REQ-TRR-2).</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Envoy's own daily character cap for a metered provider was reached (kind <see cref="TranslationErrorKind.QuotaExceeded"/>).</summary>
public sealed class BudgetExceededException(string providerId, int limit)
    : TranslationException(
        TextTemplate.Format(TranslationErrorText.BudgetExceeded, null, ("limit", limit), ("provider", ProviderCatalog.NameOf(providerId))),
        kind: TranslationErrorKind.QuotaExceeded)
{
    public string ProviderId { get; } = providerId;
    public int Limit { get; } = limit;
}
