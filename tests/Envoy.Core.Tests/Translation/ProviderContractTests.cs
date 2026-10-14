using System.Net;
using Amazon.Runtime;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Providers;
using Xunit;

namespace Envoy.Core.Tests.Translation;

/// <summary>
/// The HTTP contract of every provider against recorded fixtures (REQ-TRR-24): request shape (method, host/path, auth
/// header, key fields) and the mapped result or <see cref="TranslationErrorKind"/>.
/// </summary>
public class ProviderContractTests
{
    private const string Secret = "sk-ant-api03-SECRETKEY0123456789";
    private static readonly TranslationRequest Request = new("你好，再来一局？", "en");

    private static async Task<(TranslationResult? Result, TranslationException? Error)> Run(ITranslator t)
    {
        try
        {
            return (await t.TranslateAsync(Request), null);
        }
        catch (TranslationException ex)
        {
            return (null, ex);
        }
    }

    private static void AssertOutcome(string scenario, TranslationErrorKind? expected, TranslationResult? result, TranslationException? error)
    {
        if (expected is null)
        {
            Assert.Null(error);
            Assert.NotNull(result);
            Assert.Equal("Hello, rematch?", result!.Text);
            return;
        }
        Assert.NotNull(error);
        Assert.Equal(expected, error!.Kind);
        // No body, URL or exception text in the message.
        Assert.DoesNotContain("<", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("http", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Request.Text, error.Message, StringComparison.Ordinal);
        if (scenario == "429" && expected == TranslationErrorKind.RateLimited && error.RetryAfter is { } ra)
            Assert.Equal(TimeSpan.FromSeconds(2), ra);
    }

    [Theory]
    [InlineData("success", null)]
    [InlineData("429", TranslationErrorKind.RateLimited)]
    [InlineData("500", TranslationErrorKind.Transient)]
    [InlineData("malformed", TranslationErrorKind.BadResponse)]
    [InlineData("403", TranslationErrorKind.RateLimited)]
    public async Task Google_contract(string scenario, TranslationErrorKind? expected)
    {
        var handler = new FakeHttpHandler().Respond(ProviderFixture.Load("google-free", scenario));
        var (result, error) = await Run(new GoogleFreeTranslator(handler.Client(), TestClock.Create()));
        AssertOutcome(scenario, expected, result, error);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("translate.googleapis.com", req.Uri.Host);
        Assert.Equal("/translate_a/single", req.Uri.AbsolutePath);
        Assert.Contains("client=gtx", req.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("tl=en", req.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("q=" + Uri.EscapeDataString(Request.Text), req.Uri.OriginalString, StringComparison.Ordinal);
        if (scenario == "success") Assert.Equal("zh-CN", result!.DetectedSourceLanguage);
    }

    [Theory]
    [InlineData("success", null)]
    [InlineData("429", TranslationErrorKind.RateLimited)]
    [InlineData("500", TranslationErrorKind.Transient)]
    [InlineData("malformed", TranslationErrorKind.BadResponse)]
    [InlineData("401", TranslationErrorKind.RateLimited)]
    public async Task Edge_contract(string scenario, TranslationErrorKind? expected)
    {
        var token = ProviderFixture.Text("edge-free", "auth-token.txt");
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, token, "text/plain")
            .Respond(ProviderFixture.Load("edge-free", scenario));
        if (scenario == "401") handler.Respond(HttpStatusCode.OK, token, "text/plain").Respond(ProviderFixture.Load("edge-free", "401"));
        var (result, error) = await Run(new EdgeFreeTranslator(handler.Client(), TestClock.Create()));
        AssertOutcome(scenario, expected, result, error);

        var requests = handler.Requests;
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("edge.microsoft.com", requests[0].Uri.Host);
        Assert.Equal("/translate/auth", requests[0].Uri.AbsolutePath);
        var translate = requests[1];
        Assert.Equal(HttpMethod.Post, translate.Method);
        Assert.Equal("api-edge.cognitive.microsofttranslator.com", translate.Uri.Host);
        Assert.Equal("/translate", translate.Uri.AbsolutePath);
        Assert.Contains("api-version=3.0", translate.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("to=en", translate.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer " + token, translate.Headers["Authorization"]);
        Assert.Contains("\"text\":", translate.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edge_refreshes_token_once_on_401()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, "stale-token", "text/plain")
            .Respond(ProviderFixture.Load("edge-free", "401"))
            .Respond(HttpStatusCode.OK, "fresh-token", "text/plain")
            .Respond(ProviderFixture.Load("edge-free", "success"));
        var result = await new EdgeFreeTranslator(handler.Client(), TestClock.Create()).TranslateAsync(Request);

        Assert.Equal("Hello, rematch?", result.Text);
        var requests = handler.Requests;
        Assert.Equal(4, requests.Count);
        Assert.Equal("Bearer stale-token", requests[1].Headers["Authorization"]);
        Assert.Equal("Bearer fresh-token", requests[3].Headers["Authorization"]);
    }

    [Fact]
    public async Task Edge_second_401_is_RateLimited()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, "token-1", "text/plain")
            .Respond(ProviderFixture.Load("edge-free", "401"))
            .Respond(HttpStatusCode.OK, "token-2", "text/plain")
            .Respond(ProviderFixture.Load("edge-free", "401"));
        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            new EdgeFreeTranslator(handler.Client(), TestClock.Create()).TranslateAsync(Request));

        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(4, handler.Requests.Count); // exactly one refresh, no third try
    }

    [Fact]
    public async Task Edge_failing_auth_endpoint_is_Transient()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Forbidden, "nope", "text/plain");
        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            new EdgeFreeTranslator(handler.Client(), TestClock.Create()).TranslateAsync(Request));
        Assert.Equal(TranslationErrorKind.Transient, ex.Kind);
    }

    [Theory]
    [InlineData("success", null)]
    [InlineData("429", TranslationErrorKind.RateLimited)]
    [InlineData("500", TranslationErrorKind.Transient)]
    [InlineData("malformed", TranslationErrorKind.BadResponse)]
    [InlineData("auth", TranslationErrorKind.Auth)]
    [InlineData("456", TranslationErrorKind.QuotaExceeded)]
    public async Task DeepL_contract(string scenario, TranslationErrorKind? expected)
    {
        const string key = "0123abcd-0000-1111-2222-333344445555:fx";
        var handler = new FakeHttpHandler().Respond(ProviderFixture.Load("deepl", scenario));
        var (result, error) = await Run(new DeepLTranslator(handler.Client(), key, TestClock.Create()));
        AssertOutcome(scenario, expected, result, error);
        if (error is not null) Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("api-free.deepl.com", req.Uri.Host); // ":fx" keys use the free host
        Assert.Equal("/v2/translate", req.Uri.AbsolutePath);
        Assert.Equal("DeepL-Auth-Key " + key, req.Headers["Authorization"]);
        Assert.Contains("\"target_lang\":\"EN-US\"", req.Body, StringComparison.Ordinal);
        Assert.Contains("\"text\":[", req.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepL_paid_key_uses_the_paid_host()
    {
        var handler = new FakeHttpHandler().Respond(ProviderFixture.Load("deepl", "success"));
        await new DeepLTranslator(handler.Client(), "paid-key-123456").TranslateAsync(Request);
        Assert.Equal("api.deepl.com", Assert.Single(handler.Requests).Uri.Host);
    }

    [Theory]
    [InlineData("success", null)]
    [InlineData("429", TranslationErrorKind.RateLimited)]
    [InlineData("500", TranslationErrorKind.Transient)]
    [InlineData("malformed", TranslationErrorKind.BadResponse)]
    [InlineData("auth", TranslationErrorKind.Auth)]
    [InlineData("400", TranslationErrorKind.Unsupported)]
    public async Task Libre_contract(string scenario, TranslationErrorKind? expected)
    {
        var handler = new FakeHttpHandler().Respond(ProviderFixture.Load("libretranslate", scenario));
        var (result, error) = await Run(new LibreTranslateTranslator(handler.Client(), "http://localhost:5000/", "libre-key-123", TestClock.Create()));
        AssertOutcome(scenario, expected, result, error);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("localhost", req.Uri.Host);
        Assert.Equal("/translate", req.Uri.AbsolutePath);
        Assert.Contains("\"q\":", req.Body, StringComparison.Ordinal);
        Assert.Contains("\"target\":\"en\"", req.Body, StringComparison.Ordinal);
        Assert.Contains("\"api_key\":\"libre-key-123\"", req.Body, StringComparison.Ordinal);
        if (scenario == "success") Assert.Equal("zh-CN", result!.DetectedSourceLanguage);
    }

    [Fact]
    public void Libre_parse_reads_text_and_language()
    {
        var r = LibreTranslateTranslator.Parse("""{"detectedLanguage":{"confidence":90,"language":"ko"},"translatedText":"gg"}""", "libretranslate");
        Assert.Equal("gg", r.Text);
        Assert.Equal("ko", r.DetectedSourceLanguage);
    }

    [Theory]
    [InlineData("success", null)]
    [InlineData("429", TranslationErrorKind.RateLimited)]
    [InlineData("500", TranslationErrorKind.Transient)]
    [InlineData("malformed", TranslationErrorKind.BadResponse)]
    [InlineData("auth", TranslationErrorKind.Auth)]
    public async Task Claude_contract(string scenario, TranslationErrorKind? expected)
    {
        var handler = new FakeHttpHandler().Respond(ProviderFixture.Load("claude", scenario));
        var (result, error) = await Run(new ClaudeTranslator(Secret, ClaudeTranslator.DefaultModel, handler.Client(), TimeSpan.FromSeconds(5)));
        AssertOutcome(scenario, expected, result, error);
        if (error is not null) Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);

        // SDK retries are off: exactly one request per call, even on 429 and 500.
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("api.anthropic.com", req.Uri.Host);
        Assert.Equal("/v1/messages", req.Uri.AbsolutePath);
        Assert.Equal(Secret, req.Headers["x-api-key"]);
        Assert.Contains("\"model\":\"" + ClaudeTranslator.DefaultModel + "\"", req.Body, StringComparison.Ordinal);
        if (scenario == "success") Assert.Equal("zh-CN", result!.DetectedSourceLanguage);
    }

    [Fact]
    public void Claude_client_has_sdk_retries_off_and_our_timeout()
    {
        var claude = new ClaudeTranslator(Secret, ClaudeTranslator.DefaultModel, new HttpClient(), TimeSpan.FromSeconds(9));
        Assert.Equal(0, claude.ClientSettings.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(9), claude.ClientSettings.Timeout);
    }

    [Theory]
    [InlineData("success", null)]
    [InlineData("429", TranslationErrorKind.RateLimited)]
    [InlineData("500", TranslationErrorKind.Transient)]
    [InlineData("malformed", TranslationErrorKind.BadResponse)]
    [InlineData("auth", TranslationErrorKind.Auth)]
    [InlineData("quota", TranslationErrorKind.QuotaExceeded)]
    public async Task Aws_contract(string scenario, TranslationErrorKind? expected)
    {
        var handler = new FakeHttpHandler().Respond(ProviderFixture.Load("aws", scenario));
        using var aws = new AwsTranslator("AKIAABCDEFGHIJKLMNOP", "aws-secret-0123456789", "eu-west-1", TimeSpan.FromSeconds(5), new FakeAwsHttpClientFactory(handler));
        var (result, error) = await Run(aws);
        AssertOutcome(scenario, expected, result, error);

        var req = Assert.Single(handler.Requests); // MaxErrorRetry = 0
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("translate.eu-west-1.amazonaws.com", req.Uri.Host);
        // AWS SDK v4 sends Translate calls as Smithy RPC v2 CBOR (the operation is in the path, not X-Amz-Target).
        Assert.Equal("/service/AWSShineFrontendService_20170701/operation/TranslateText", req.Uri.AbsolutePath);
        Assert.Equal("rpc-v2-cbor", req.Headers["smithy-protocol"]);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAABCDEFGHIJKLMNOP/", req.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Contains("TargetLanguageCode", req.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ThrottlingException", 400, TranslationErrorKind.RateLimited)]
    [InlineData("TooManyRequestsException", 429, TranslationErrorKind.RateLimited)]
    [InlineData("UnrecognizedClientException", 400, TranslationErrorKind.Auth)]
    [InlineData("InvalidSignatureException", 400, TranslationErrorKind.Auth)]
    [InlineData("AccessDeniedException", 400, TranslationErrorKind.Auth)]
    [InlineData("ExpiredTokenException", 400, TranslationErrorKind.Auth)]
    [InlineData("LimitExceededException", 400, TranslationErrorKind.QuotaExceeded)]
    [InlineData("UnsupportedLanguagePairException", 400, TranslationErrorKind.Unsupported)]
    [InlineData("TextSizeLimitExceededException", 400, TranslationErrorKind.Unsupported)]
    [InlineData("DetectedLanguageLowConfidenceException", 400, TranslationErrorKind.Unsupported)]
    [InlineData("ServiceUnavailableException", 503, TranslationErrorKind.Transient)]
    [InlineData("InternalServerException", 500, TranslationErrorKind.Transient)]
    [InlineData("Whatever", 502, TranslationErrorKind.Transient)]
    public void Aws_error_codes_map_to_kinds(string code, int status, TranslationErrorKind kind)
    {
        var ex = new AmazonServiceException("raw sdk text", null, ErrorType.Sender, code, "req-1", (HttpStatusCode)status);
        var mapped = AwsTranslator.Map(ex, ProviderCatalog.Aws, CancellationToken.None);
        Assert.Equal(kind, mapped!.Kind);
        Assert.DoesNotContain("raw sdk text", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Aws_client_exception_is_Transient()
    {
        var mapped = AwsTranslator.Map(new AmazonClientException("socket"), ProviderCatalog.Aws, CancellationToken.None);
        Assert.Equal(TranslationErrorKind.Transient, mapped!.Kind);
    }

    [Fact]
    public async Task Missing_credentials_are_Configuration()
    {
        var http = new FakeHttpHandler().Client();
        Assert.Equal(TranslationErrorKind.Configuration, (await Assert.ThrowsAsync<TranslationException>(() => new DeepLTranslator(http, "").TranslateAsync(Request))).Kind);
        Assert.Equal(TranslationErrorKind.Configuration, (await Assert.ThrowsAsync<TranslationException>(() => new ClaudeTranslator("", "", http).TranslateAsync(Request))).Kind);
        Assert.Equal(TranslationErrorKind.Configuration, (await Assert.ThrowsAsync<TranslationException>(() => new LibreTranslateTranslator(http, "", null).TranslateAsync(Request))).Kind);
        Assert.Equal(TranslationErrorKind.Configuration, Assert.Throws<TranslationException>(() => new AwsTranslator("", "", "us-east-1")).Kind);
    }

    [Fact]
    public async Task Network_failure_is_Transient_and_keeps_the_HttpRequestError()
    {
        var handler = new FakeHttpHandler().Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new GoogleFreeTranslator(handler.Client()).TranslateAsync(Request));
        Assert.Equal(TranslationErrorKind.Transient, ex.Kind);
        Assert.Equal(HttpRequestError.ConnectionError, Assert.IsType<HttpRequestException>(ex.InnerException).HttpRequestError);
        Assert.DoesNotContain("refused", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClient_timeout_inside_a_provider_is_Timeout()
    {
        var handler = new FakeHttpHandler().Throw(new TaskCanceledException("timeout", new TimeoutException()));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new GoogleFreeTranslator(handler.Client()).TranslateAsync(Request));
        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
    }
}

/// <summary>Hands the AWS SDK an HttpClient over the fake handler (tests only).</summary>
internal sealed class FakeAwsHttpClientFactory(FakeHttpHandler handler) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => handler.Client();

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
}
