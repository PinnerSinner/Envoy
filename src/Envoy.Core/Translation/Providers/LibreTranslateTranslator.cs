using System.Net.Http.Json;
using System.Text.Json;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

public sealed class LibreTranslateTranslator(HttpClient http, string baseUrl, string? apiKey) : ITranslator
{
    public string Id => ProviderCatalog.LibreTranslate;
    public bool IsMetered => false;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new TranslationException("LibreTranslate needs a server URL (Settings → Translation).");

        var body = new Dictionary<string, string>
        {
            ["q"] = request.Text,
            ["source"] = request.SourceLanguage is null ? "auto" : ToLibre(request.SourceLanguage),
            ["target"] = ToLibre(request.TargetLanguage),
            ["format"] = "text",
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) body["api_key"] = apiKey;

        using var response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/translate", JsonContent.Create(body), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new TranslationException($"LibreTranslate returned {(int)response.StatusCode}.");

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? detected = root.TryGetProperty("detectedLanguage", out var d) && d.TryGetProperty("language", out var l)
            ? LanguageCatalog.Normalize(l.GetString() ?? "")
            : null;
        return new TranslationResult(root.GetProperty("translatedText").GetString() ?? "", detected, Id);
    }

    private static string ToLibre(string code) => code switch
    {
        "zh-CN" => "zh",
        "zh-TW" => "zt",
        _ => code.Split('-')[0],
    };
}
