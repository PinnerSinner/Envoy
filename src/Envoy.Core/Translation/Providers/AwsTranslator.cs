using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.Translate;
using Amazon.Translate.Model;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

public sealed class AwsTranslator : ITranslator, IDisposable
{
    /// <summary>Per-attempt timeout when none is given (REQ-TRR-23).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    private static readonly HashSet<string> ThrottlingCodes = new(StringComparer.Ordinal)
    {
        "ThrottlingException", "TooManyRequestsException", "Throttling", "ThrottledException", "RequestLimitExceeded",
        "ProvisionedThroughputExceededException", "SlowDown",
    };

    private static readonly HashSet<string> AuthCodes = new(StringComparer.Ordinal)
    {
        "UnrecognizedClientException", "InvalidSignatureException", "AccessDeniedException", "ExpiredTokenException",
        "InvalidClientTokenId", "SignatureDoesNotMatch", "IncompleteSignature", "MissingAuthenticationToken",
    };

    private static readonly HashSet<string> UnsupportedCodes = new(StringComparer.Ordinal)
    {
        "UnsupportedLanguagePairException", "TextSizeLimitExceededException", "DetectedLanguageLowConfidenceException",
        "InvalidRequestException", "InvalidParameterValueException", "ValidationException",
    };

    private readonly AmazonTranslateClient _client;

    /// <param name="timeout">The per-attempt timeout; the SDK never retries on its own (REQ-TRR-23).</param>
    /// <param name="httpClientFactory">Tests only: a factory that returns a client over a fake handler.</param>
    public AwsTranslator(string accessKeyId, string secretAccessKey, string region, TimeSpan? timeout = null,
                         HttpClientFactory? httpClientFactory = null)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
            throw TranslationErrorText.NotConfigured(TranslationErrorText.MissingAwsKeys, ProviderCatalog.Aws);
        Config = CreateConfig(region, timeout, httpClientFactory);
        _client = new AmazonTranslateClient(new BasicAWSCredentials(accessKeyId, secretAccessKey), Config);
    }

    public string Id => ProviderCatalog.Aws;
    public bool IsMetered => true;

    /// <summary>The SDK configuration: no internal retries, our timeout.</summary>
    internal AmazonTranslateConfig Config { get; }

    internal static AmazonTranslateConfig CreateConfig(string region, TimeSpan? timeout, HttpClientFactory? httpClientFactory)
    {
        var config = new AmazonTranslateConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(region) ? "us-east-1" : region),
            MaxErrorRetry = 0,
            Timeout = timeout ?? DefaultTimeout,
        };
        if (httpClientFactory is not null) config.HttpClientFactory = httpClientFactory;
        return config;
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var response = await _client.TranslateTextAsync(new TranslateTextRequest
            {
                Text = request.Text,
                SourceLanguageCode = request.SourceLanguage is null ? "auto" : ToAws(request.SourceLanguage),
                TargetLanguageCode = ToAws(request.TargetLanguage),
            }, cancellationToken).ConfigureAwait(false);
            if (response.TranslatedText is null) throw TranslationErrorText.Error(TranslationErrorKind.BadResponse, Id);
            return new TranslationResult(response.TranslatedText, LanguageCatalog.Normalize(response.SourceLanguageCode ?? ""), Id);
        }
        catch (Exception ex) when (Map(ex, Id, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>The SDK exception mapped to a kind by its error code (REQ-TRR-2), or null for a caller cancellation.</summary>
    internal static TranslationException? Map(Exception ex, string providerId, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested) return null;
        if (ex is TranslationException te) return te;
        if (ex is AmazonUnmarshallingException { StatusCode: >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous } unreadable)
            return TranslationErrorText.Error(TranslationErrorKind.BadResponse, providerId, unreadable, (int)unreadable.StatusCode);
        if (ex is AmazonServiceException service)
        {
            var status = (int)service.StatusCode;
            var code = service.ErrorCode ?? "";
            var kind = code switch
            {
                _ when ThrottlingCodes.Contains(code) => TranslationErrorKind.RateLimited,
                _ when AuthCodes.Contains(code) => TranslationErrorKind.Auth,
                "LimitExceededException" => TranslationErrorKind.QuotaExceeded,
                _ when UnsupportedCodes.Contains(code) => TranslationErrorKind.Unsupported,
                "ServiceUnavailableException" or "InternalServerException" => TranslationErrorKind.Transient,
                _ when status >= 500 => TranslationErrorKind.Transient,
                _ when status == 0 => TranslationErrorKind.Transient,
                _ => ProviderHttp.Classify(status),
            };
            return TranslationErrorText.Error(kind, providerId, ex, status == 0 ? null : status);
        }
        if (ex is HttpRequestException or IOException or OperationCanceledException) return ProviderHttp.Wrap(ex, providerId, ct);
        if (ex is AmazonClientException client)
        {
            return client.InnerException is HttpRequestException hre
                ? ProviderHttp.Wrap(hre, providerId, ct)
                : TranslationErrorText.Error(TranslationErrorKind.Transient, providerId, ex);
        }
        return TranslationErrorText.Error(TranslationErrorKind.Unknown, providerId, ex);
    }

    private static string ToAws(string code) => code switch
    {
        "zh-CN" => "zh",
        "zh-TW" => "zh-TW",
        _ => code.Split('-')[0],
    };

    public void Dispose() => _client.Dispose();
}
