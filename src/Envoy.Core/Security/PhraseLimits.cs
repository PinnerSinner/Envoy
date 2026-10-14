namespace Envoy.Core.Security;

/// <summary>
/// Bounds on a user's <c>phrases.json</c> (REQ-QSC-32). The file may come from someone else, and its text is typed
/// into game chat, so it is limited in size and cleaned like any other outgoing text.
/// </summary>
public static class PhraseLimits
{
    /// <summary>A larger user file is ignored and only the built-in phrases load.</summary>
    public const long MaxFileBytes = 1_048_576;

    public const int MaxPhrases = 500, MaxTextLength = 500, MaxIdLength = 64, MaxCategoryLength = 64, MaxIconLength = 16;

    /// <summary>Cuts <paramref name="value"/> to at most <paramref name="max"/> UTF-16 units without splitting a surrogate pair.</summary>
    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= max) return value;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1])) cut--;
        return value[..cut];
    }
}
