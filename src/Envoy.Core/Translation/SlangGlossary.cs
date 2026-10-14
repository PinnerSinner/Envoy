using System.Text.RegularExpressions;

namespace Envoy.Core.Translation;

/// <summary>
/// Gaming shorthand that machine translators mangle ("666" → "666", "ㅈㅈ" → "ㅈㅈ"). When a whole message is one
/// of these we answer instantly and offline, which is also free and never rate-limited.
/// Meanings are in English; for other target languages the meaning is sent through the normal translator.
/// </summary>
public static partial class SlangGlossary
{
    public sealed record Entry(Regex Pattern, string Language, string Meaning);

    private static Entry E(string pattern, string lang, string meaning) =>
        new(new Regex($"^(?:{pattern})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), lang, meaning);

    public static IReadOnlyList<Entry> Entries { get; } =
    [
        // Chinese
        E("6{3,}|六六六", "zh-CN", "Nice! / Well played (\"666\")"),
        E("2333+", "zh-CN", "LOL"),
        E("88+|886|拜拜", "zh-CN", "Bye!"),
        E("nb|nbnb|牛逼+|牛b|牛批|牛+", "zh-CN", "Awesome! / Damn, that's good"),
        E("tql|太强了", "zh-CN", "Too strong! / You're so good"),
        E("yyds", "zh-CN", "GOAT (greatest of all time)"),
        E("xswl", "zh-CN", "LMAO"),
        E("卧槽+|我靠|wc|握草", "zh-CN", "WTF / Holy crap"),
        E("菜|太菜了|菜鸡", "zh-CN", "Noob / That's weak"),
        E("哈{2,}|hhh+", "zh-CN", "Haha"),
        E("[?？]{2,}", "zh-CN", "??? (what are you doing?)"),
        E("gg思密达", "zh-CN", "GG (mocking)"),
        E("加油", "zh-CN", "Come on! / You can do it"),
        E("好的|好|ok了", "zh-CN", "OK"),
        E("稳", "zh-CN", "Steady / We've got this"),
        // Korean
        E("[ㅋ]{2,}", "ko", "LOL"),
        E("[ㅎ]{2,}", "ko", "Hehe"),
        E("ㅈㅈ", "ko", "GG — I give up"),
        E("ㄱㄱ", "ko", "Let's go!"),
        E("ㅇㅇ", "ko", "Yeah / OK"),
        E("ㄴㄴ", "ko", "No no"),
        E("ㅇㅋ", "ko", "OK"),
        E("ㅅㄱ|ㅅㄱㅇ", "ko", "Good game (thanks for the effort)"),
        E("ㄳ|ㄱㅅ", "ko", "Thanks"),
        E("ㅈㅅ", "ko", "Sorry"),
        E("[ㅠㅜ]{2,}", "ko", "(crying) T_T"),
        E("ㅎㅇ", "ko", "Hi"),
        E("ㅂㅂ|ㅂㅇ", "ko", "Bye"),
        // Russian
        E("гг|gg вп|гг вп", "ru", "GG"),
        E("изи|ez", "ru", "Easy"),
        E("нуб", "ru", "Noob"),
        E("спс|пасиб|пасибо", "ru", "Thanks"),
        E("пж|плз|плиз", "ru", "Please"),
        E("лол", "ru", "LOL"),
        E("а?ха(ха)+х?", "ru", "Haha"),
        E("ку|ку-ку|прив", "ru", "Hi"),
        E("бб|пока", "ru", "Bye"),
        // Spanish / Portuguese
        E("j[aeiou](j[aeiou])+j?", "es", "Haha"),
        E("k{4,}", "pt", "LOL"),
        E("(rs)+", "pt", "LOL"),
        E("vlw|valeu", "pt", "Thanks"),
        E("blz|beleza", "pt", "OK / Cool"),
        // Japanese
        E("w{3,}|草+", "ja", "LOL"),
        E("よろしく(お願いします)?|よろ", "ja", "Nice to meet you / Have a good game"),
        E("おつ|乙", "ja", "Good game (thanks for playing)"),
        // Vietnamese
        E("kkk+", "vi", "LOL"),
    ];

    /// <summary>Returns the meaning when the whole message is a known slang expression.</summary>
    public static Entry? Match(string text)
    {
        var t = text.Trim().TrimEnd('!', '！', '.', '。', '~', '～');
        if (t.Length == 0 || t.Length > 24) return null;
        return Entries.FirstOrDefault(e => e.Pattern.IsMatch(t));
    }
}
