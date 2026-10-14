using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

/// <summary>
/// Microsoft Translator via the short-lived token the Edge browser fetches for its built-in page translation.
/// Free and keyless; unofficial like the Google endpoint, which makes it a good independent fallback.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Translators live as long as the translator stack; 03-translation-resilience owns disposal.")]
public sealed class EdgeFreeTranslator(HttpClient http, TimeProvider? clock = null) : ITranslator
{
    private const string AuthUrl = "https://edge.microsoft.com/translate/auth";
    private const string TranslateUrl = "https://api-edge.cognitive.microsofttranslator.com/translate";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public string Id => ProviderCatalog.EdgeFree;
    public bool IsMetered => false;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var url = $"{TranslateUrl}?api-version=3.0&to={ToMicrosoft(request.TargetLanguage)}";
        if (request.SourceLanguage is not null) url += $"&from={ToMicrosoft(request.SourceLanguage)}";

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new[] { new { Text = request.Text } }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) _token = null;
        if (!response.IsSuccessStatusCode)
            throw new TranslationException($"Microsoft Translator returned {(int)response.StatusCode}.");

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json, Id);
    }

    /// <summary>Response shape: [{"detectedLanguage":{"language":"zh-Hans"},"translations":[{"text":"..."}]}]</summary>
    internal static TranslationResult Parse(string json, string providerId)
    {
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement[0];
        var text = item.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        string? detected = item.TryGetProperty("detectedLanguage", out var d)
            ? LanguageCatalog.Normalize(d.GetProperty("language").GetString() ?? "")
            : null;
        return new TranslationResult(text, detected, providerId);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && _clock.GetUtcNow() < _tokenExpires) return _token;
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _clock.GetUtcNow() < _tokenExpires) return _token;
            var token = (await http.GetStringAsync(AuthUrl, ct).ConfigureAwait(false)).Trim();
            if (token.Length == 0) throw new TranslationException("Microsoft Translator did not issue a token.");
            _token = token;
            _tokenExpires = _clock.GetUtcNow().AddMinutes(8); // tokens last 10 minutes
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string ToMicrosoft(string code) => code switch
    {
        "zh-CN" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        _ => code.Split('-')[0],
    };
}
