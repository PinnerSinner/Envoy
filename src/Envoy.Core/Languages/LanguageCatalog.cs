namespace Envoy.Core.Languages;

/// <summary>Languages Envoy knows about, ordered roughly by how often they show up in AoE ranked lobbies.</summary>
public static class LanguageCatalog
{
    public const string Auto = "auto";

    public static IReadOnlyList<Language> All { get; } =
    [
        new("en", "English", "English", "en-US", Script.Latin),
        new("zh-CN", "Chinese (Simplified)", "简体中文", "zh-Hans-CN", Script.Han),
        new("zh-TW", "Chinese (Traditional)", "繁體中文", "zh-Hant-TW", Script.Han),
        new("ko", "Korean", "한국어", "ko-KR", Script.Hangul),
        new("ja", "Japanese", "日本語", "ja-JP", Script.Kana),
        new("ru", "Russian", "Русский", "ru-RU", Script.Cyrillic),
        new("uk", "Ukrainian", "Українська", "uk-UA", Script.Cyrillic),
        new("es", "Spanish", "Español", "es-ES", Script.Latin),
        new("pt", "Portuguese", "Português", "pt-BR", Script.Latin),
        new("de", "German", "Deutsch", "de-DE", Script.Latin),
        new("fr", "French", "Français", "fr-FR", Script.Latin),
        new("it", "Italian", "Italiano", "it-IT", Script.Latin),
        new("pl", "Polish", "Polski", "pl-PL", Script.Latin),
        new("tr", "Turkish", "Türkçe", "tr-TR", Script.Latin),
        new("vi", "Vietnamese", "Tiếng Việt", "vi-VN", Script.Latin),
        new("nl", "Dutch", "Nederlands", "nl-NL", Script.Latin),
        new("cs", "Czech", "Čeština", "cs-CZ", Script.Latin),
        new("sv", "Swedish", "Svenska", "sv-SE", Script.Latin),
        new("ro", "Romanian", "Română", "ro-RO", Script.Latin),
        new("hu", "Hungarian", "Magyar", "hu-HU", Script.Latin),
        new("id", "Indonesian", "Bahasa Indonesia", "id-ID", Script.Latin),
        new("ar", "Arabic", "العربية", "ar-SA", Script.Arabic),
        new("th", "Thai", "ไทย", "th-TH", Script.Thai),
        new("el", "Greek", "Ελληνικά", "el-GR", Script.Greek),
    ];

    private static readonly Dictionary<string, Language> ByCode =
        All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    public static Language? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (ByCode.TryGetValue(code, out var exact)) return exact;
        return ByCode.TryGetValue(Normalize(code), out var normalized) ? normalized : null;
    }

    public static string NameOf(string? code) => Find(code)?.EnglishName ?? code ?? "Unknown";

    /// <summary>
    /// Maps the many spellings providers return ("ZH", "zh-Hans", "zh_cn", "PT-BR") onto Envoy's canonical codes.
    /// Unknown codes are returned lower-cased so they still compare sensibly.
    /// </summary>
    public static string Normalize(string code)
    {
        var c = code.Trim().Replace('_', '-');
        var lower = c.ToLowerInvariant();
        return lower switch
        {
            "zh" or "zh-cn" or "zh-hans" or "zh-hans-cn" or "zh-sg" or "zh-chs" => "zh-CN",
            "zh-tw" or "zh-hant" or "zh-hant-tw" or "zh-hk" or "zh-cht" or "zt" => "zh-TW",
            "en-us" or "en-gb" => "en",
            "pt-br" or "pt-pt" => "pt",
            "iw" or "he" => "he",
            "in" => "id",
            _ when ByCode.ContainsKey(c) => ByCode[c].Code,
            _ when lower.Contains('-') && ByCode.ContainsKey(lower.Split('-')[0]) => ByCode[lower.Split('-')[0]].Code,
            _ => lower,
        };
    }

    /// <summary>True when two codes refer to the same language family (zh-CN and zh-TW count as the same for "don't translate").</summary>
    public static bool SameLanguage(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na == nb) return true;
        return na.StartsWith("zh", StringComparison.Ordinal) && nb.StartsWith("zh", StringComparison.Ordinal);
    }
}
