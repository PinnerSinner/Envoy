using Envoy.Core.Languages;
using Envoy.Core.Text;

namespace Envoy.Core.Translation;

/// <summary>Text for the "Try it" box on the Translation page (contract §8.7).</summary>
public static class TranslationTestText
{
    /// <summary>The sample the input box starts with.</summary>
    public const string SampleInput = "你好！再来一局吗？";

    /// <summary>Shown while the test translation runs.</summary>
    public const string Pending = "…";

    /// <summary>{translation} = translated text; {detected} and {mine} = language names; {provider} = provider name.</summary>
    public const string ResultFormat = "{translation}    ({detected} → {mine}, via {provider})";

    /// <summary>{message} = the error message. Today's text; 03 replaces it with error-kind text.</summary>
    public const string FailureFormat = "⚠ {message}";

    /// <summary>Describes a result. Provider name = catalog name or the raw id; language names via <see cref="LanguageCatalog.NameOf"/>.</summary>
    public static string Describe(TranslationResult result, string myLanguage)
    {
        ArgumentNullException.ThrowIfNull(result);
        var provider = ProviderCatalog.Find(result.ProviderId)?.Name ?? result.ProviderId;
        return TextTemplate.Format(ResultFormat, null,
            ("translation", result.Text),
            ("detected", LanguageCatalog.NameOf(result.DetectedSourceLanguage)),
            ("mine", LanguageCatalog.NameOf(myLanguage)),
            ("provider", provider));
    }

    public static string Failure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return TextTemplate.Format(FailureFormat, null, ("message", ex.Message));
    }
}
