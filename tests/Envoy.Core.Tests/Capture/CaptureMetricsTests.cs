using System.Globalization;
using Envoy.Core.Capture;
using Envoy.Core.Settings;
using Envoy.Core.Tests.Translation.Fakes;
using Xunit;

namespace Envoy.Core.Tests.Capture;

public class CaptureMetricsTests
{
    [Fact]
    public void Averages_use_last_50_samples()
    {
        var m = new CaptureMetrics(TestClock.Create());
        for (var i = 0; i < 50; i++) m.RecordCapture(TimeSpan.FromMilliseconds(100), true);
        for (var i = 0; i < 50; i++) m.RecordCapture(TimeSpan.FromMilliseconds(4), true);
        m.RecordOcr(TimeSpan.FromMilliseconds(60));
        m.RecordOcr(TimeSpan.FromMilliseconds(80));
        var s = m.Snapshot();
        Assert.Equal(4, s.AvgCaptureMs, 6);
        Assert.Equal(70, s.AvgOcrMs, 6);
        Assert.Equal(2, s.OcrRuns);

        var small = new CaptureMetrics(TestClock.Create(), window: 3);
        foreach (var ms in new[] { 10, 20, 30, 40 }) small.RecordCapture(TimeSpan.FromMilliseconds(ms), true);
        Assert.Equal(30, small.Snapshot().AvgCaptureMs, 6);
    }

    [Fact]
    public void Counts_captured_and_skipped_frames()
    {
        var m = new CaptureMetrics(TestClock.Create());
        m.RecordCapture(TimeSpan.Zero, true);
        m.RecordCapture(TimeSpan.Zero, false);
        m.RecordCapture(TimeSpan.Zero, false);
        m.RecordOcrBusy();
        var s = m.Snapshot();
        Assert.Equal(3, s.FramesCaptured);
        Assert.Equal(2, s.FramesSkipped);
        Assert.Equal(1, s.OcrBusySkips);
    }

    [Fact]
    public void Timeout_marks_degraded_for_60s()
    {
        var clock = TestClock.Create();
        var m = new CaptureMetrics(clock);
        m.RecordOcrTimeout();
        Assert.Equal(1, m.Snapshot().OcrTimeouts);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(CaptureHealth.Degraded, m.Snapshot().Health);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(CaptureHealth.Healthy, m.Snapshot().Health);

        m.RecordRestart();
        Assert.Equal(CaptureHealth.Degraded, m.Snapshot().Health);
        Assert.Equal(1, m.Snapshot().Restarts);
    }

    [Fact]
    public void Stuck_ocr_keeps_degraded_and_is_counted()
    {
        var clock = TestClock.Create();
        var m = new CaptureMetrics(clock);
        m.RecordOcrTimeout();
        clock.Advance(TimeSpan.FromSeconds(59));
        m.RecordOcrStuck();
        clock.Advance(TimeSpan.FromSeconds(30));
        var s = m.Snapshot();
        Assert.Equal(CaptureHealth.Degraded, s.Health);
        Assert.Equal(1, s.OcrStuck);
        m.Reset();
        Assert.Equal(0, m.Snapshot().OcrStuck);
    }

    [Fact]
    public void Failing_after_three_consecutive_failures()
    {
        var m = new CaptureMetrics(TestClock.Create());
        m.RecordFailure();
        m.RecordFailure();
        Assert.Equal(CaptureHealth.Healthy, m.Snapshot().Health);
        m.RecordFailure();
        Assert.Equal(CaptureHealth.Failing, m.Snapshot().Health);
        Assert.Equal(3, m.Snapshot().ConsecutiveFailures);
    }

    [Fact]
    public void Success_clears_failing()
    {
        var m = new CaptureMetrics(TestClock.Create());
        for (var i = 0; i < 5; i++) m.RecordFailure();
        m.RecordSuccess();
        Assert.Equal(CaptureHealth.Healthy, m.Snapshot().Health);
        Assert.Equal(0, m.Snapshot().ConsecutiveFailures);
    }

    [Fact]
    public void Paused_when_pause_reason_set()
    {
        var m = new CaptureMetrics(TestClock.Create());
        for (var i = 0; i < 5; i++) m.RecordFailure();
        m.SetPause(PauseReason.GameMinimized);
        var s = m.Snapshot();
        Assert.Equal(CaptureHealth.Paused, s.Health);
        Assert.Equal(PauseReason.GameMinimized, s.PauseReason);
        m.SetPause(PauseReason.None);
        Assert.Equal(CaptureHealth.Failing, m.Snapshot().Health);
    }

    [Fact]
    public void GaveUp_is_failing()
    {
        var m = new CaptureMetrics(TestClock.Create());
        m.SetGaveUp(true);
        Assert.Equal(CaptureHealth.Failing, m.Snapshot().Health);
        m.Reset();
        Assert.Equal(CaptureHealth.Healthy, m.Snapshot().Health);
    }

    [Fact]
    public void StatsLine_formats_expected_text()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var m = new CaptureMetrics(TestClock.Create());
            m.SetBackend("gdi");
            m.SetInterval(TimeSpan.FromMilliseconds(1500));
            m.RecordCapture(TimeSpan.FromMilliseconds(4.4), true);
            for (var i = 0; i < 9; i++) m.RecordCapture(TimeSpan.FromMilliseconds(4.4), false);
            m.RecordOcr(TimeSpan.FromMilliseconds(61.2));
            Assert.Equal("capture 4 ms · OCR 61 ms · skipped 9/10 (90%) · every 1.5 s · gdi", CaptureText.Stats(m.Snapshot()));

