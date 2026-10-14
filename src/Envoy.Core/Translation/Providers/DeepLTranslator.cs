using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

public sealed class DeepLTranslator(HttpClient http, string apiKey, TimeProvider? clock = null) : ITranslator
{
    public string Id => ProviderCatalog.DeepL;
    public bool IsMetered => true;

    // Free-plan keys end in ":fx" and must use the api-free host.
    private string Endpoint => apiKey.EndsWith(":fx", StringComparison.Ordinal)
        ? "https://api-free.deepl.com/v2/translate"
        : "https://api.deepl.com/v2/translate";

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(apiKey)) throw TranslationErrorText.NotConfigured(TranslationErrorText.MissingKey, Id);

        var body = new Dictionary<string, object> { ["text"] = new[] { request.Text }, ["target_lang"] = ToDeepLTarget(request.TargetLanguage) };
        if (request.SourceLanguage is not null) body["source_lang"] = ToDeepLSource(request.SourceLanguage);

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);

        using var response = await ProviderHttp.SendAsync(http, message, Id, cancellationToken).ConfigureAwait(false);
        // 403 = key rejected, 456 = monthly quota used up (both in the REQ-TRR-2 table).
        var json = await ProviderHttp.ReadBodyAsync(response, Id, clock ?? TimeProvider.System, null, cancellationToken).ConfigureAwait(false);
        return ProviderHttp.Parse(Id, json, b => Parse(b, Id));
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
