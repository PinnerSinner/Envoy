using Envoy.Core.Text;

namespace Envoy.Core.Privacy;

/// <summary>
/// "What leaves your PC" (REQ-OUXA-47, contract §8.1), shown on the guide's first step, the War Room callout and About
/// › Privacy. Whole sentences with named placeholders (contract §8.7).
/// </summary>
public static class PrivacyText
{
    /// <summary>Bump when the data flows change: every user is asked again and translation stays off until they are.</summary>
    public const int Version = 1;

    public const string Heading = "What leaves your PC";

    /// <summary>{providers} = provider chain display names joined with " then ".</summary>
    public const string ChatGoesTo = "Chat lines Envoy reads, and replies you translate, are sent to {providers} to be translated.";

    public const string KeylessNote = "The free Google and Microsoft options use unofficial endpoints without an account. You can choose another provider on the Translation page.";
    public const string UpdatesGoTo = "Update checks go to GitHub.";
    public const string StaysLocal = "Text recognition (OCR) and speech run on your PC and send nothing.";
    public const string Acknowledge = "I understand what leaves my PC";
    public const string TranslationOff = "Translation is off until you confirm what leaves your PC. Open Envoy to turn it on.";

    /// <summary>{providers} as in <see cref="ChatGoesTo"/>.</summary>
    public const string CalloutText = "Envoy sends chat to {providers} to translate it. Translation stays off until you confirm.";

    public const string DocUrl = "https://github.com/PinnerSinner/Envoy/blob/main/docs/privacy.md";

    /// <summary>The separator between provider names in {providers}.</summary>
    public const string ProviderSeparator = " then ";

    /// <summary>The provider chain's display names, in order, e.g. "Google Translate (free) then Microsoft Translator (free)".</summary>
    public static string Providers(IReadOnlyList<string> providerNames)
    {
        ArgumentNullException.ThrowIfNull(providerNames);
        return string.Join(ProviderSeparator, providerNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal));
    }

    public static string ChatGoesToFor(IReadOnlyList<string> providerNames) =>
        TextTemplate.Format(ChatGoesTo, null, ("providers", Providers(providerNames)));

    public static string CalloutFor(IReadOnlyList<string> providerNames) =>
        TextTemplate.Format(CalloutText, null, ("providers", Providers(providerNames)));

    /// <summary>The disclosure lines in display order: chat, keyless note, updates, local.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<string> providerNames) =>
        [ChatGoesToFor(providerNames), KeylessNote, UpdatesGoTo, StaysLocal];

    /// <summary>True when the acknowledged version covers the current data flows.</summary>
    public static bool IsAcknowledged(int acknowledgedVersion) => acknowledgedVersion >= Version;
}
