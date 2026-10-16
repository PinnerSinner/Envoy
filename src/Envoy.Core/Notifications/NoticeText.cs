using Envoy.Core.Text;

namespace Envoy.Core.Notifications;

/// <summary>Advisory notice sentences (contract §8.7, REQ-OUXA-16): what happened, what Envoy does now, where to look.</summary>
public static class NoticeText
{
    public const string OcrMissingTitle = "Text recognition pack missing";

    /// <summary>{languages} = language names, e.g. "Japanese, Korean".</summary>
    public const string OcrMissing = "Envoy can't read {languages} chat until Windows' free text recognition pack is installed. Click to install.";

    public const string ProviderTitle = "Translation provider problem";

    /// <summary>{provider} = the provider's display name.</summary>
    public const string ProviderAuthFailed = "{provider} rejected your key. Envoy is using your other providers. Click to check the key.";

    /// <summary>{provider} = the provider's display name.</summary>
    public const string ProviderQuota = "{provider} has reached its limit. Envoy is using your other providers until it resets.";

    /// <summary>{provider} = the provider's display name.</summary>
    public const string ProviderCoolingDown = "{provider} keeps failing. Envoy is using your other providers and will try it again shortly.";

    public const string UpdateTitle = "Envoy update";

    /// <summary>The separator between language names in {languages}.</summary>
    public const string LanguageSeparator = ", ";

    public static string OcrMissingFor(IEnumerable<string> languageNames) =>
        TextTemplate.Format(OcrMissing, null, ("languages", string.Join(LanguageSeparator, languageNames)));

    public static string ProviderFor(string template, string providerName) =>
        TextTemplate.Format(template, null, ("provider", providerName));
}
