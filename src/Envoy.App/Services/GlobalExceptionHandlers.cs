using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Envoy.Core.Diagnostics;
using Envoy.Core.Notifications;
using Forms = System.Windows.Forms;

namespace Envoy.App.Services;

/// <summary>
/// Wires the four global exception events to the Core <see cref="UnhandledErrorPolicy"/> and applies its outcome
/// (REQ-REL-11/12/13). The WinForms exception dialog never appears: tray-callback exceptions go through the same policy.
/// </summary>
internal sealed class GlobalExceptionHandlers
{
    private readonly Application _app;
    private readonly UnhandledErrorPolicy _policy;
    private readonly ILog _log;
    private readonly List<UnhandledSource> _installed = [];

    private GlobalExceptionHandlers(Application app, UnhandledErrorPolicy policy, ILog log)
    {
        _app = app;
        _policy = policy;
        _log = log;
    }

    /// <summary>Shows a problem toast. Until the toast service exists (early start-up) problems are only logged.</summary>
    public Action<ProblemReport>? ShowProblem { get; set; }

    /// <summary>
    /// Runs once, just before the crash-loop box: hides the tray icon and closes toasts, because <c>Shutdown(1)</c> skips
    /// <c>ExitApp</c> and a NotifyIcon that is never disposed stays in the tray as a ghost.
    /// </summary>
    public Action? BeforeCrashLoopExit { get; set; }

    /// <summary>The events this instance subscribed to, for the smoke test's <c>handlers</c> section.</summary>
    public IReadOnlyList<UnhandledSource> InstalledSources => _installed;

    public static GlobalExceptionHandlers Install(Application app, UnhandledErrorPolicy policy, ILog log)
    {
        var handlers = new GlobalExceptionHandlers(app, policy, log);
        app.DispatcherUnhandledException += handlers.OnDispatcher;
        handlers._installed.Add(UnhandledSource.Dispatcher);

        // Must run before any WinForms control (the tray icon) exists.
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.CatchException);
        Forms.Application.ThreadException += handlers.OnWinForms;
        handlers._installed.Add(UnhandledSource.WinForms);

        TaskScheduler.UnobservedTaskException += handlers.OnUnobserved;
        handlers._installed.Add(UnhandledSource.TaskScheduler);

        AppDomain.CurrentDomain.UnhandledException += handlers.OnDomain;
        handlers._installed.Add(UnhandledSource.AppDomain);
        return handlers;
    }

    private void OnDispatcher(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (Apply(_policy.OnUiException(e.Exception, UnhandledSource.Dispatcher))) e.Handled = true;
    }

    private void OnWinForms(object sender, ThreadExceptionEventArgs e)
    {
        // With CatchException mode the exception is already caught; LeaveUnhandled (smoke test) rethrows it on the
        // dispatcher so CI fails exactly as for a WPF exception.
        if (!Apply(_policy.OnUiException(e.Exception, UnhandledSource.WinForms)))
        {
            var ex = e.Exception;
            _app.Dispatcher.BeginInvoke(() => throw new InvalidOperationException("Unhandled exception in a tray callback.", ex));
        }
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _policy.OnUnobserved(e.Exception);
        e.SetObserved();
    }

    private void OnDomain(object sender, UnhandledExceptionEventArgs e) => _policy.OnDomainException(e.ExceptionObject, e.IsTerminating);

    /// <summary>Applies an outcome; true when the exception is now handled.</summary>
    private bool Apply(UiErrorOutcome outcome)
    {
        switch (outcome.Action)
        {
            case UiErrorAction.ShowProblem:
                try
                {
                    if (outcome.Problem is { } problem) ShowProblem?.Invoke(problem);
                }
                catch (Exception ex)
                {
                    _log.Critical("Showing a problem toast failed", ex);
                }
                return true;
            case UiErrorAction.ExitCrashLoop:
                // The policy returns this once; later exceptions (the box below pumps messages, so a recurring source
                // keeps throwing) come back as HandledWhileExiting, so exactly one box is shown.
                try
                {
                    BeforeCrashLoopExit?.Invoke();
                }
                catch (Exception ex)
                {
                    _log.Critical("Preparing the crash-loop exit failed", ex);
                }
                DialogService.ShowWarning(null, CrashLoopText.Title, CrashLoopText.Message);
                _app.Shutdown(1);
                return true;
            case UiErrorAction.HandledWhileExiting:
                return true;
            default:
                return false;
        }
    }
}
