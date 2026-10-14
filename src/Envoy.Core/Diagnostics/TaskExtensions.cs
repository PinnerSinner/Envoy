namespace Envoy.Core.Diagnostics;

/// <summary>Fire-and-forget with a record (REQ-REL-16). Use instead of <c>_ = SomethingAsync()</c>.</summary>
public static class TaskExtensions
{
    /// <summary>
    /// Observes <paramref name="task"/>: a fault is logged at Error as "{what} failed" with the exception, a cancellation
    /// at Debug as "{what} cancelled". Never throws. An already-completed task is handled synchronously.
    /// <paramref name="what"/> is a fixed developer description ("update check"), never user data.
    /// </summary>
    public static void Forget(this Task task, ILog log, string what)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(log);
        if (task.IsCompleted)
        {
            Observe(task, log, what);
            return;
        }
        task.ContinueWith(t => Observe(t, log, what), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Observe(Task task, ILog log, string what)
    {
        try
        {
            if (task.IsFaulted)
            {
                var ex = task.Exception!.InnerExceptions.Count == 1 ? task.Exception.InnerException! : task.Exception;
                log.Error(what + " failed", ex);
            }
            else if (task.IsCanceled)
            {
                log.Debug(what + " cancelled");
            }
        }
        catch (Exception)
        {
            // Logging never throws, but a fire-and-forget continuation must not be the first place that changes.
        }
    }
}
