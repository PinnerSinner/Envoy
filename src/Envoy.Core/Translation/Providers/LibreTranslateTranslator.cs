using System.Net.Http.Json;
using System.Text.Json;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

public sealed class LibreTranslateTranslator(HttpClient http, string baseUrl, string? apiKey, TimeProvider? clock = null) : ITranslator
{
    public string Id => ProviderCatalog.LibreTranslate;
    public bool IsMetered => false;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(baseUrl)) throw TranslationErrorText.NotConfigured(TranslationErrorText.MissingUrl, Id);

        var body = new Dictionary<string, string>
        {
            ["q"] = request.Text,
            ["source"] = request.SourceLanguage is null ? "auto" : ToLibre(request.SourceLanguage),
            ["target"] = ToLibre(request.TargetLanguage),
            ["format"] = "text",
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) body["api_key"] = apiKey;

        Uri url;
        try
        {
            url = new Uri($"{baseUrl.TrimEnd('/')}/translate");
        }
        catch (UriFormatException)
        {
            throw TranslationErrorText.NotConfigured(TranslationErrorText.MissingUrl, Id);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        using var response = await ProviderHttp.SendAsync(http, message, Id, cancellationToken).ConfigureAwait(false);
        // 403 = key rejected, 400 = language pair or input not supported (both in the REQ-TRR-2 table).
        var json = await ProviderHttp.ReadBodyAsync(response, Id, clock ?? TimeProvider.System, null, cancellationToken).ConfigureAwait(false);
        return ProviderHttp.Parse(Id, json, b => Parse(b, Id));
    }

    /// <summary>Response shape: {"translatedText":"...","detectedLanguage":{"confidence":90,"language":"zh"}}</summary>
    internal static TranslationResult Parse(string json, string providerId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? detected = root.TryGetProperty("detectedLanguage", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("language", out var l)
            ? LanguageCatalog.Normalize(l.GetString() ?? "")
            : null;
        return new TranslationResult(root.GetProperty("translatedText").GetString() ?? "", detected, providerId);
    }

    private static string ToLibre(string code) => code switch
    {
        "zh-CN" => "zh",
        "zh-TW" => "zt",
        _ => code.Split('-')[0],
    };
}
