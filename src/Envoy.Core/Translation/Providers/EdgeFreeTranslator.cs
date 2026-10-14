using System.Diagnostics.CodeAnalysis;
using System.Net;
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
    Justification = "Translators are rebuilt on settings edits and own no handle that needs disposal: the HttpClient is AppHost's, and SemaphoreSlim allocates no wait handle unless AvailableWaitHandle is read.")]
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
        ArgumentNullException.ThrowIfNull(request);
        var url = $"{TranslateUrl}?api-version=3.0&to={ToMicrosoft(request.TargetLanguage)}";
        if (request.SourceLanguage is not null) url += $"&from={ToMicrosoft(request.SourceLanguage)}";

        // REQ-TRR-25: a 401 means the token went stale; fetch a fresh one and resend once, inside this attempt.
        for (var pass = 1; ; pass++)
        {
            var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new[] { new { Text = request.Text } }),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await ProviderHttp.SendAsync(http, message, Id, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ForgetToken(token);
                if (pass == 1) continue;
            }
            // A second 401 is the keyless endpoint refusing us, not a bad key.
            var json = await ProviderHttp.ReadBodyAsync(response, Id, _clock,
                status => status is 401 or 403 ? TranslationErrorKind.RateLimited : null, cancellationToken).ConfigureAwait(false);
            return ProviderHttp.Parse(Id, json, body => Parse(body, Id));
        }
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

    private void ForgetToken(string token)
    {
        if (ReferenceEquals(Volatile.Read(ref _token), token)) Volatile.Write(ref _token, null);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _token) is { } cached && _clock.GetUtcNow() < _tokenExpires) return cached;
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } again && _clock.GetUtcNow() < _tokenExpires) return again;
            using var message = new HttpRequestMessage(HttpMethod.Get, AuthUrl);
            using var response = await ProviderHttp.SendAsync(http, message, Id, ct).ConfigureAwait(false);
            // Whatever the auth endpoint says, it is a hiccup on Microsoft's side, not the user's fault.
            var body = await ProviderHttp.ReadBodyAsync(response, Id, _clock,
                status => status == 429 ? TranslationErrorKind.RateLimited : TranslationErrorKind.Transient, ct).ConfigureAwait(false);
            var token = body.Trim();
            if (token.Length == 0)
            {
                throw new TranslationException(
                    TranslationErrorText.Format(TranslationErrorText.NoToken, ("provider", ProviderCatalog.NameOf(Id))),
                    kind: TranslationErrorKind.Transient);
            }
            _tokenExpires = _clock.GetUtcNow().AddMinutes(8); // tokens last 10 minutes
            Volatile.Write(ref _token, token);
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
