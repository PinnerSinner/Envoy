namespace Envoy.Core.Translation;

/// <summary>Tries each provider in order until one succeeds, so a rate-limited free endpoint never leaves you blind.</summary>
public sealed class FallbackTranslator(IReadOnlyList<ITranslator> chain) : ITranslator
{
    public string Id => chain.Count > 0 ? chain[0].Id : "none";
    public bool IsMetered => chain.Any(t => t.IsMetered);

    public IReadOnlyList<ITranslator> Chain => chain;

    /// <summary>Raised when a provider fails and the next one is tried.</summary>
    public event Action<string, Exception>? ProviderFailed;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (chain.Count == 0) throw new TranslationException("No translation provider is configured.");

        var errors = new List<Exception>();
        foreach (var translator in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await translator.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
                ProviderFailed?.Invoke(translator.Id, ex);
            }
        }

        throw new TranslationException(
            "All translation providers failed: " + string.Join(" | ", errors.Select(e => e.Message)),
            new AggregateException(errors));
    }
}
