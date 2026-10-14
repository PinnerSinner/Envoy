namespace Envoy.Core.Phrases;

/// <summary>One row of the Phrasebook page's preview list.</summary>
public sealed record PhrasePreviewRow(string Icon, string Category, string Mine, string Theirs);

/// <summary>Builds the Phrasebook page's preview and the starter user phrases file.</summary>
public static class PhrasePreview
{
    /// <summary>
    /// One row per phrase, in book order. Mine = the phrase in <paramref name="myLanguage"/>, else English, else its id;
    /// Theirs = the phrase in <paramref name="targetLanguage"/>, else <see cref="PhrasebookText.Missing"/>.
    /// </summary>
    public static IReadOnlyList<PhrasePreviewRow> Build(PhraseBook book, string myLanguage, string targetLanguage) =>
        book.All
            .Select(p => new PhrasePreviewRow(p.Icon, p.Category, p.In(myLanguage) ?? p.In("en") ?? p.Id, p.In(targetLanguage) ?? PhrasebookText.Missing))
            .ToList();

    /// <summary>The JSON written to <c>phrases.json</c> by "Edit my phrases" when the file doesn't exist yet (file content, not UI text).</summary>
    public const string UserFileTemplate = """
        [
          {
            "id": "my-first-phrase",
            "category": "Mine",
            "icon": "★",
            "text": {
              "en": "Nice to meet you!",
              "zh-CN": "很高兴认识你！",
              "ko": "만나서 반가워요!"
            }
          }
        ]
        """;
}
