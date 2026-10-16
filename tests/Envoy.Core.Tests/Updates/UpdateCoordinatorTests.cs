using Envoy.Core.Diagnostics;
using Envoy.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests.Updates;

/// <summary>A scripted backend: queued check results (or exceptions), counters and gates; never touches the network.</summary>
internal sealed class FakeUpdateBackend(InstallKind kind, string current = "1.0.0") : IUpdateBackend
{
    private readonly Queue<Func<UpdateChannel, CancellationToken, Task<AvailableUpdate?>>> _checks = new();
    private Func<UpdateChannel, CancellationToken, Task<AvailableUpdate?>> _last = (_, _) => Task.FromResult<AvailableUpdate?>(null);
    private int _checkCalls;
    private int _downloadCalls;

    public InstallKind Kind { get; } = kind;
    public ReleaseVersion CurrentVersion { get; } = ReleaseVersion.Parse(current);
    public ReleaseVersion? PendingRestart { get; set; }
    public int CheckCalls => Volatile.Read(ref _checkCalls);
    public int DownloadCalls => Volatile.Read(ref _downloadCalls);
    public List<UpdateChannel> Channels { get; } = [];
    public List<IReadOnlyList<string>> Launches { get; } = [];

    /// <summary>When set, a check waits for it (and honours the token).</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    /// <summary>When set, a download reports 0 → <see cref="GatePercent"/>, waits for it, then reports 100.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }
    public int GatePercent { get; set; } = 50;
    public IReadOnlyList<int> ProgressSteps { get; set; } = [0, 100];
    public Exception? DownloadError { get; set; }
    public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeUpdateBackend Returns(string? version)
    {
        lock (_checks) _checks.Enqueue((_, _) => Task.FromResult(version is null ? null : Update(version)));
        return this;
    }

    public FakeUpdateBackend Fails(Exception ex)
    {
        lock (_checks) _checks.Enqueue((_, _) => Task.FromException<AvailableUpdate?>(ex));
        return this;
    }

    public static AvailableUpdate Update(string version)
    {
        var v = ReleaseVersion.Parse(version);
        return new AvailableUpdate(v, "Notes", UpdateEndpoints.ReleasePage(v), null, null, Handle: "handle");
    }

    public async Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        Interlocked.Increment(ref _checkCalls);
        lock (Channels) Channels.Add(channel);
        if (CheckGate is { } gate) await gate.Task.WaitAsync(ct);
        Func<UpdateChannel, CancellationToken, Task<AvailableUpdate?>> next;
        lock (_checks) next = _checks.Count > 0 ? (_last = _checks.Dequeue()) : _last; // the last result repeats
        return await next(channel, ct);
    }

    public async Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _downloadCalls);
        DownloadStarted.TrySetResult();
        if (DownloadGate is { } gate)
        {
            progress.Report(0);
            progress.Report(GatePercent);
            await gate.Task.WaitAsync(ct);
        }
        if (DownloadError is { } error) throw error;
        foreach (var p in ProgressSteps) progress.Report(p);
    }

    public void LaunchUpdaterAfterExit(IReadOnlyList<string> restartArgs)
    {
        lock (Launches) Launches.Add(restartArgs);
    }
}

