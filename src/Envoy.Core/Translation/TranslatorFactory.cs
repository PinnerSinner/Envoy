using System.Security.Cryptography;
using System.Text;
using Envoy.Core.Settings;
using Envoy.Core.Translation.Providers;
using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Translation;

/// <summary>
/// Builds the translator stack from settings (spec §7):
/// <c>Caching(runtime cache, primary) → Coalescing → Fallback(deadline)[ Budgeted(Resilient(provider)) …, Budgeted(Resilient(GoogleFree)) ]</c>.
/// Runs on the UI thread inside <c>ApplySettings</c>; providers capture their settings at construction. Breakers,
/// throttles, health and the cache live in the long-lived <see cref="TranslationRuntime"/> and survive rebuilds.
/// </summary>
public sealed class TranslatorFactory
{
    private readonly HttpClient _http;
    private readonly UsageTracker _usage;

    /// <param name="runtime">The host's long-lived runtime; null creates a private one (tests and back-compat).</param>
    public TranslatorFactory(HttpClient http, UsageTracker usage, TranslationRuntime? runtime = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        Runtime = runtime ?? new TranslationRuntime();
    }

    public TranslationRuntime Runtime { get; }

    /// <summary>
    /// The provider ids of the chain <see cref="Build"/> makes from <paramref name="settings"/>, in order: the primary,
    /// the fallbacks, and Google (free) as the last resort. Providers that cannot be built are included (shown as "Not set up").
    /// </summary>
    public static IReadOnlyList<string> ChainIds(TranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ids = new List<string> { settings.Primary };
        ids.AddRange(settings.Fallbacks.Where(f => f != settings.Primary));
        ids = [.. ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)];
        if (!ids.Contains(ProviderCatalog.GoogleFree)) ids.Add(ProviderCatalog.GoogleFree);
        return ids;
    }

    /// <summary>The cache namespace: the primary provider id, plus the model when Claude is primary (REQ-TRR-19).</summary>
    public static string CacheNamespace(TranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return CacheNamespace(settings.Primary, settings);
    }

    /// <summary>The cache namespace of <paramref name="providerId"/>: its id, plus the model for Claude.</summary>
    public static string CacheNamespace(string providerId, TranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(settings);
        return providerId == ProviderCatalog.Claude
            ? providerId + "/" + (string.IsNullOrWhiteSpace(settings.ClaudeModel) ? ClaudeTranslator.DefaultModel : settings.ClaudeModel)
            : providerId;
    }

    /// <summary>
    /// SHA-256 of the provider's credential and endpoint fields (key, URL, model, region). Computed in memory, never
    /// stored or logged; a change resets the provider's breaker and health (REQ-TRR-11).
    /// </summary>
    public static string Fingerprint(string providerId, TranslationSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string[] fields = providerId switch
        {
            ProviderCatalog.DeepL => [s.DeepLKey],
            ProviderCatalog.Claude => [s.ClaudeKey, s.ClaudeModel],
            ProviderCatalog.LibreTranslate => [s.LibreTranslateUrl, s.LibreTranslateKey],
            ProviderCatalog.Aws => [s.AwsAccessKeyId, s.AwsSecretAccessKey, s.AwsRegion],
            _ => [],
        };
        var joined = providerId + "\u001f" + string.Join("\u001f", fields.Select(f => f ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    /// <summary>
    /// Builds one lane's stack. Both lanes share the runtime's breakers, health and memory cache; only the chat lane's
    /// results can be saved to disk (the reply box's entries are memory-only). The cache namespace is the first provider
    /// that could be built, and only its results are cached (REQ-TRR-19).
    /// </summary>
    public ITranslator Build(TranslationSettings settings, Action<string, Exception>? onProviderFailed = null,
                             TranslationLane lane = TranslationLane.Chat)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var limit = settings.DailyCharacterLimit; // captured on the UI thread (contract §7)
        var clock = Runtime.Clock;

        var chain = new List<ITranslator>();
        foreach (var id in ChainIds(settings))
        {
            ITranslator? provider;
            try
            {
                provider = Runtime.SharedProvider(id, Fingerprint(id, settings), () => Create(id, settings));
            }
            catch (TranslationException ex)
            {
                // Missing credentials for a fallback shouldn't break the providers that do work.
                Runtime.RecordBuildFailure(id, ex);
                onProviderFailed?.Invoke(id, ex);
                continue;
            }
            if (provider is null) continue;
            var guard = Runtime.Guard(id, Fingerprint(id, settings));
            var resilient = new ResilientTranslator(provider, guard, lane, clock);
            chain.Add(new BudgetedTranslator(resilient, _usage, () => limit,
                (ex, until) => guard.Health.RecordBudgetExceeded(ex, until), clock));
        }

        var runtime = Runtime;
        var fallback = new FallbackTranslator(chain, () => runtime.Options.RequestTimeout, clock, runtime.Health.Redactor);
        if (onProviderFailed is not null) fallback.ProviderFailed += onProviderFailed;
        var coalescing = new CoalescingTranslator(fallback, r => TranslationCache.Key("", r));
        // A primary that is "Not set up" is not in the chain: the first provider that is gets the namespace.
        var primary = chain.Count > 0 ? chain[0].Id : settings.Primary;
        return new CachingTranslator(coalescing, Runtime.Cache, CacheNamespace(primary, settings), primary,
            persist: lane == TranslationLane.Chat);
    }

    /// <summary>
    /// A new bare provider for <paramref name="id"/> (no resilience), or null for an unknown id. The caller owns it
    /// (an <see cref="IDisposable"/> one must be disposed); <see cref="Build"/> shares them through the runtime instead.
    /// </summary>
    /// <exception cref="TranslationException">Kind <see cref="TranslationErrorKind.Configuration"/>: a key is missing.</exception>
    public ITranslator? Create(string id, TranslationSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var clock = Runtime.Clock;
        var timeout = ResilienceOptions.For(id).AttemptTimeout;
        // A provider without its key or URL is "Not set up": it is left out of the chain instead of failing every line.
        switch (id)
        {
            case ProviderCatalog.DeepL when string.IsNullOrWhiteSpace(s.DeepLKey):
            case ProviderCatalog.Claude when string.IsNullOrWhiteSpace(s.ClaudeKey):
                throw TranslationErrorText.NotConfigured(TranslationErrorText.MissingKey, id);
            case ProviderCatalog.LibreTranslate when string.IsNullOrWhiteSpace(s.LibreTranslateUrl):
                throw TranslationErrorText.NotConfigured(TranslationErrorText.MissingUrl, id);
        }
        return id switch
        {
            ProviderCatalog.GoogleFree => new GoogleFreeTranslator(_http, clock),
            ProviderCatalog.EdgeFree => new EdgeFreeTranslator(_http, clock),
            ProviderCatalog.DeepL => new DeepLTranslator(_http, s.DeepLKey, clock),
            ProviderCatalog.Claude => new ClaudeTranslator(s.ClaudeKey, s.ClaudeModel, _http, timeout),
            ProviderCatalog.LibreTranslate => new LibreTranslateTranslator(_http, s.LibreTranslateUrl, s.LibreTranslateKey, clock),
            ProviderCatalog.Aws => new AwsTranslator(s.AwsAccessKeyId, s.AwsSecretAccessKey, s.AwsRegion, timeout),
            _ => null,
        };
    }
}