            m.SetInterval(TimeSpan.FromMilliseconds(300));
            Assert.Contains("every 0.3 s", CaptureText.Stats(m.Snapshot()), StringComparison.Ordinal);
            Assert.Equal("capture 0 ms · OCR 0 ms · skipped 0/0 (0%) · every 0.0 s · gdi", CaptureText.Stats(CaptureMetricsSnapshot.Empty));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Concurrent_recording_is_consistent()
    {
        var m = new CaptureMetrics(TestClock.Create());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                m.RecordCapture(TimeSpan.FromMilliseconds(5), i % 2 == 0);
                m.RecordOcr(TimeSpan.FromMilliseconds(10));
                m.RecordOcrBusy();
                _ = m.Snapshot();
            }
        })));
        var s = m.Snapshot();
        Assert.Equal(8000, s.FramesCaptured);
        Assert.Equal(4000, s.FramesSkipped);
        Assert.Equal(8000, s.OcrRuns);
        Assert.Equal(8000, s.OcrBusySkips);
        Assert.Equal(5, s.AvgCaptureMs, 6);
        Assert.Equal(10, s.AvgOcrMs, 6);
    }
}

public class CaptureTextTests
{
    [Fact]
    public void Paused_has_text_for_every_reason()
    {
        foreach (var reason in Enum.GetValues<PauseReason>())
            Assert.False(string.IsNullOrWhiteSpace(CaptureText.Paused(reason)), reason.ToString());
        Assert.Equal(CaptureText.NoRegion, CaptureText.Paused(PauseReason.NoRegion));
        Assert.Equal(CaptureText.WaitingForGame, CaptureText.Paused(PauseReason.NoGame));
        Assert.Equal(CaptureText.PausedGameMinimized, CaptureText.Paused(PauseReason.GameMinimized));
    }

    [Fact]
    public void Health_has_text_for_every_state()
    {
        foreach (var health in Enum.GetValues<CaptureHealth>())
            Assert.False(string.IsNullOrWhiteSpace(CaptureText.Health(CaptureMetricsSnapshot.Empty with { Health = health })));
        Assert.Equal(CaptureText.PausedChatCovered,
            CaptureText.Health(CaptureMetricsSnapshot.Empty with { Health = CaptureHealth.Paused, PauseReason = PauseReason.ChatCovered }));
    }

    [Fact]
    public void No_text_starts_with_Error_prefix()
    {
        var texts = typeof(CaptureText).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(texts.Count > 15);
        foreach (var text in texts)
        {
            Assert.False(text.StartsWith("Error", StringComparison.OrdinalIgnoreCase), text);
            Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Region_formats_fill_named_placeholders()
    {
        Assert.Equal("600 × 150 px · follows the Age of Empires II: DE window", CaptureText.RegionFollows(600, 150, "Age of Empires II: DE"));
        Assert.Equal("600 × 200 px at (-1900, 700) · fixed screen position", CaptureText.RegionFixed(new ScreenRegion(-1900, 700, 600, 200)));
    }
}

public class CaptureErrorsTests
{
    [Fact]
    public void Describe_never_returns_exception_text()
    {
        Exception[] errors =
        [
            new TimeoutException("secret-xyz"),
            new CaptureException("secret-xyz"),
            new InvalidOperationException("secret-xyz"),
            new IOException("secret-xyz"),
            new AggregateException("secret-xyz"),
        ];
        foreach (var ex in errors) Assert.DoesNotContain("secret-xyz", CaptureErrors.Describe(ex), StringComparison.Ordinal);
        Assert.Equal(CaptureText.OcrTimedOut, CaptureErrors.Describe(errors[0]));
        Assert.Equal(CaptureText.ScreenUnreadable, CaptureErrors.Describe(errors[1]));
        Assert.Equal(CaptureText.CaptureFailed, CaptureErrors.Describe(errors[2]));
    }
}

public class SoakBudgetTests
{
    private static SoakSample Sample(int minute, long bytes = 100_000_000, int handles = 500) =>
        new(minute, bytes, handles, 40, 30, 0, 2_000_000, 100, 6);

    [Fact]
    public void Growth_over_20_percent_is_reported()
    {
        var samples = Enumerable.Range(1, 10).Select(m => Sample(m, bytes: m < 10 ? 100_000_000 : 125_000_000)).ToList();
        var violation = Assert.Single(SoakBudget.Violations(samples));
        Assert.Contains("PrivateBytes", violation, StringComparison.Ordinal);
        Assert.Contains("25%", violation, StringComparison.Ordinal);

        var fine = Enumerable.Range(1, 10).Select(m => Sample(m, bytes: m < 10 ? 100_000_000 : 119_000_000)).ToList();
        Assert.Empty(SoakBudget.Violations(fine));
    }

    [Fact]
    public void Minute_5_is_the_baseline()
    {
        // Big start-up growth before minute 5 is fine; only minute 5 → end counts.
        var samples = new List<SoakSample> { Sample(1, handles: 100), Sample(4, handles: 300), Sample(5, handles: 500), Sample(9, handles: 590) };
        Assert.Empty(SoakBudget.Violations(samples));
        samples.Add(Sample(10, handles: 700));
        Assert.Contains(SoakBudget.Violations(samples), v => v.Contains("Handles", StringComparison.Ordinal));
    }

    [Fact]
    public void Short_runs_report_nothing()
    {
        Assert.Empty(SoakBudget.Violations([]));
        Assert.Empty(SoakBudget.Violations([Sample(1), Sample(2, bytes: 900_000_000)]));
        Assert.Empty(SoakBudget.Violations([Sample(5), Sample(5, bytes: 900_000_000)]));
        Assert.Empty(SoakBudget.Violations([Sample(5) with { PoolOutstanding = 0 }, Sample(6) with { PoolOutstanding = 1 }]));
    }
}
