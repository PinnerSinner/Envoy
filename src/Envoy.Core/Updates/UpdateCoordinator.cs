using System.Diagnostics.CodeAnalysis;
using Envoy.Core.Diagnostics;

namespace Envoy.Core.Updates;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed,
}

/// <summary>Everything the update UI shows, as one immutable value.</summary>
/// <param name="Percent">Download progress, 0–100.</param>
/// <param name="DeferredForGame">An automatic download is waiting for the game to close.</param>
/// <param name="Error">The last failure's kind; kept after a silent automatic failure for diagnostics only.</param>
/// <param name="FailedWhileDownloading">The Failed state came from a download (Retry downloads again).</param>
public sealed record UpdateSnapshot(
    InstallKind Kind, ReleaseVersion Current, UpdateState State, AvailableUpdate? Available,
    int Percent, bool DeferredForGame, UpdateErrorKind? Error, bool FailedWhileDownloading,
    bool LastCheckWasManual, DateTimeOffset? LastChecked, UpdateChannel Channel);

/// <summary>
/// All update policy and state (contract §2.5, spec 06): when to check, whether to download, what to show. UI-agnostic
/// and thread-safe: <see cref="Snapshot"/> is replaced atomically, one semaphore serialises checks and downloads, and
/// preferences arrive only as immutable <see cref="UpdatePreferences"/>.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim never creates a wait handle (AvailableWaitHandle is not used), and a check or download still finishing after the host is disposed must be able to release it.")]
public sealed class UpdateCoordinator
{
    public static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(12);
    public static readonly TimeSpan StartupMinInterval = TimeSpan.FromHours(1);

    private readonly IUpdateBackend _backend;
    private readonly Action<DateTimeOffset> _lastCheckChanged;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _runLock = new();
    private UpdatePreferences _prefs;
    private UpdateSnapshot _snapshot;
    private Task? _checkRun;
    private bool _checkRunManual;
    private UpdateChannel _checkRunChannel;
    private Task? _backgroundDownload;
    private volatile bool _gameRunning;

