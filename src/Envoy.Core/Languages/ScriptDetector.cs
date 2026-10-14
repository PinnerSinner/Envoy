namespace Envoy.Core.Languages;

/// <summary>
/// Cheap, offline guess at a message's language from the characters it uses. It can't separate Latin-script
/// languages from each other, but it is right about Chinese / Korean / Japanese / Russian etc., which is exactly
/// where it matters: it lets us skip translating messages already in the player's language and pick an OCR engine.
/// </summary>
public static class ScriptDetector
{
    public static Script Dominant(string text)
    {
        var counts = Count(text);
        if (counts.Count == 0) return Script.Unknown;
        // Japanese mixes kana with kanji; any kana at all means Japanese rather than Chinese.
        if (counts.TryGetValue(Script.Kana, out var kana) && kana > 0) return Script.Kana;
        return counts.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>Best-guess language code, or null when the script is shared by many languages (Latin).</summary>
    public static string? GuessLanguage(string text) => Dominant(text) switch
    {
        Script.Han => LooksTraditional(text) ? "zh-TW" : "zh-CN",
        Script.Kana => "ja",
        Script.Hangul => "ko",
        Script.Cyrillic => text.IndexOfAny(['і', 'ї', 'є', 'ґ', 'І', 'Ї', 'Є', 'Ґ']) >= 0 ? "uk" : "ru",
        Script.Arabic => "ar",
        Script.Thai => "th",
        Script.Greek => "el",
        Script.Hebrew => "he",
        Script.Latin when HasVietnameseMarks(text) => "vi",
        _ => null,
    };

    public static Script Classify(char c) => c switch
    {
        >= 'a' and <= 'z' or >= 'A' and <= 'Z' => Script.Latin,
        >= 'À' and <= 'ɏ' => Script.Latin,
        >= 'Ḁ' and <= 'ỿ' => Script.Latin,
        >= 'Ͱ' and <= 'Ͽ' => Script.Greek,
        >= 'Ѐ' and <= 'ԯ' => Script.Cyrillic,
        >= '֐' and <= '׿' => Script.Hebrew,
        >= '؀' and <= 'ۿ' => Script.Arabic,
        >= 'ऀ' and <= 'ॿ' => Script.Devanagari,
        >= '฀' and <= '๿' => Script.Thai,
        >= 'ᄀ' and <= 'ᇿ' => Script.Hangul,
        >= '㄰' and <= '㆏' => Script.Hangul,
        >= '가' and <= '힯' => Script.Hangul,
        >= '぀' and <= 'ヿ' => Script.Kana,
        >= 'ㇰ' and <= 'ㇿ' => Script.Kana,
        >= '㐀' and <= '䶿' => Script.Han,
        >= '一' and <= '鿿' => Script.Han,
        >= '豈' and <= '﫿' => Script.Han,
        _ => Script.Unknown,
    };

    public static bool ContainsScript(string text, Script script) => text.Any(c => Classify(c) == script);

    private static Dictionary<Script, int> Count(string text)
    {
        var counts = new Dictionary<Script, int>();
        foreach (var c in text)
        {
            var s = Classify(c);
            if (s == Script.Unknown) continue;
            counts[s] = counts.GetValueOrDefault(s) + 1;
        }
        return counts;
    }

    // A handful of very common characters that only appear in Traditional Chinese.
    private const string TraditionalOnly = "們這個來對說時會為過還麼與給讓開關點錯當經嗎見後機發現學問題實體間謝謝輸贏贏戰隊";

    private static bool LooksTraditional(string text) => text.Any(c => TraditionalOnly.Contains(c));

    private const string VietnameseMarks = "ăâđêôơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ";

    private static bool HasVietnameseMarks(string text) =>
        text.ToLowerInvariant().Count(c => VietnameseMarks.Contains(c)) >= 2;
}
