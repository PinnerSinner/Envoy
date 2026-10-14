namespace Envoy.Core.Translation;

/// <summary>
/// Serves repeats from the runtime's <see cref="TranslationCache"/>: "gg", "gl hf" and spam repeat a lot, and every
/// repeat is free. Entries are keyed by <paramref name="cacheNamespace"/> (the primary provider) so switching provider
/// never serves the old provider's output (REQ-TRR-19).
/// </summary>
public sealed class CachingTranslator(ITranslator inner, TranslationCache cache, string cacheNamespace) : ITranslator
{
    /// <summary>A private cache with namespace "" (tests and back-compat).</summary>
    public CachingTranslator(ITranslator inner, int capacity = 500) : this(inner, new TranslationCache(capacity), "")
    {
    }

    public string Id => inner.Id;
    public bool IsMetered => inner.IsMetered;
    public TranslationCache Cache => cache;
    public string Namespace => cacheNamespace;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = TranslationCache.Key(cacheNamespace, request);
        if (cache.TryGet(key, out var hit)) return hit with { FromCache = true };

        var result = await inner.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        var persistable = !result.FromGlossary && request.Text.Trim().Length <= TranslationCache.MaxPersistableLength;
        cache.Set(key, result, persistable);
        return result;
    }
}