    /// <param name="lastCheckChanged">
    /// Called (any thread) after every completed check; AppHost marshals it to the UI thread and writes
    /// <c>Settings.Updates.LastCheckUtc</c>.
    /// </param>
    public UpdateCoordinator(IUpdateBackend backend, UpdatePreferences initial, Action<DateTimeOffset> lastCheckChanged,
                             TimeProvider time, ILog log)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(initial);
        _backend = backend;
        _prefs = initial;
        _lastCheckChanged = lastCheckChanged ?? throw new ArgumentNullException(nameof(lastCheckChanged));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _snapshot = new UpdateSnapshot(backend.Kind, backend.CurrentVersion, UpdateState.Idle, null, 0, false, null, false,
            false, initial.LastCheckUtc, initial.Channel);
    }

    /// <summary>The current state; immutable, read without a lock.</summary>
    public UpdateSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>The preferences in use.</summary>
    public UpdatePreferences Preferences => Volatile.Read(ref _prefs);

    /// <summary>Raised on a background thread after every state change. UI consumers use AppHost's marshalled notification.</summary>
    public event Action? Changed;

    public bool CanRestartToUpdate => Snapshot is { State: UpdateState.ReadyToRestart, Kind: InstallKind.Installed };

    /// <summary>
    /// Publishes new preferences. <c>LastCheckUtc</c> keeps the later of the stored and the incoming value. Returns a
    /// manual check when the channel changed (REQ-DIST-18), else a completed task.
    /// </summary>
    public Task Configure(UpdatePreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        UpdatePreferences old;
        lock (_stateLock)
        {
            old = _prefs;
            var merged = prefs with { LastCheckUtc = Later(old.LastCheckUtc, prefs.LastCheckUtc) };
            Volatile.Write(ref _prefs, merged);
        }
        // A download waiting for the game is automatic: it no longer starts once either switch is off (REQ-DIST-13, 16).
        if (!prefs.CheckAutomatically || !prefs.AutoDownload)
            Update(s => s.DeferredForGame ? s with { DeferredForGame = false } : s);
        if (old.Channel == prefs.Channel) return Task.CompletedTask;
        Update(s => s with { Channel = prefs.Channel });
        _log.Info("Update channel changed", LogField.Of("channel", prefs.Channel));
        return CheckAsync(manual: true);
    }

    /// <summary>The UI thread reports whether a game is running; a true→false change does not itself download (TickAsync does).</summary>
    public void SetGameRunning(bool running) => _gameRunning = running;

    /// <summary>
    /// Start-up (never called in safe mode): a downloaded update → ReadyToRestart without network. Otherwise an automatic
    /// check, unless automatic checks are off or the last check was under <see cref="StartupMinInterval"/> ago.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ReleaseVersion? pending;
        try
        {
            pending = _backend.PendingRestart;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Warn("Couldn't read the downloaded update", ex);
            pending = null;
        }
        if (pending is not null)
        {
            _log.Info("A downloaded update is waiting", LogField.Of("version", pending));
            Update(s => s with
            {
                State = UpdateState.ReadyToRestart,
                Available = new AvailableUpdate(pending, null, UpdateEndpoints.ReleasePage(pending), null, null),
                Percent = 100,
            });
            return Task.CompletedTask;
        }
        var prefs = Preferences;
        if (!prefs.CheckAutomatically) return Task.CompletedTask;
        if (prefs.LastCheckUtc is { } last && _time.GetUtcNow() - last < StartupMinInterval) return Task.CompletedTask;
        return CheckAsync(manual: false, ct);
    }

    /// <summary>
    /// Checks for an update. An automatic check (<paramref name="manual"/> false) does nothing while automatic checks
    /// are off. A call shares the running check when that check is on the same channel and at least as manual; a manual
    /// call during an automatic check, or a call after a channel change, queues one more check after it (so a manual
    /// failure is always shown, REQ-DIST-20, and the new channel is always checked, REQ-DIST-18). The task ends when the
    /// check does; an automatic download it starts runs on its own.
    /// </summary>
    public Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        var channel = Preferences.Channel;
        if (!manual && !Preferences.CheckAutomatically) return Task.CompletedTask;
        lock (_runLock)
        {
            var running = _checkRun is { IsCompleted: false } r ? r : null;
            if (running is not null && (_checkRunManual || !manual) && _checkRunChannel == channel) return running;
            var run = Task.Run(async () =>
            {
                // The follow-up starts after the running check ends (whatever its outcome), so its result is the last word.
                if (running is not null) await Task.WhenAny(running).ConfigureAwait(false);
                await CheckRunAsync(manual, ct).ConfigureAwait(false);
            }, CancellationToken.None);
            _checkRun = run;
            _checkRunManual = manual;
            _checkRunChannel = channel;
            return run;
        }
    }

    /// <summary>The last automatic download started by a check or a tick (tests await it); a completed task when none.</summary>
    internal Task BackgroundDownload
    {
        get
        {
            lock (_runLock) return _backgroundDownload ?? Task.CompletedTask;
        }
    }

    /// <summary>"Update now", or Retry after a failed download. Installed copies in Available or Failed only.</summary>
    public Task DownloadAsync(CancellationToken ct = default) => DownloadCoreAsync(manual: true, ct);

    /// <summary>
    /// The periodic tick: starts a download that waited for the game once no game runs, else checks when the last check
    /// is <see cref="PeriodicInterval"/> old (REQ-DIST-21).
    /// </summary>
    public Task TickAsync(CancellationToken ct = default)
    {
        var s = Snapshot;
        var prefs = Preferences;
        if (s.State is UpdateState.Checking or UpdateState.Downloading) return Task.CompletedTask;
        if (s is { DeferredForGame: true, State: UpdateState.Available, Kind: InstallKind.Installed } && !_gameRunning
            && prefs is { AutoDownload: true, CheckAutomatically: true })
            return StartBackgroundDownload(ct);
        if (!prefs.CheckAutomatically) return Task.CompletedTask;
        if (prefs.LastCheckUtc is { } last && _time.GetUtcNow() - last < PeriodicInterval) return Task.CompletedTask;
        return CheckAsync(manual: false, ct);
    }

    /// <summary>
    /// "Skip this version" (REQ-DIST-22): back to Idle and hidden from automatic checks. Returns the skipped version for
    /// the caller to persist, or null when nothing is on offer. A ready package is kept and still applied at the next
    /// launch.
    /// </summary>
    public ReleaseVersion? SkipAvailableVersion()
    {
        ReleaseVersion? skipped = null;
        Update(s =>
        {
            if (s.Available is null || s.State is not (UpdateState.Available or UpdateState.ReadyToRestart)) return s;
            skipped = s.Available.Version;
            return s with { State = UpdateState.Idle, Available = null, DeferredForGame = false, Percent = 0 };
        });
        if (skipped is null) return null;
        lock (_stateLock) Volatile.Write(ref _prefs, _prefs with { SkippedVersion = skipped });
        _log.Info("Update skipped", LogField.Of("version", skipped));
        return skipped;
    }

    /// <summary>
    /// Starts the updater (it waits for this process to exit). Allowed after the host's token is cancelled (ExitApp
    /// step 8).
    /// </summary>
    /// <exception cref="InvalidOperationException">No installed update is ready.</exception>
    public void RestartToUpdate(IReadOnlyList<string> restartArgs)
    {
        ArgumentNullException.ThrowIfNull(restartArgs);
        if (!CanRestartToUpdate) throw new InvalidOperationException("No downloaded update is ready to apply.");
        _backend.LaunchUpdaterAfterExit(restartArgs);
    }

    private async Task CheckRunAsync(bool manual, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        bool startDownload;
        try
        {
            startDownload = await CheckLockedAsync(manual, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        // Not awaited: the check (and a command awaiting it) ends when the check does (REQ-DIST-16).
        if (startDownload) StartBackgroundDownload(ct).Forget(_log, "background update download");
    }

    private Task StartBackgroundDownload(CancellationToken ct)
    {
        var download = DownloadCoreAsync(manual: false, ct);
        lock (_runLock) _backgroundDownload = download;
        return download;
    }

    /// <summary>Runs one check under the gate. True when an automatic download should start.</summary>
    private async Task<bool> CheckLockedAsync(bool manual, CancellationToken ct)
    {
        var previous = Snapshot;
        var prefs = Preferences;
        // An automatic re-check leaves an offer (banner, tray item) in place instead of showing "Checking…" (REQ-DIST-16, 24).
        var keepOffer = !manual && previous.State is UpdateState.Available or UpdateState.ReadyToRestart;
        if (!keepOffer) Update(s => s with { State = UpdateState.Checking, LastCheckWasManual = manual });
        // The state before the check: the snapshot as it is now when it was never replaced by Checking (Skip may have run).
        UpdateSnapshot Restore(UpdateSnapshot s) => keepOffer ? s : previous with { Channel = s.Channel };
        AvailableUpdate? result;
        try
        {
            result = await _backend.CheckAsync(prefs.Channel, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Update(Restore);
            throw;
        }
        catch (Exception ex)
        {
            var kind = (ex as UpdateCheckException)?.Kind ?? UpdateErrorKind.Unknown;
            var at = CheckCompleted();
            _log.Warn("Update check failed", ex, LogField.Of("kind", kind), LogField.Of("manual", manual));
            Update(s => manual
                ? s with { State = UpdateState.Failed, Error = kind, FailedWhileDownloading = false, LastChecked = at }
                : Restore(s) with { Error = kind, LastChecked = at, LastCheckWasManual = false });
            return false;
        }

        var checkedAt = CheckCompleted();
        _log.Info("Update check finished", LogField.Of("available", result?.Version), LogField.Of("manual", manual));
        var current = _backend.CurrentVersion;
        var pending = previous.State == UpdateState.ReadyToRestart ? previous.Available?.Version ?? SafePendingRestart() : null;
        if (pending is not null && (result is null || result.Version <= pending))
        {
            Update(s => Restore(s) with { LastChecked = checkedAt, LastCheckWasManual = manual, Error = null });
            return false;
        }
        if (result is null || result.Version <= current)
        {
            Update(s => s with
            {
                State = UpdateState.UpToDate, Available = null, Error = null, Percent = 0, DeferredForGame = false,
                FailedWhileDownloading = false, LastChecked = checkedAt,
            });
            return false;
        }
        // Re-read: "Skip this version" can run while an automatic re-check keeps the banner up.
        if (!manual && Preferences.SkippedVersion is { } skipped && skipped == result.Version)
        {
            Update(s => s with
            {
                State = UpdateState.Idle, Available = null, Error = null, Percent = 0, DeferredForGame = false,
                FailedWhileDownloading = false, LastChecked = checkedAt,
            });
            return false;
        }

        var auto = _backend.Kind == InstallKind.Installed && prefs.AutoDownload;
        var gameRunning = _gameRunning;
        // Only a tick starts a deferred download, and a tick needs automatic checks on (REQ-DIST-13): with them off,
        // a check during a game offers Update now instead of promising a download that would never start.
        var deferred = auto && gameRunning && prefs.CheckAutomatically;
        Update(s => s with
        {
            State = UpdateState.Available, Available = result, Error = null, Percent = 0, DeferredForGame = deferred,
            FailedWhileDownloading = false, LastChecked = checkedAt,
        });
        return auto && !gameRunning;
    }

    private async Task DownloadCoreAsync(bool manual, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = Snapshot;
            if (previous.Kind != InstallKind.Installed || previous.Available is not { } update
                || previous.State is not (UpdateState.Available or UpdateState.Failed))
                return;
            Update(s => s with
            {
                State = UpdateState.Downloading, Percent = 0, DeferredForGame = false, Error = null, FailedWhileDownloading = false,
            });
            try
            {
                await _backend.DownloadAsync(update, new PercentProgress(this), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Update(s => previous with { Channel = s.Channel, LastChecked = s.LastChecked });
                throw;
            }
            catch (Exception ex)
            {
                var kind = (ex as UpdateCheckException)?.Kind ?? UpdateErrorKind.Unknown;
                _log.Warn("Update download failed", ex, LogField.Of("kind", kind), LogField.Of("manual", manual),
                    LogField.Of("version", update.Version));
                // A background download fails silently (REQ-DIST-20): the banner goes back to "Update now".
                Update(s => manual
                    ? s with { State = UpdateState.Failed, Error = kind, FailedWhileDownloading = true, Available = update }
                    : s with { State = UpdateState.Available, Error = kind, Percent = 0, Available = update });
                return;
            }
            _log.Info("Update downloaded", LogField.Of("version", update.Version));
            Update(s => s with { State = UpdateState.ReadyToRestart, Percent = 100, Error = null });
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnProgress(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        var changed = false;
        lock (_stateLock)
        {
            var s = _snapshot;
            if (s.State == UpdateState.Downloading && s.Percent != p)
            {
                Volatile.Write(ref _snapshot, s with { Percent = p });
                changed = true;
            }
        }
        if (changed) RaiseChanged();
    }

    /// <summary>Records a completed check and reports its time. Returns that time.</summary>
    private DateTimeOffset CheckCompleted()
    {
        var now = _time.GetUtcNow();
        lock (_stateLock) Volatile.Write(ref _prefs, _prefs with { LastCheckUtc = Later(_prefs.LastCheckUtc, now) });
        try
        {
            _lastCheckChanged(now);
        }
        catch (Exception ex)
        {
            // AppHost's callback only queues work on the dispatcher; never let it break the state machine.
            _log.Warn("Last-check callback failed", ex);
        }
        return now;
    }

    private ReleaseVersion? SafePendingRestart()
    {
        try
        {
            return _backend.PendingRestart;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Warn("Couldn't read the downloaded update", ex);
            return null;
        }
    }

    private void Update(Func<UpdateSnapshot, UpdateSnapshot> change)
    {
        bool changed;
        lock (_stateLock)
        {
            var before = _snapshot;
            var after = change(before);
            changed = !Equals(before, after);
            if (changed) Volatile.Write(ref _snapshot, after);
        }
        if (changed) RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A subscriber's bug must not stop a check or download half way.
            _log.Warn("An update-state subscriber failed", ex);
        }
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Reports synchronously on the caller's thread (System.Progress would post to a captured context).</summary>
    private sealed class PercentProgress(UpdateCoordinator owner) : IProgress<int>
    {
        public void Report(int value) => owner.OnProgress(value);
    }
}
