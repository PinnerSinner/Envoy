namespace Envoy.Core.Chat;

/// <summary>Converts a list of player names to and from the single text box on the Translation page.</summary>
public static class PlayerListText
{
    private static readonly char[] Separators = [',', '\n', '\r', ';'];

    /// <summary>Splits on , ; and line breaks, trims, drops empties and keeps the first spelling of each name (case-insensitive).</summary>
    public static List<string> Parse(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public static string Format(IEnumerable<string> players) => string.Join(", ", players);
}
