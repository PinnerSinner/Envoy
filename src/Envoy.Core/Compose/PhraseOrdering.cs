using Envoy.Core.Languages;
using Envoy.Core.Phrases;
using Envoy.Core.Settings;

namespace Envoy.Core.Compose;

/// <summary>
/// Orders the reply box's quick-phrase chips for the language you are writing to (REQ-OUXC-42) and counts which phrases
/// you send (REQ-OUXC-43). The usage map holds phrase ids and counts only, never text. UI thread only.
/// </summary>
public static class PhraseOrdering
{
    public const int MaxCount = 999;
    public const int MaxIdsPerLanguage = 100;

    /// <summary>
    /// <see cref="PhraseOrder.BookOrder"/>: unchanged. <see cref="PhraseOrder.MostUsed"/>: phrases with a translation in
    /// <paramref name="targetLanguage"/> first, then the rest; within each group by use count for that language,
    /// descending, ties in phrasebook order.
    /// </summary>
    public static IReadOnlyList<Phrase> Order(IReadOnlyList<Phrase> phrases, string targetLanguage,
        IReadOnlyDictionary<string, Dictionary<string, int>> usage, PhraseOrder mode)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        if (mode == PhraseOrder.BookOrder) return phrases;
        var counts = usage is not null && usage.TryGetValue(LanguageCatalog.Normalize(targetLanguage ?? ""), out var c) ? c : null;
        return phrases
            .Select((p, index) => (Phrase: p, Index: index))
            .OrderBy(x => x.Phrase.In(targetLanguage ?? "") is null ? 1 : 0)
            .ThenByDescending(x => counts is not null && counts.TryGetValue(x.Phrase.Id, out var n) ? n : 0)
            .ThenBy(x => x.Index)
            .Select(x => x.Phrase)
            .ToList();
    }

    /// <summary>
    /// Increments <c>usage[Normalize(target)][phraseId]</c>, saturating at <see cref="MaxCount"/>. A language keeps at most
    /// <see cref="MaxIdsPerLanguage"/> ids: beyond that the lowest counts are dropped (never the one just used).
    /// </summary>
    public static void RecordUse(Dictionary<string, Dictionary<string, int>> usage, string targetLanguage, string phraseId)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (string.IsNullOrWhiteSpace(targetLanguage) || string.IsNullOrEmpty(phraseId)) return;
        var language = LanguageCatalog.Normalize(targetLanguage);
        if (!usage.TryGetValue(language, out var counts) || counts is null) usage[language] = counts = new(StringComparer.Ordinal);
        counts[phraseId] = Math.Min(MaxCount, (counts.TryGetValue(phraseId, out var n) ? Math.Max(0, n) : 0) + 1);

        if (counts.Count <= MaxIdsPerLanguage) return;
        var drop = counts
            .Where(kv => kv.Key != phraseId)
            .OrderBy(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(counts.Count - MaxIdsPerLanguage)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in drop) counts.Remove(id);
    }
}
