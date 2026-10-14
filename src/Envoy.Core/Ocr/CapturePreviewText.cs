using Envoy.Core.Text;

namespace Envoy.Core.Ocr;

/// <summary>Text for the live capture preview (contract §8.7).</summary>
public static class CapturePreviewText
{
    /// <summary>Shown when OCR read nothing.</summary>
    public const string NoText = "(no text found)";

    /// <summary>{lines} = number of lines read; {ocrMs} = OCR time in milliseconds. Formatted with the current culture.</summary>
    public const string StatsFormat = "{lines} lines · OCR {ocrMs:0} ms";

    public static string Stats(int lines, TimeSpan ocrTime) =>
        TextTemplate.Format(StatsFormat, null, ("lines", lines), ("ocrMs", ocrTime.TotalMilliseconds));

    /// <summary><see cref="NoText"/> when empty, else one line per entry.</summary>
    public static string Lines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count == 0 ? NoText : string.Join(Environment.NewLine, lines);
    }
}
