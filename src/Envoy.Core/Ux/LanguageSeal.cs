using Envoy.Core.Languages;

namespace Envoy.Core.Ux;

/// <summary>The wax colours a dispatch seal can take; each is the palette brush <c>Wax.{name}</c>.</summary>
public enum SealWax
{
    Gules,
    Azure,
    Vert,
    Or,
    Purpure,
    Sable,
}

/// <summary>
/// The wax seal on an overlay dispatch (spec 09a, REQ-HRD-8): a glyph for the sender's language and a wax colour.
/// Chinese 中 on gules, Korean 한 on azure, Japanese 日 on or, Russian Р on vert; every other known language shows the first
/// letter of its English name on purpure; an unknown or missing code shows "?" on sable.
/// </summary>
public sealed record LanguageSeal(string Glyph, SealWax Wax)
{
    /// <summary>The seal shown when the language is not known.</summary>
    public static LanguageSeal Unknown { get; } = new("?", SealWax.Sable);

    /// <summary>The palette brush key for <see cref="Wax"/>, e.g. <c>Wax.Gules</c>.</summary>
    public string BrushKey => "Wax." + Wax;

    /// <summary>The seal for a language code in any spelling <see cref="LanguageCatalog.Normalize"/> accepts.</summary>
    public static LanguageSeal For(string? languageCode)
    {
        var language = LanguageCatalog.Find(languageCode);
        if (language is null) return Unknown;
        return language.Code switch
        {
            "zh-CN" or "zh-TW" => new("中", SealWax.Gules),
            "ko" => new("한", SealWax.Azure),
            "ja" => new("日", SealWax.Or),
            "ru" => new("Р", SealWax.Vert),
            _ => new(language.EnglishName[..1].ToUpperInvariant(), SealWax.Purpure),
        };
    }
}
