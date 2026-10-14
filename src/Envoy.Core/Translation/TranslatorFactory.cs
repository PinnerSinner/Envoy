using Envoy.Core.Settings;
using Envoy.Core.Translation.Providers;

namespace Envoy.Core.Translation;

/// <summary>Builds the translator stack from settings: budget cap → provider, chained for fallback, then cached.</summary>
public sealed class TranslatorFactory(HttpClient http, UsageTracker usage)
{
    public ITranslator Build(TranslationSettings settings, Action<string, Exception>? onProviderFailed = null)
    {
        var ids = new List<string> { settings.Primary };
        ids.AddRange(settings.Fallbacks.Where(f => f != settings.Primary));

        var chain = new List<ITranslator>();
        foreach (var id in ids.Distinct())
        {
            try
            {
                if (Create(id, settings) is { } t)
                    chain.Add(new BudgetedTranslator(t, usage, () => settings.DailyCharacterLimit));
            }
            catch (TranslationException ex)
            {
                // Missing credentials for a fallback shouldn't break the providers that do work.
                onProviderFailed?.Invoke(id, ex);
            }
        }

        // Always keep a free provider at the end so a missing key or spent budget doesn't stop translation.
        if (!chain.Any(t => t.Id == ProviderCatalog.GoogleFree)) chain.Add(new GoogleFreeTranslator(http));

        var fallback = new FallbackTranslator(chain);
        if (onProviderFailed is not null) fallback.ProviderFailed += onProviderFailed;
        return new CachingTranslator(fallback);
    }

    public ITranslator? Create(string id, TranslationSettings s) => id switch
    {
        ProviderCatalog.GoogleFree => new GoogleFreeTranslator(http),
        ProviderCatalog.EdgeFree => new EdgeFreeTranslator(http),
        ProviderCatalog.DeepL => new DeepLTranslator(http, s.DeepLKey),
        ProviderCatalog.Claude => new ClaudeTranslator(s.ClaudeKey, s.ClaudeModel),
        ProviderCatalog.LibreTranslate => new LibreTranslateTranslator(http, s.LibreTranslateUrl, s.LibreTranslateKey),
        ProviderCatalog.Aws => new AwsTranslator(s.AwsAccessKeyId, s.AwsSecretAccessKey, s.AwsRegion),
        _ => null,
    };
}
