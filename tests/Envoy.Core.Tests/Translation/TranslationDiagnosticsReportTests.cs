using Envoy.Core.Diagnostics;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class TranslationDiagnosticsReportTests
{
    [Fact]
    public void Report_lists_health_for_active_chain_without_error_text()
    {
        var snapshot = new DiagnosticsSnapshot
        {
            AppVersion = "1.0.0",
            Runtime = ".NET",
            OsDescription = "Windows",
            Architecture = "X64",
            TranslationHealth =
            [
                ProviderHealthSnapshot.Empty("google-free") with
                {
                    State = ProviderState.CoolingDown, MedianLatency = TimeSpan.FromMilliseconds(340), Successes = 12, Failures = 3,
                    LastErrorKind = TranslationErrorKind.Transient, LastErrorStatus = 503, LastError = "UNIQUE-ERROR-TEXT",
                },
                ProviderHealthSnapshot.Empty("edge-free"),
            ],
        };
        var text = DiagnosticsReport.Render(snapshot, new AppSettings(), []);
        Assert.Contains("- google-free: CoolingDown, median 340 ms, ok 12, failed 3, last error kind Transient (503)", text, StringComparison.Ordinal);
        Assert.Contains("- edge-free: Unknown, median - ms, ok 0, failed 0, last error kind none (-)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUE-ERROR-TEXT", text, StringComparison.Ordinal);
        var translation = text.IndexOf("### Translation", StringComparison.Ordinal);
        var hotkeys = text.IndexOf("### Hotkeys", StringComparison.Ordinal);
        Assert.InRange(text.IndexOf("- google-free:", StringComparison.Ordinal), translation, hotkeys);
    }
}
