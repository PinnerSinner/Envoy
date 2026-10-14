using System.Diagnostics.CodeAnalysis;
using System.Net;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

/// <summary>
/// Translates with Claude using the player's own Anthropic API key. Slower than the machine-translation
/// providers but far better with RTS slang, abbreviations and banter.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Translators are rebuilt on settings edits and own no handle that needs disposal: the HttpClient is AppHost's (disposing the client would dispose it).")]
public sealed class ClaudeTranslator : ITranslator
{
    /// <summary>Per-attempt timeout when none is given (REQ-TRR-23).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Models offered in settings. Haiku is much cheaper per message; Opus is the most capable.</summary>
    public static IReadOnlyList<string> SuggestedModels { get; } = [DefaultModel, "claude-sonnet-5-5", "claude-haiku-5-5"];

    private const string SystemPrompt = """
        You translate in-game chat for Age of Empires (AoE I/II/III/IV and Age of Mythology) players.
        Translate the user's message into the requested language the way a fellow player would say it:
        keep it short and casual, keep player names, numbers and emoji, and render gamer slang by meaning
        (e.g. Chinese "666" means "nice!", Korean "ㅈㅈ" means "gg, I give up", Russian "изи" means "ez").
        Common RTS terms (rush, boom, tower rush, fast castle, feudal, imperial, vills, TC, gg, wp) should stay
        as the term players actually use in the target language.

        Reply with exactly one line in the form `<source-language-code>|<translation>`, where the code is
        the BCP-47 code of the message's original language (e.g. zh-CN, ko, ru, es). Output nothing else.
        """;

    private readonly AnthropicClient _client;
    private readonly string _apiKey;
    private readonly string _model;

    /// <param name="http">The shared client (AppHost's); null only in tests that do not send.</param>
    /// <param name="timeout">The per-attempt timeout; the SDK never retries on its own (REQ-TRR-23).</param>
    public ClaudeTranslator(string apiKey, string model, HttpClient? http = null, TimeSpan? timeout = null)
    {
        _apiKey = apiKey;
        _model = model;
        var attempt = timeout ?? DefaultTimeout;
        _client = http is null
            ? new AnthropicClient { ApiKey = apiKey, MaxRetries = 0, Timeout = attempt }
            : new AnthropicClient { ApiKey = apiKey, MaxRetries = 0, Timeout = attempt, HttpClient = http };
    }

    /// <summary>The SDK client's retry count (always 0) and timeout, for tests.</summary>
    internal (int? MaxRetries, TimeSpan? Timeout) ClientSettings => (_client.MaxRetries, _client.Timeout);

    public string Id => ProviderCatalog.Claude;
    public bool IsMetered => true;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_apiKey)) throw TranslationErrorText.NotConfigured(TranslationErrorText.MissingKey, Id);

        var target = LanguageCatalog.NameOf(request.TargetLanguage);
        var model = string.IsNullOrWhiteSpace(_model) ? DefaultModel : _model;
        var useFallbacks = SupportsServerFallback(_model);

        BetaMessage response;
        try
        {
            response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = 1024,
                System = SystemPrompt,
                // Chat translation is a simple task: low effort keeps it fast and cheap.
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                Betas = useFallbacks ? ["server-side-fallback-2026-07-01"] : null,
                Fallbacks = useFallbacks ? new BetaFallbacksParam(new Default()) : null,
                Messages =
                [
                    new() { Role = Role.User, Content = $"Translate into {target} ({request.TargetLanguage}):\n{request.Text}" },
                ],
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Map(ex, Id, cancellationToken) is { } mapped)
        {
            throw mapped;
        }

        if (response.StopReason == "refusal")
        {
            throw new TranslationException(
                TranslationErrorText.Format(TranslationErrorText.Refused, ("provider", ProviderCatalog.NameOf(Id))),
                kind: TranslationErrorKind.Unsupported);
        }

        var text = string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t is not null));
        return Parse(text, Id);
    }

    /// <summary>
    /// The SDK exception mapped to a kind (REQ-TRR-1, 2), or null for a cancellation the caller asked for. Retry-After
    /// is not exposed by the SDK's exceptions, so Claude 429s use backoff and the breaker (spec Risk 2).
    /// </summary>
    internal static TranslationException? Map(Exception ex, string providerId, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested) return null;
        if (ex is TranslationException te) return te;
        int? status = ex is AnthropicApiException api ? (int)api.StatusCode : null;
        var kind = ex switch
        {
            AnthropicRateLimitException => TranslationErrorKind.RateLimited,
            AnthropicUnauthorizedException or AnthropicForbiddenException => TranslationErrorKind.Auth,
            Anthropic5xxException => TranslationErrorKind.Transient,
            AnthropicApiException { StatusCode: (HttpStatusCode)529 or HttpStatusCode.RequestTimeout } => TranslationErrorKind.Transient,
            AnthropicBadRequestException or AnthropicUnprocessableEntityException or AnthropicNotFoundException => TranslationErrorKind.Unsupported,
            AnthropicApiException a => ProviderHttp.Classify((int)a.StatusCode),
            AnthropicIOException or HttpRequestException or IOException => TranslationErrorKind.Transient,
            AnthropicInvalidDataException or System.Text.Json.JsonException => TranslationErrorKind.BadResponse,
            OperationCanceledException => TranslationErrorKind.Timeout,
            _ => TranslationErrorKind.Unknown,
        };
        var inner = ex is AnthropicIOException { InnerException: HttpRequestException hre } ? hre : ex;
        return kind == TranslationErrorKind.Transient && inner is HttpRequestException
            ? ProviderHttp.Wrap(inner, providerId, ct)
            : TranslationErrorText.Error(kind, providerId, inner, status);
    }

    internal static TranslationResult Parse(string reply, string providerId)
    {
        var line = reply.Trim();
        var bar = line.IndexOf('|', StringComparison.Ordinal);
        if (bar is > 0 and <= 12)
        {
            var code = line[..bar].Trim().Trim('`');
            return new TranslationResult(line[(bar + 1)..].Trim(), LanguageCatalog.Normalize(code), providerId);
        }
        return new TranslationResult(line, null, providerId);
    }

    private static bool SupportsServerFallback(string m) =>
        m is DefaultModel or "claude-sonnet-5-5" || m.StartsWith("claude-fable", StringComparison.Ordinal);
}
