using Envoy.Core.Privacy;

namespace Envoy.Core.Translation;

/// <summary>
/// The outermost translator (contract §8.1, REQ-OUXA-47): while <paramref name="allowed"/> is false every call fails
/// with <see cref="TranslationErrorKind.Configuration"/> and <see cref="PrivacyText.TranslationOff"/> before the inner
/// translator runs, so no text reaches any provider, health, breaker or cache. <paramref name="allowed"/> reads a
/// volatile flag; this runs on pool threads.
/// </summary>
public sealed class PrivacyGatedTranslator(ITranslator inner, Func<bool> allowed) : ITranslator
{
    private readonly ITranslator _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<bool> _allowed = allowed ?? throw new ArgumentNullException(nameof(allowed));

    public string Id => _inner.Id;

    public bool IsMetered => _inner.IsMetered;

    /// <summary>The translator this gate wraps.</summary>
    public ITranslator Inner => _inner;

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        // Faulted tasks rather than synchronous throws, like an async method, so callers that store the task see the same.
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<TranslationResult>(cancellationToken);
        if (!_allowed())
            return Task.FromException<TranslationResult>(new TranslationException(PrivacyText.TranslationOff, kind: TranslationErrorKind.Configuration));
        return _inner.TranslateAsync(request, cancellationToken);
    }
}
