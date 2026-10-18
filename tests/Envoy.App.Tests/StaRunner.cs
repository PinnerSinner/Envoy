using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Envoy.App.Tests;

/// <summary>
/// Runs async WPF work on a dedicated STA thread with its own <see cref="Dispatcher"/>, as the app's UI thread is:
/// rendering needs STA, and awaits inside the work resume on that dispatcher.
/// </summary>
internal static class StaRunner
{
    public static T Run<T>(Func<Task<T>> work, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(work);
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    result = await work();
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(timeout)) throw new TimeoutException($"The STA work did not finish within {timeout}.");
        error?.Throw();
        return result;
    }

    public static T Run<T>(Func<T> work, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Run(() => Task.FromResult(work()), timeout);
    }
}
