using System.Globalization;
using Envoy.Core.Text;

namespace Envoy.Core.Translation;

/// <summary>Text about saved translations on the Translation page (contract §8.7).</summary>
public static class TranslationCacheText
{
    public const string SavedCount = "{count} translations saved on this PC.";
    public const string SavedOne = "1 translation saved on this PC.";
    public const string SavedNone = "No translations saved yet.";

    public static string Count(int count) => count switch
    {
        <= 0 => SavedNone,
        1 => SavedOne,
        _ => TextTemplate.Format(SavedCount, CultureInfo.CurrentCulture, ("count", count)),
    };
}
