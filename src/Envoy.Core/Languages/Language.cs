namespace Envoy.Core.Languages;

/// <summary>Writing system a language is normally typed in. Used to guess a message's language offline.</summary>
public enum Script
{
    Unknown,
    Latin,
    Han,
    Kana,
    Hangul,
    Cyrillic,
    Arabic,
    Thai,
    Greek,
    Hebrew,
    Devanagari,
}

/// <param name="Code">Canonical code used throughout Envoy (Google-style, e.g. "zh-CN").</param>
/// <param name="OcrTag">BCP-47 tag Windows OCR expects for this language.</param>
public sealed record Language(string Code, string EnglishName, string NativeName, string OcrTag, Script Script)
{
    public string DisplayName => EnglishName == NativeName ? EnglishName : $"{EnglishName} ({NativeName})";
    public override string ToString() => DisplayName;
}
