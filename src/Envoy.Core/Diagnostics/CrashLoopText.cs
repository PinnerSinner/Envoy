namespace Envoy.Core.Diagnostics;

/// <summary>The one modal error message Envoy may show: the crash loop (REQ-REL-15, contract §2.2).</summary>
public static class CrashLoopText
{
    public const string Title = "Envoy is closing";

    public const string Message =
        "Envoy ran into the same problem several times in a row, so it is closing. Start it again while holding Shift to use safe mode, then copy diagnostics from About › Troubleshooting into a bug report.";
}
