using Anthropic;
using Anthropic.Models.Beta.Messages;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

/// <summary>
/// Translates with Claude using the player's own Anthropic API key. Slower than the machine-translation
/// providers but far better with RTS slang, abbreviations and banter.
/// </summary>
public sealed class ClaudeTranslator(string apiKey, string model) : ITranslator
{
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

    private readonly AnthropicClient _client = new() { ApiKey = apiKey };

    public string Id => ProviderCatalog.Claude;
    public bool IsMetered => true;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new TranslationException("Claude needs an Anthropic API key (Settings → Translation).");

        var target = LanguageCatalog.NameOf(request.TargetLanguage);
        var useFallbacks = SupportsServerFallback(model);

        BetaMessage response;
        try
        {
            response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model,
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TranslationException($"Claude: {ex.Message}", ex);
        }

        if (response.StopReason == "refusal")
            throw new TranslationException("Claude declined to translate this message.");

        var text = string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t is not null));
        return Parse(text, Id);
    }

    internal static TranslationResult Parse(string reply, string providerId)
    {
        var line = reply.Trim();
        var bar = line.IndexOf('|');
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
