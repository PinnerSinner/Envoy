namespace Envoy.Core.Translation;

public enum ProviderCredential
{
    None,
    ApiKey,
    UrlAndOptionalKey,
    AwsKeys,
}

/// <summary>Static description of a translation provider for the settings UI.</summary>
public sealed record ProviderInfo(
    string Id,
    string Name,
    string Blurb,
    ProviderCredential Credential,
    bool IsMetered,
    string? SignUpUrl = null);

public static class ProviderCatalog
{
    public const string GoogleFree = "google-free";
    public const string EdgeFree = "edge-free";
    public const string DeepL = "deepl";
    public const string LibreTranslate = "libretranslate";
    public const string Aws = "aws";
    public const string Claude = "claude";

    public static IReadOnlyList<ProviderInfo> All { get; } =
    [
        new(GoogleFree, "Google Translate (free)",
            "No key or account. Uses the same public endpoint as the Google Translate web widget. Good quality, but unofficial and can be rate-limited.",
            ProviderCredential.None, IsMetered: false),
        new(EdgeFree, "Microsoft Translator (free)",
            "No key or account. Uses the endpoint built into the Edge browser's page translation. A good automatic fallback for Google.",
            ProviderCredential.None, IsMetered: false),
        new(DeepL, "DeepL",
            "Excellent quality for European languages and solid for Chinese, Japanese and Korean. The DeepL API Free plan gives 500,000 characters a month at no cost (card needed to sign up).",
            ProviderCredential.ApiKey, IsMetered: true, "https://www.deepl.com/pro-api"),
        new(Claude, "Claude (Anthropic)",
            "Best at gamer slang, abbreviations and tone (\"666\", \"ㅈㅈ\", \"изи\"). Pay-as-you-go with your own Anthropic API key.",
            ProviderCredential.ApiKey, IsMetered: true, "https://console.anthropic.com/"),
        new(Aws, "Amazon Translate",
            "Use your own AWS credentials (e.g. credits). Create an IAM user with only the translate:TranslateText permission.",
            ProviderCredential.AwsKeys, IsMetered: true, "https://aws.amazon.com/translate/"),
        new(LibreTranslate, "LibreTranslate",
            "Open-source and self-hostable, so translation can run entirely on your own PC. Point it at your server's URL.",
            ProviderCredential.UrlAndOptionalKey, IsMetered: false, "https://github.com/LibreTranslate/LibreTranslate"),
    ];

    public static ProviderInfo? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
