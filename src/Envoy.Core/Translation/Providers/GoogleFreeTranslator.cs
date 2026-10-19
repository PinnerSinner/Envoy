using System.Text.Json;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

/// <summary>
/// Google's keyless "gtx" endpoint (the one browser extensions and the website widget use).
/// Free and no account needed, but undocumented, so it may rate-limit or change without notice.
/// </summary>
public sealed class GoogleFreeTranslator(HttpClient http, TimeProvider? clock = null) : ITranslator
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    public string Id => ProviderCatalog.GoogleFree;
    public bool IsMetered => false;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sl = request.SourceLanguage is null ? "auto" : ToGoogle(request.SourceLanguage);
        var url = $"{Endpoint}?client=gtx&sl={sl}&tl={ToGoogle(request.TargetLanguage)}&dt=t&q={Uri.EscapeDataString(request.Text)}";

        using var message = new HttpRequestMessage(HttpMethod.Get, url).AsBrowser();
        using var response = await ProviderHttp.SendAsync(http, message, Id, cancellationToken).ConfigureAwait(false);
        // A 403 from the keyless endpoint means "too many requests from this IP", not a bad key.
        var json = await ProviderHttp.ReadBodyAsync(response, Id, clock ?? TimeProvider.System,
            status => status == 403 ? TranslationErrorKind.RateLimited : null, cancellationToken).ConfigureAwait(false);
        return ProviderHttp.Parse(Id, json, body => Parse(body, Id));
    }

    /// <summary>Response shape: [[["translated","original",...],...], null, "detected-lang", ...]</summary>
    internal static TranslationResult Parse(string json, string providerId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
            throw new FormatException("Not a gtx response.");
        var text = string.Concat(
            root[0].EnumerateArray()
                .Where(seg => seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
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
