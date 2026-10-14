using System.Globalization;
using System.Text;
using Envoy.Core.Text;

namespace Envoy.Core.Diagnostics;

/// <summary>
/// The line format of <c>envoy.log</c> and the diagnostics report:
/// <c>2026-10-14T18:03:12.481Z WRN [Translation] Provider failed provider=deepl</c>, then the exception lines indented
/// two spaces. Times are UTC with milliseconds. Line breaks inside the message become <c>\n</c>, so each entry starts on
/// exactly one line.
/// </summary>
public static class LogFormatter
{
    /// <summary>Suffix of a repeat summary (REQ-REL-7). {count} = suppressed repeats; {seconds} = the window.</summary>
    public const string RepeatedFormat = " (repeated {count} times in {seconds} s)";

    public static string LevelCode(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT",
    };

    /// <summary>The entry's first line, without the exception.</summary>
    public static string FormatLine(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sb = new StringBuilder(96);
        sb.Append(entry.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
          .Append(' ').Append(LevelCode(entry.Level))
          .Append(" [").Append(entry.Category).Append("] ")
          .Append(OneLine(entry.Message));
        if (entry.RepeatCount > 0)
        {
            sb.Append(TextTemplate.Format(RepeatedFormat, CultureInfo.InvariantCulture,
                ("count", entry.RepeatCount), ("seconds", (int)RepeatSuppressor.Window.TotalSeconds)));
        }
        if (entry.Fields.Length > 0) sb.Append(' ').Append(OneLine(entry.Fields));
        return sb.ToString();
    }

    /// <summary>The first line plus each exception line indented two spaces, joined with <see cref="Environment.NewLine"/>.</summary>
    public static string Format(LogEntry entry)
    {
        var line = FormatLine(entry);
        if (string.IsNullOrEmpty(entry.Exception)) return line;
        var sb = new StringBuilder(line);
        foreach (var exceptionLine in entry.Exception.Split('\n'))
        {
            var trimmed = exceptionLine.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            sb.Append(Environment.NewLine).Append("  ").Append(trimmed);
        }
        return sb.ToString();
    }

    private static string OneLine(string text) =>
        text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
            : text;
}
