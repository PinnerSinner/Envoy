using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

public sealed class DeepLTranslator(HttpClient http, string apiKey) : ITranslator
{
    public string Id => ProviderCatalog.DeepL;
    public bool IsMetered => true;

    // Free-plan keys end in ":fx" and must use the api-free host.
    private string Endpoint => apiKey.EndsWith(":fx", StringComparison.Ordinal)
        ? "https://api-free.deepl.com/v2/translate"
        : "https://api.deepl.com/v2/translate";

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new TranslationException("DeepL needs an API key (Settings → Translation).");

        var body = new Dictionary<string, object> { ["text"] = new[] { request.Text }, ["target_lang"] = ToDeepLTarget(request.TargetLanguage) };
        if (request.SourceLanguage is not null) body["source_lang"] = ToDeepLSource(request.SourceLanguage);

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new TranslationException((int)response.StatusCode switch
            {
                403 => "DeepL rejected the API key.",
                456 => "DeepL monthly quota used up.",
                var code => $"DeepL returned {code}.",
            });
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json, Id);
    }

    internal static TranslationResult Parse(string json, string providerId)
    {
        using var doc = JsonDocument.Parse(json);
        var t = doc.RootElement.GetProperty("translations")[0];
        var detected = t.TryGetProperty("detected_source_language", out var d) ? LanguageCatalog.Normalize(d.GetString() ?? "") : null;
        return new TranslationResult(t.GetProperty("text").GetString() ?? "", detected, providerId);
    }

    private static string ToDeepLTarget(string code) => code switch
    {
        "en" => "EN-US",
        "pt" => "PT-BR",
        "zh-CN" => "ZH-HANS",
        "zh-TW" => "ZH-HANT",
        _ => code.Split('-')[0].ToUpperInvariant(),
    };

    private static string ToDeepLSource(string code) => code.Split('-')[0].ToUpperInvariant();
}
