using System.Reflection;
using System.Text.Json;
using Envoy.Core.Chat;
using Envoy.Core.Languages;

namespace Envoy.Core.Phrases;

public sealed class Phrase
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Icon { get; set; } = "";
    public Dictionary<string, string> Text { get; set; } = new();

    public string? In(string language)
    {
        if (Text.TryGetValue(language, out var exact)) return exact;
        var normalized = LanguageCatalog.Normalize(language);
        return Text.TryGetValue(normalized, out var n) ? n : null;
    }
}

/// <summary>
/// Hand-translated quick phrases ("gl hf", "rematch?", "I'm using a translator") in the languages most common
/// in AoE matchmaking. Users can add their own in phrases.json next to settings.json.
/// </summary>
public sealed class PhraseBook(IReadOnlyList<Phrase> phrases)
{
    public IReadOnlyList<Phrase> All { get; } = phrases;

    public IEnumerable<string> Categories => All.Select(p => p.Category).Distinct();

    public Phrase? FindByText(string text, string language)
    {
        var needle = TextSimilarity.Normalize(text);
        return All.FirstOrDefault(p => p.In(language) is { } t && TextSimilarity.Normalize(t) == needle);
    }

    public static PhraseBook LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Envoy.Core.Phrases.phrases.json")
            ?? throw new InvalidOperationException("Built-in phrasebook resource missing.");
        return new PhraseBook(Deserialize(stream));
    }

    /// <summary>Built-in phrases plus any user phrases (user entries with the same id replace built-ins).</summary>
    public static PhraseBook LoadWithUserFile(string userFile)
    {
        var builtIn = LoadBuiltIn().All.ToList();
        if (!File.Exists(userFile)) return new PhraseBook(builtIn);
        try
        {
            using var stream = File.OpenRead(userFile);
            var user = Deserialize(stream);
            var ids = user.Select(p => p.Id).ToHashSet();
            return new PhraseBook([.. user, .. builtIn.Where(p => !ids.Contains(p.Id))]);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new PhraseBook(builtIn);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static List<Phrase> Deserialize(Stream stream) =>
        JsonSerializer.Deserialize<List<Phrase>>(stream, JsonOptions) ?? [];
}