public class UpdateCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<DateTimeOffset> _lastChecks = [];
    private readonly List<UpdateState> _states = [];

    private static UpdatePreferences Prefs(bool auto = true, bool autoDownload = true, UpdateChannel channel = UpdateChannel.Stable,
                                           string? skipped = null, DateTimeOffset? lastCheck = null) =>
        new(auto, autoDownload, channel, skipped is null ? null : ReleaseVersion.Parse(skipped), lastCheck);

    private UpdateCoordinator Create(FakeUpdateBackend backend, UpdatePreferences? prefs = null)
    {
        var c = new UpdateCoordinator(backend, prefs ?? Prefs(), t =>
        {
            lock (_lastChecks) _lastChecks.Add(t);
        }, _time, NullLog.Instance);
        c.Changed += () =>
        {
            lock (_states) _states.Add(c.Snapshot.State);
        };
        return c;
    }

    [Fact]
    public async Task Automatic_check_disabled_makes_no_backend_call()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("2.0.0");
        var c = Create(backend, Prefs(auto: false));
        await c.StartAsync();
        await c.TickAsync();
        await c.CheckAsync(manual: false);
        Assert.Equal(0, backend.CheckCalls);
        Assert.Equal(UpdateState.Idle, c.Snapshot.State);
    }

    [Fact]
    public async Task Manual_check_runs_even_when_disabled()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Returns("2.0.0");
        var c = Create(backend, Prefs(auto: false));
        await c.CheckAsync(manual: true);
        Assert.Equal(1, backend.CheckCalls);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
        Assert.True(c.Snapshot.LastCheckWasManual);
    }

    [Theory]
    [InlineData(-30, 0)]
    [InlineData(-120, 1)]
    public async Task Startup_skips_check_when_checked_within_an_hour(int minutesAgo, int calls)
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable);
        var c = Create(backend, Prefs(lastCheck: Now.AddMinutes(minutesAgo)));
        await c.StartAsync();
        Assert.Equal(calls, backend.CheckCalls);
        var never = new FakeUpdateBackend(InstallKind.Portable);
        await Create(never, Prefs(lastCheck: null)).StartAsync();
        Assert.Equal(1, never.CheckCalls);
    }

    [Fact]
    public async Task Startup_with_pending_package_goes_ready_without_network()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed) { PendingRestart = ReleaseVersion.Parse("1.1.0") };
        var c = Create(backend);
        await c.StartAsync();
        Assert.Equal(0, backend.CheckCalls);
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
        Assert.Equal("1.1.0", c.Snapshot.Available!.Version.ToString());
        Assert.True(c.CanRestartToUpdate);
    }

    [Fact]
    public async Task Installed_autodownload_reaches_ReadyToRestart()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend);
        await c.CheckAsync(manual: false);
        await c.BackgroundDownload;
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
        Assert.Equal(1, backend.DownloadCalls);
        Assert.Equal(100, c.Snapshot.Percent);
        lock (_states)
        {
            Assert.Equal(UpdateState.Checking, _states[0]);
            Assert.Contains(UpdateState.Available, _states);
            Assert.Contains(UpdateState.Downloading, _states);
            Assert.Equal(UpdateState.ReadyToRestart, _states[^1]);
        }
    }

    [Fact]
    public async Task Autodownload_off_stops_at_Available()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend, Prefs(autoDownload: false));
        await c.CheckAsync(manual: false);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
        Assert.Equal(0, backend.DownloadCalls);
        await c.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
    }

    [Fact]
    public async Task Download_deferred_while_game_running_then_resumes_on_tick()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend);
        c.SetGameRunning(true);
        await c.CheckAsync(manual: false);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
        Assert.True(c.Snapshot.DeferredForGame);
        Assert.Equal(0, backend.DownloadCalls);

        await c.TickAsync(); // still in game
        Assert.Equal(0, backend.DownloadCalls);

        c.SetGameRunning(false);
        Assert.Equal(0, backend.DownloadCalls); // the transition itself does not download
        await c.TickAsync();
        Assert.Equal(1, backend.DownloadCalls);
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
        Assert.False(c.Snapshot.DeferredForGame);
        Assert.Equal(1, backend.CheckCalls);
    }

    [Fact]
    public async Task Game_starting_mid_download_does_not_cancel()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed) { DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) }.Returns("1.1.0");
        var c = Create(backend);
        var run = c.CheckAsync(manual: false);
        await backend.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        c.SetGameRunning(true);
        backend.DownloadGate!.SetResult();
        await run;
        await c.BackgroundDownload;
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
    }

    [Fact]
    public async Task Portable_never_downloads()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Returns("1.1.0");
        var c = Create(backend);
        await c.CheckAsync(manual: false);
        await c.DownloadAsync();
        await c.TickAsync();
        Assert.Equal(0, backend.DownloadCalls);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
        Assert.False(c.CanRestartToUpdate);
    }

    [Fact]
    public async Task Skipped_version_hidden_for_automatic_check()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Returns("1.1.0");
        var c = Create(backend, Prefs(skipped: "1.1.0"));
        await c.CheckAsync(manual: false);
        Assert.Equal(UpdateState.Idle, c.Snapshot.State);
        Assert.Null(c.Snapshot.Available);
    }

    [Fact]
    public async Task Skipped_version_shown_for_manual_check()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Returns("1.1.0");
        var c = Create(backend, Prefs(skipped: "1.1.0"));
        await c.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);

        var newer = new FakeUpdateBackend(InstallKind.Portable).Returns("1.2.0");
        var d = Create(newer, Prefs(skipped: "1.1.0"));
        await d.CheckAsync(manual: false);
        Assert.Equal(UpdateState.Available, d.Snapshot.State); // a newer version than the skipped one is offered
    }

    [Fact]
    public async Task SkipAvailableVersion_returns_version_and_goes_idle()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Returns("1.1.0");
        var c = Create(backend);
        Assert.Null(c.SkipAvailableVersion()); // nothing on offer yet
        await c.CheckAsync(manual: false);
        Assert.Equal("1.1.0", c.SkipAvailableVersion()?.ToString());
        Assert.Equal(UpdateState.Idle, c.Snapshot.State);
        Assert.Equal("1.1.0", c.Preferences.SkippedVersion?.ToString());
        _time.Advance(TimeSpan.FromHours(13));
        await c.TickAsync(); // the next automatic check hides it
        Assert.Equal(UpdateState.Idle, c.Snapshot.State);
    }

    [Fact]
    public async Task Automatic_failure_is_silent()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Fails(new UpdateCheckException(UpdateErrorKind.Offline));
        var c = Create(backend);
        await c.CheckAsync(manual: false);
        Assert.Equal(UpdateState.Idle, c.Snapshot.State);
        Assert.Equal(UpdateErrorKind.Offline, c.Snapshot.Error);
        Assert.False(UpdateViewBuilder.Build(c.Snapshot).ShowBanner);
        Assert.DoesNotContain(UpdateState.Failed, _states);
    }

    [Fact]
    public async Task Manual_failure_sets_Failed_with_kind()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Fails(new UpdateCheckException(UpdateErrorKind.RateLimited));
        var c = Create(backend);
        await c.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, c.Snapshot.State);
        Assert.Equal(UpdateErrorKind.RateLimited, c.Snapshot.Error);
        Assert.False(c.Snapshot.FailedWhileDownloading);

        var other = new FakeUpdateBackend(InstallKind.Portable).Fails(new InvalidOperationException("boom"));
        var d = Create(other);
        await d.CheckAsync(manual: true);
        Assert.Equal(UpdateErrorKind.Unknown, d.Snapshot.Error);
    }

    [Fact]
    public async Task Download_failure_keeps_available_and_retry_succeeds()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed) { DownloadError = new UpdateCheckException(UpdateErrorKind.DownloadFailed) }.Returns("1.1.0");
        var c = Create(backend, Prefs(autoDownload: false));
        await c.CheckAsync(manual: true);
        await c.DownloadAsync();
        Assert.Equal(UpdateState.Failed, c.Snapshot.State);
        Assert.True(c.Snapshot.FailedWhileDownloading);
        Assert.Equal(UpdateErrorKind.DownloadFailed, c.Snapshot.Error);
        Assert.Equal("1.1.0", c.Snapshot.Available?.Version.ToString());

        backend.DownloadError = null;
        await c.DownloadAsync(); // Retry
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
        Assert.Equal(2, backend.DownloadCalls);
    }

    [Fact]
    public async Task Background_download_failure_is_silent()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed) { DownloadError = new UpdateCheckException(UpdateErrorKind.Offline) }.Returns("1.1.0");
        var c = Create(backend);
        await c.CheckAsync(manual: false);
        await c.BackgroundDownload;
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
        Assert.Equal(UpdateErrorKind.Offline, c.Snapshot.Error);
        Assert.DoesNotContain(UpdateState.Failed, _states);
    }

    [Fact]
    public async Task Cancellation_restores_previous_state()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable) { CheckGate = new() }.Returns("1.1.0");
        var c = Create(backend);
        using var cts = new CancellationTokenSource();
        var run = c.CheckAsync(manual: true, cts.Token);
        while (c.Snapshot.State != UpdateState.Checking) await Task.Delay(5);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(UpdateState.Idle, c.Snapshot.State);
        Assert.Null(c.Snapshot.Error);
        Assert.Empty(_lastChecks);
    }

    [Fact]
    public async Task Concurrent_checks_share_one_backend_call()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable) { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) }.Returns("1.1.0");
        var c = Create(backend);
        var a = c.CheckAsync(manual: true);
        var b = c.CheckAsync(manual: false); // an automatic call shares a manual run
        var d = c.CheckAsync(manual: true);
        Assert.Same(a, b);
        Assert.Same(a, d);
        backend.CheckGate!.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(1, backend.CheckCalls);
        await c.CheckAsync(manual: true); // a later call runs again
        Assert.Equal(2, backend.CheckCalls);
    }

    [Fact]
    public async Task Manual_check_during_automatic_check_runs_again_and_shows_failure()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable) { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) }
            .Fails(new UpdateCheckException(UpdateErrorKind.Offline));
        var c = Create(backend);
        var automatic = c.CheckAsync(manual: false);
        Assert.Same(automatic, c.CheckAsync(manual: false));
        var manual = c.CheckAsync(manual: true);
        Assert.NotSame(automatic, manual);
        Assert.Same(manual, c.CheckAsync(manual: true)); // the queued manual check is shared
        backend.CheckGate!.SetResult();
        await Task.WhenAll(automatic, manual);
        Assert.Equal(2, backend.CheckCalls);
        Assert.Equal(UpdateState.Failed, c.Snapshot.State);
        Assert.Equal(UpdateErrorKind.Offline, c.Snapshot.Error);
        Assert.True(c.Snapshot.LastCheckWasManual);
    }

    [Fact]
    public async Task Channel_change_during_a_check_checks_the_new_channel()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable) { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) }
            .Returns("1.1.0-beta.1");
        var c = Create(backend);
        var first = c.CheckAsync(manual: true);
        while (backend.CheckCalls == 0) await Task.Delay(5);
        var second = c.Configure(Prefs(channel: UpdateChannel.Beta));
        Assert.NotSame(first, second);
        backend.CheckGate!.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal([UpdateChannel.Stable, UpdateChannel.Beta], backend.Channels);
        Assert.Equal(UpdateChannel.Beta, c.Snapshot.Channel);
    }

    [Fact]
    public async Task Check_task_ends_before_the_background_download()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed) { DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) }.Returns("1.1.0");
        var c = Create(backend);
        await c.CheckAsync(manual: true).WaitAsync(TimeSpan.FromSeconds(10)); // a command awaiting it is not busy for the download
        await backend.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UpdateState.Downloading, c.Snapshot.State);
        backend.DownloadGate!.SetResult();
        await c.BackgroundDownload;
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
    }

    [Fact]
    public async Task Deferred_download_needs_automatic_checks_on()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend);
        c.SetGameRunning(true);
        await c.CheckAsync(manual: false);
        Assert.True(c.Snapshot.DeferredForGame);

        await c.Configure(Prefs(auto: false)); // the master switch goes off while the download waits
        Assert.False(c.Snapshot.DeferredForGame); // the banner offers Update now instead
        c.SetGameRunning(false);
        await c.TickAsync();
        Assert.Equal(0, backend.DownloadCalls);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
    }

    [Fact]
    public async Task Manual_check_in_game_with_automatic_checks_off_does_not_defer()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend, Prefs(auto: false));
        c.SetGameRunning(true);
        await c.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
        Assert.False(c.Snapshot.DeferredForGame);
        c.SetGameRunning(false);
        await c.TickAsync();
        Assert.Equal(0, backend.DownloadCalls);
    }

    [Fact]
    public async Task Manual_check_with_automatic_checks_off_does_not_download()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend, Prefs(auto: false, autoDownload: true)); // also safe mode (UpdatePreferences.From)
        await c.CheckAsync(manual: true);
        await c.BackgroundDownload;
        Assert.Equal(0, backend.DownloadCalls);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
        Assert.Equal(UpdateAction.UpdateNow, UpdateViewBuilder.Build(c.Snapshot).Primary);

        await c.DownloadAsync(); // Update now still works
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
    }

    [Fact]
    public async Task Manual_failure_while_ready_keeps_restart_offer()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend);
        await c.CheckAsync(manual: true);
        await c.BackgroundDownload;
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
        Assert.Equal(1, backend.DownloadCalls);

        backend.Fails(new UpdateCheckException(UpdateErrorKind.Offline)).Returns("1.1.0");
        await c.CheckAsync(manual: true);
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
        Assert.True(c.CanRestartToUpdate);
        Assert.Equal(UpdateErrorKind.Offline, c.Snapshot.Error);
        Assert.Equal(ReleaseVersion.Parse("1.1.0"), c.Snapshot.Available?.Version);
        Assert.Equal(UpdateAction.RestartToUpdate, UpdateViewBuilder.Build(c.Snapshot).Primary);

        await c.CheckAsync(manual: true); // the next check finds the same version: still ready, no second download
        await c.BackgroundDownload;
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
        Assert.Null(c.Snapshot.Error);
        Assert.Equal(1, backend.DownloadCalls);
    }

    [Fact]
    public async Task Configure_check_honours_the_token()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable) { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var c = Create(backend);
        using var cts = new CancellationTokenSource();
        var check = c.Configure(Prefs(channel: UpdateChannel.Beta), cts.Token);
        while (backend.CheckCalls == 0) await Task.Delay(5);
        await cts.CancelAsync(); // AppHost.Dispose cancels UpdatesToken
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(UpdateState.Idle, c.Snapshot.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Automatic_recheck_keeps_the_offer_visible(bool ready)
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend, Prefs(autoDownload: ready));
        await c.CheckAsync(manual: false);
        await c.BackgroundDownload;
        var offered = ready ? UpdateState.ReadyToRestart : UpdateState.Available;
        Assert.Equal(offered, c.Snapshot.State);

        backend.CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Fails(new UpdateCheckException(UpdateErrorKind.Offline)).Returns("1.1.0");
        lock (_states) _states.Clear();
        _time.Advance(TimeSpan.FromHours(13));
        var tick = c.TickAsync();
        while (backend.CheckCalls < 2) await Task.Delay(5);
        Assert.Equal(offered, c.Snapshot.State); // the banner and tray item stay during the check
        Assert.Equal(ready, c.CanRestartToUpdate);
        backend.CheckGate.SetResult();
        await tick;
        Assert.Equal(offered, c.Snapshot.State); // a silent failure keeps the offer
        Assert.Equal(UpdateErrorKind.Offline, c.Snapshot.Error);

        _time.Advance(TimeSpan.FromHours(13));
        await c.TickAsync(); // a successful re-check of the same version keeps it too
        Assert.Equal(offered, c.Snapshot.State);
        lock (_states) Assert.All(_states, s => Assert.Equal(offered, s));
        Assert.Equal(ready ? 1 : 0, backend.DownloadCalls);
    }

    [Theory]
    [InlineData(11, 0)]
    [InlineData(12, 1)]
    public async Task Periodic_tick_checks_after_12_hours(int hours, int calls)
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable);
        var c = Create(backend, Prefs(lastCheck: Now));
        _time.Advance(TimeSpan.FromHours(hours));
        await c.TickAsync();
        Assert.Equal(calls, backend.CheckCalls);
    }

    [Fact]
    public async Task LastCheck_callback_after_success_and_failure()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Returns(null).Fails(new UpdateCheckException(UpdateErrorKind.Offline));
        var c = Create(backend);
        await c.CheckAsync(manual: true);
        _time.Advance(TimeSpan.FromMinutes(5));
        await c.CheckAsync(manual: false);
        Assert.Equal([Now, Now.AddMinutes(5)], _lastChecks);
        Assert.Equal(Now.AddMinutes(5), c.Snapshot.LastChecked);
        Assert.Equal(Now.AddMinutes(5), c.Preferences.LastCheckUtc);
    }

    [Fact]
    public async Task Configure_keeps_later_LastCheckUtc()
    {
        var c = Create(new FakeUpdateBackend(InstallKind.Portable), Prefs(lastCheck: Now));
        await c.Configure(Prefs(lastCheck: Now.AddHours(-3)));
        Assert.Equal(Now, c.Preferences.LastCheckUtc);
        await c.Configure(Prefs(lastCheck: Now.AddHours(1)));
        Assert.Equal(Now.AddHours(1), c.Preferences.LastCheckUtc);
        await c.Configure(Prefs(lastCheck: null));
        Assert.Equal(Now.AddHours(1), c.Preferences.LastCheckUtc);
    }

    [Fact]
    public async Task Configure_channel_change_triggers_one_manual_check()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable).Returns("1.1.0-beta.1");
        var c = Create(backend, Prefs(auto: false));
        await c.Configure(Prefs(auto: false, channel: UpdateChannel.Beta));
        Assert.Equal(1, backend.CheckCalls);
        Assert.Equal(UpdateChannel.Beta, c.Snapshot.Channel);
        Assert.Equal(UpdateState.Available, c.Snapshot.State);
        await c.Configure(Prefs(auto: false, channel: UpdateChannel.Beta, autoDownload: false));
        Assert.Equal(1, backend.CheckCalls);
    }

    [Fact]
    public async Task First_configure_does_not_check()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable);
        var c = Create(backend, Prefs(channel: UpdateChannel.Beta));
        await c.Configure(Prefs(channel: UpdateChannel.Beta, autoDownload: false));
        Assert.Equal(0, backend.CheckCalls);
    }

    [Fact]
    public async Task Channel_is_passed_to_backend()
    {
        var backend = new FakeUpdateBackend(InstallKind.Portable);
        var c = Create(backend, Prefs(channel: UpdateChannel.Beta));
        await c.CheckAsync(manual: true);
        await c.Configure(Prefs(channel: UpdateChannel.Stable));
        Assert.Equal([UpdateChannel.Beta, UpdateChannel.Stable], backend.Channels);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    [InlineData("1.0.0-rc.1")]
    public async Task Older_or_equal_result_is_up_to_date(string version)
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns(version);
        var c = Create(backend);
        await c.CheckAsync(manual: true);
        Assert.Equal(UpdateState.UpToDate, c.Snapshot.State);
        Assert.Null(c.Snapshot.Available);
        Assert.Equal(0, backend.DownloadCalls);
    }

    [Fact]
    public async Task Check_while_ready_keeps_ready_for_same_version()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed) { PendingRestart = ReleaseVersion.Parse("1.1.0") }.Returns("1.1.0");
        var c = Create(backend);
        await c.StartAsync();
        await c.CheckAsync(manual: true);
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
        Assert.Equal(0, backend.DownloadCalls);

        backend.Returns(null); // feed went away (or nothing newer): still ready
        await c.CheckAsync(manual: true);
        Assert.Equal(UpdateState.ReadyToRestart, c.Snapshot.State);
    }

    [Fact]
    public async Task RestartToUpdate_requires_ready_and_installed()
    {
        var portable = Create(new FakeUpdateBackend(InstallKind.Portable).Returns("1.1.0"));
        await portable.CheckAsync(manual: true);
        Assert.Throws<InvalidOperationException>(() => portable.RestartToUpdate([]));

        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend, Prefs(autoDownload: false));
        await c.CheckAsync(manual: true);
        Assert.Throws<InvalidOperationException>(() => c.RestartToUpdate([]));
        await c.DownloadAsync();
        c.RestartToUpdate(["--minimized"]);
        Assert.Equal(["--minimized"], Assert.Single(backend.Launches));
    }

    [Fact]
    public async Task RestartToUpdate_works_after_token_cancelled()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed).Returns("1.1.0");
        var c = Create(backend);
        using var cts = new CancellationTokenSource();
        await c.CheckAsync(manual: false, cts.Token);
        await c.BackgroundDownload;
        await cts.CancelAsync(); // AppHost.Dispose cancels the update token before ExitApp step 8
        c.RestartToUpdate([]);
        Assert.Single(backend.Launches);
    }

    [Fact]
    public async Task Progress_raises_changed_only_on_whole_percent_change()
    {
        var backend = new FakeUpdateBackend(InstallKind.Installed) { ProgressSteps = [0, 0, 10, 10, 10, 55, 55, 100, 100] }.Returns("1.1.0");
        var c = Create(backend, Prefs(autoDownload: false));
        await c.CheckAsync(manual: true);
        var percents = new List<int>();
        c.Changed += () =>
        {
            lock (percents) percents.Add(c.Snapshot.Percent);
        };
        await c.DownloadAsync();
        // Downloading at 0, then 10, 55, 100, then ReadyToRestart (still 100).
        Assert.Equal([0, 10, 55, 100, 100], percents);
    }

    [Fact]
    public void Diagnostics_line_has_kinds_only()
    {
        var c = Create(new FakeUpdateBackend(InstallKind.Installed));
        var line = UpdateDiagnostics.Line(c.Snapshot with { Error = UpdateErrorKind.RateLimited, LastChecked = Now });
        Assert.Equal("Installed, channel Stable, state Idle, last check 2026-10-14 12:00 UTC, last error kind RateLimited", line);
    }
}
