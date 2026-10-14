using System.Text.Json;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

/// <summary>
/// Google's keyless "gtx" endpoint (the one browser extensions and the website widget use).
/// Free and no account needed, but undocumented, so it may rate-limit or change without notice.
/// </summary>
public sealed class GoogleFreeTranslator(HttpClient http) : ITranslator
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    public string Id => ProviderCatalog.GoogleFree;
    public bool IsMetered => false;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var sl = request.SourceLanguage is null ? "auto" : ToGoogle(request.SourceLanguage);
        var url = $"{Endpoint}?client=gtx&sl={sl}&tl={ToGoogle(request.TargetLanguage)}&dt=t&q={Uri.EscapeDataString(request.Text)}";

        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TranslationException($"Google Translate returned {(int)response.StatusCode}. It may be rate-limiting; Envoy will use your fallback provider.");

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json, Id);
    }

    /// <summary>Response shape: [[["translated","original",...],...], null, "detected-lang", ...]</summary>
    internal static TranslationResult Parse(string json, string providerId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var text = string.Concat(
            root[0].EnumerateArray()
                .Where(seg => seg.ValueKind == JsonValueKind.Array && seg[0].ValueKind == JsonValueKind.String)
                .Select(seg => seg[0].GetString()));
        string? detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
            ? LanguageCatalog.Normalize(root[2].GetString()!)
            : null;
        return new TranslationResult(text, detected, providerId);
    }

    private static string ToGoogle(string code) => code switch
    {
        "zh-CN" or "zh-TW" => code,
        _ => code.Split('-')[0],
    };
}
