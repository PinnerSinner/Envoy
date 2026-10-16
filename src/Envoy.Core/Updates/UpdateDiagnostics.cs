using System.Globalization;

namespace Envoy.Core.Updates;

/// <summary>The diagnostics report's "Updates" line (REQ-DIST-31). Kinds and states only; never a URL or notes.</summary>
public static class UpdateDiagnostics
{
    /// <summary>"Installed, channel Beta, state UpToDate, version 1.2.0, last check 2026-10-14 12:00 UTC, last error kind none".</summary>
    public static string Line(UpdateSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var inv = CultureInfo.InvariantCulture;
        var last = s.LastChecked is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", inv) : "never";
        var offered = s.Available is { } a ? ", offered " + a.Version : "";
        return string.Create(inv,
            $"{s.Kind}, channel {s.Channel}, state {s.State}{offered}, last check {last}, last error kind {s.Error?.ToString() ?? "none"}");
    }
}
