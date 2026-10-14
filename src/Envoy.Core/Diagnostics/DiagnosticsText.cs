using Envoy.Core.Text;

namespace Envoy.Core.Diagnostics;

/// <summary>Troubleshooting status lines and captions (contract §8.7).</summary>
public static class DiagnosticsText
{
    public const string Copied = "Copied. Paste it into your bug report.";

    /// <summary>{path} = where the file was saved.</summary>
    public const string ExportedFormat = "Saved the diagnostics file to {path}.";

    public const string Privacy =
        "Diagnostics stay on your PC until you paste or attach them. They never include your API keys, chat, player names or where the chat area is on screen.";

    public const string NoCrash = "No crash recorded.";

    /// <summary>{summary} = the crash marker's one-line summary.</summary>
    public const string LastCrashFormat = "Last crash: {summary}";

    public static string Exported(string path) => TextTemplate.Format(ExportedFormat, null, ("path", path));

    public static string LastCrash(CrashMarker? marker) =>
        marker is null ? NoCrash : TextTemplate.Format(LastCrashFormat, null, ("summary", marker.Summary));
}
