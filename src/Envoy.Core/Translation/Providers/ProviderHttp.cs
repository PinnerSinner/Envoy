using System.Text.Json;

namespace Envoy.Core.Translation.Providers;

/// <summary>
/// The one place providers turn HTTP outcomes into classified <see cref="TranslationException"/>s (REQ-TRR-1, 2). No
/// message ever contains a response body, a URL or raw exception text.
/// </summary>
internal static class ProviderHttp
{
    /// <summary>Longest <c>Retry-After</c> Envoy honours (REQ-TRR-2).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Sends <paramref name="request"/>. A network failure becomes <see cref="TranslationErrorKind.Transient"/> (the
    /// <see cref="HttpRequestException"/> is kept as the inner exception so a pre-send failure can be refunded) and a
    /// cancellation the caller did not ask for (<c>HttpClient.Timeout</c>) becomes <see cref="TranslationErrorKind.Timeout"/>.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string providerId, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Wrap(ex, providerId, ct) is { } wrapped)
        {
            throw wrapped;
        }
    }

    /// <summary>
    /// Throws a classified <see cref="TranslationException"/> for a non-2xx response (<paramref name="overrideKind"/>
    /// may reclassify a status, returning null to keep <see cref="Classify"/>), else returns the body.
    /// </summary>
    public static async Task<string> ReadBodyAsync(HttpResponseMessage response, string providerId, TimeProvider clock,
                                                   Func<int, TranslationErrorKind?>? overrideKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            var kind = overrideKind?.Invoke(status) ?? Classify(status);
            throw TranslationErrorText.Error(kind, providerId, statusCode: status,
                retryAfter: ParseRetryAfter(response, (clock ?? TimeProvider.System).GetUtcNow()));
        }
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Wrap(ex, providerId, ct) is { } wrapped)
        {
            throw wrapped;
        }
    }

    /// <summary>The REQ-TRR-2 table. Providers override 401/403 where the endpoint is keyless.</summary>
    public static TranslationErrorKind Classify(int status) => status switch
    {
        >= 200 and < 300 => TranslationErrorKind.Unknown,
        408 or 500 or 502 or 503 or 504 or 529 => TranslationErrorKind.Transient,
        429 => TranslationErrorKind.RateLimited,
        401 or 403 => TranslationErrorKind.Auth,
        456 => TranslationErrorKind.QuotaExceeded,
        400 or 404 or 413 or 414 or 422 => TranslationErrorKind.Unsupported,
        >= 500 => TranslationErrorKind.Transient,
        _ => TranslationErrorKind.Unknown,
    };

    /// <summary><c>Retry-After</c> as delta-seconds or an HTTP-date, clamped to 0–3600 s; null when absent.</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        var header = response.Headers.RetryAfter;
        TimeSpan? value = header?.Delta ?? (header?.Date is { } date ? date - now : null);
        if (value is not { } v) return null;
        if (v < TimeSpan.Zero) return TimeSpan.Zero;
        return v > MaxRetryAfter ? MaxRetryAfter : v;
    }

    /// <summary>Runs <paramref name="parse"/>; any parsing failure becomes <see cref="TranslationErrorKind.BadResponse"/>.</summary>
    public static TranslationResult Parse(string providerId, string body, Func<string, TranslationResult> parse)
    {
        ArgumentNullException.ThrowIfNull(parse);
        try
        {
            return parse(body);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException
                                       or InvalidOperationException or FormatException or ArgumentException
                                       or NullReferenceException)
        {
            throw TranslationErrorText.Error(TranslationErrorKind.BadResponse, providerId, ex);
        }
    }

    /// <summary>
    /// The classified form of a transport exception, or null to let it propagate (a cancellation the caller asked
    /// for, or an exception that is already a <see cref="TranslationException"/>).
    /// </summary>
    public static TranslationException? Wrap(Exception ex, string providerId, CancellationToken ct) => ex switch
    {
        TranslationException => null,
        OperationCanceledException when ct.IsCancellationRequested => null,
        OperationCanceledException => TranslationErrorText.Error(TranslationErrorKind.Timeout, providerId, ex),
        HttpRequestException hre => new TranslationException(
            TranslationErrorText.Format(TranslationErrorText.NetworkFailed, ("provider", ProviderCatalog.NameOf(providerId))),
            hre, TranslationErrorKind.Transient, hre.StatusCode is { } s ? (int)s : null),
        IOException => TranslationErrorText.Error(TranslationErrorKind.Transient, providerId, ex),
        _ => null,
    };
}
