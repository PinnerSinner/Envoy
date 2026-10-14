using Envoy.Core.Languages;
using Envoy.Core.Phrases;
using Envoy.Core.Translation;

namespace Envoy.Core.Pipeline;

/// <summary>Translates what you type into the opponent's language, preferring hand-written phrasebook entries.</summary>
public sealed class OutgoingTranslator(Func<ITranslator> translator, PhraseBook phrases)
{
    public async Task<TranslationResult> TranslateAsync(string text, string myLanguage, string targetLanguage, CancellationToken ct = default)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new TranslationResult("", myLanguage, "none");

        if (LanguageCatalog.SameLanguage(myLanguage, targetLanguage))
            return new TranslationResult(trimmed, myLanguage, "none");

        // Exact phrasebook hits are human translations: better than any machine output, instant and free.
        if (phrases.FindByText(trimmed, myLanguage) is { } phrase && phrase.In(targetLanguage) is { } known)
            return new TranslationResult(known, myLanguage, "phrasebook", FromGlossary: true);

        return await translator().TranslateAsync(new TranslationRequest(trimmed, targetLanguage, myLanguage), ct).ConfigureAwait(false);
    }

    /// <summary>Picks the reply language: explicit choice, else last opponent language, else English.</summary>
    public static string ResolveTarget(string configured, string? lastOpponentLanguage, string myLanguage)
    {
        if (configured != LanguageCatalog.Auto) return configured;
        if (lastOpponentLanguage is not null) return lastOpponentLanguage;
        return myLanguage == "en" ? "zh-CN" : "en";
    }
}
