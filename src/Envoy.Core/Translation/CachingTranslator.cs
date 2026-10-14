namespace Envoy.Core.Translation;

/// <summary>
/// Serves repeats from the runtime's <see cref="TranslationCache"/>: "gg", "gl hf" and spam repeat a lot, and every
/// repeat is free. Entries are keyed by <paramref name="cacheNamespace"/> (the primary provider) so switching provider
/// never serves the old provider's output (REQ-TRR-19).
/// </summary>
/// <param name="providerId">
/// When set, only results served by this provider (the primary) are stored, so a fallback's output is never kept under
/// the primary's namespace and served after the primary works again. Null stores every result.
/// </param>
/// <param name="persist">
/// False keeps this translator's entries in memory only, never in the saved-translations file (the reply box: its
/// back-translations are your own words, spec 03 §6).
/// </param>
public sealed class CachingTranslator(ITranslator inner, TranslationCache cache, string cacheNamespace, string? providerId = null,
                                      bool persist = true) : ITranslator
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
        if (providerId is not null && !string.Equals(result.ProviderId, providerId, StringComparison.Ordinal)) return result;
        var persistable = persist && !result.FromGlossary && request.Text.Trim().Length <= TranslationCache.MaxPersistableLength;
        cache.Set(key, result, persistable);
        return result;
    }
}
