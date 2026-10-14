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

    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
}

public class TranslationException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class BudgetExceededException(string providerId, int limit)
    : TranslationException($"Daily character limit of {limit:N0} reached for {providerId}. Raise it in Settings → Translation, or switch provider.")
{
    public string ProviderId { get; } = providerId;
}
