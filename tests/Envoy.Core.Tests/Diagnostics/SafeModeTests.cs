using Envoy.Core.Diagnostics;
using Xunit;

namespace Envoy.Core.Tests.Diagnostics;

public sealed class SafeModeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "envoy-safe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Theory]
    [InlineData(new[] { "--safe-mode" }, false, 0, 0, SafeModeReason.CommandLine)]
    [InlineData(new[] { "--minimized", "--SAFE-MODE" }, true, 5, 2, SafeModeReason.CommandLine)]
    [InlineData(new string[0], true, 5, 2, SafeModeReason.ShiftHeld)]
    [InlineData(new[] { "--minimized" }, false, 2, 2, SafeModeReason.RepeatedStartupFailures)]
    [InlineData(new[] { "--minimized" }, false, 1, 2, null)]
    [InlineData(new string[0], false, 10, 0, null)] // threshold 0 disables the automatic switch
    [InlineData(new[] { "--safe" }, false, 0, 0, null)]
    public void Safe_mode_decision(string[] args, bool shift, int unfinished, int threshold, SafeModeReason? expected) =>
        Assert.Equal(expected, SafeModeDecision.Decide(args, shift, unfinished, threshold));

    [Theory]
    // safe mode: no overlay, hotkeys or auto capture; MainWindow always shown, even minimized
    [InlineData(true, true, true, true, false, false, false, true)]
    [InlineData(true, false, false, false, false, false, false, true)]
    // normal: today's rule
    [InlineData(false, false, true, true, true, true, true, true)]
    [InlineData(false, true, true, true, true, true, true, false)]
    [InlineData(false, true, false, false, true, true, false, true)] // first run always shows the window
    public void Startup_plan(bool safe, bool minimized, bool firstRunCompleted, bool autoCapture,
                             bool overlay, bool hotkeys, bool capture, bool showMain)
    {
        var plan = StartupPlan.For(safe ? SafeModeReason.CommandLine : null, minimized, firstRunCompleted, autoCapture);
        Assert.Equal(new StartupPlan(overlay, hotkeys, capture, showMain, safe), plan);
    }

    [Fact]
    public void Startup_sentinel_counts_unfinished_starts()
    {
        var path = Path.Combine(_folder, "session.json");
        var sentinel = new StartupSentinel(path);

        Assert.Equal(new StartupHistory(0, false), sentinel.BeginStartup());
        Assert.Equal(new StartupHistory(1, true), sentinel.BeginStartup()); // never became healthy
        Assert.Equal(new StartupHistory(2, true), sentinel.BeginStartup());
        Assert.Equal(SafeModeReason.RepeatedStartupFailures, SafeModeDecision.Decide([], false, 2, SafeModeDecision.AutoThreshold));

        sentinel.MarkHealthy();
        Assert.Equal(new StartupHistory(0, true), sentinel.BeginStartup()); // healthy, but never exited cleanly
        sentinel.MarkHealthy();
        sentinel.EndSession();
        Assert.False(File.Exists(path));
        Assert.Equal(new StartupHistory(0, false), sentinel.BeginStartup());
    }

    [Fact]
    public void Sentinel_reports_unclean_end()
    {
        var path = Path.Combine(_folder, "session.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{ corrupt");
        var sentinel = new StartupSentinel(path);
        Assert.Equal(new StartupHistory(0, true), sentinel.BeginStartup());

        // An unwritable location never throws.
        var blocked = new StartupSentinel(Path.Combine(_folder, "session.json", "nested.json"));
        blocked.BeginStartup();
        blocked.MarkHealthy();
        blocked.EndSession();
    }
}
