namespace Envoy.Core.Capture;

/// <summary>A <see cref="SingleFlight"/> call was refused because an earlier one is still running.</summary>
public sealed class SingleFlightBusyException : InvalidOperationException
{
    public SingleFlightBusyException() : base("A previous call is still running.")
    {
    }

    public SingleFlightBusyException(string message) : base(message)
    {
    }

    public SingleFlightBusyException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// One call at a time, each bounded by a timeout (REQ-CAP-25, 26). A call that times out is cancelled through its token
/// and abandoned, but <see cref="IsBusy"/> stays true until its work really finishes, so a hung OCR engine is never
/// called twice. Thread-safe.
/// </summary>
public sealed class SingleFlight(TimeProvider time)
{
    private int _busy;
    private Call? _current;

    /// <summary>True from the start of a call until its work task completes (even after a timeout).</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>How long the running (or abandoned) call has been going; zero when idle.</summary>
    public TimeSpan BusyFor => Volatile.Read(ref _current) is { } call ? time.GetElapsedTime(call.StartedAt) : TimeSpan.Zero;

    /// <summary>
    /// Runs <paramref name="work"/> with a token linked to <paramref name="ct"/>. Throws
    /// <see cref="SingleFlightBusyException"/> while busy. After <paramref name="timeout"/> it cancels that token and
    /// throws <see cref="TimeoutException"/>; <see cref="IsBusy"/> stays true until the work ends. A fault clears
    /// <see cref="IsBusy"/> and is rethrown.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new SingleFlightBusyException();

        var call = new Call(this, CancellationTokenSource.CreateLinkedTokenSource(ct), time.GetTimestamp());
        Volatile.Write(ref _current, call);
        Task<T> task;
        try
        {
            task = work(call.Token);
        }
        catch
        {
            call.Complete(null);
            throw;
        }

        // Clears IsBusy when the work really ends, even if nobody awaits it any more.
        _ = task.ContinueWith(static (t, state) => ((Call)state!).Complete(t), call, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return await task.WaitAsync(timeout, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            call.Cancel();
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !task.IsCompleted)
        {
            call.Cancel();
            throw;
        }
        finally
        {
            if (task.IsCompleted) call.Complete(task);
        }
    }

    private sealed class Call(SingleFlight owner, CancellationTokenSource cts, long startedAt)
    {
        private readonly object _gate = new();
        private bool _done;

        public CancellationToken Token => cts.Token;

        public long StartedAt => startedAt;

        public void Cancel()
        {
            lock (_gate)
            {
                if (!_done) cts.Cancel();
            }
        }

        public void Complete(Task? task)
        {
            _ = task?.Exception; // observed: an abandoned call's fault must not surface as an unobserved task exception
            lock (_gate)
            {
                if (_done) return;
                _done = true;
                cts.Dispose();
            }
            Interlocked.CompareExchange(ref owner._current, null, this);
            Volatile.Write(ref owner._busy, 0);
        }
    }
}

/// <summary>
/// When an abandoned OCR call counts as stuck (REQ-CAP-26, Risks "Abandoned OCR operations"). WinRT cancellation is
/// best-effort, so a call can outlive its timeout for good; the watcher then abandons it with its engines.
/// </summary>
public static class StuckOcr
{
    /// <summary>Automatic recoveries allowed per manual <c>Start</c>; after that the watcher stops trying on its own.</summary>
    public const int MaxRecoveries = 3;

    /// <summary>True once a call has run for at least twice the OCR timeout.</summary>
    public static bool IsStuck(TimeSpan busyFor, TimeSpan timeout) => timeout > TimeSpan.Zero && busyFor >= timeout * 2;
}
