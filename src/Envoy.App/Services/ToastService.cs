using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Envoy.App.Interop;
using Envoy.App.Windows;
using Envoy.Core.Diagnostics;
using Envoy.Core.Notifications;

namespace Envoy.App.Services;

/// <summary>
/// Shows problem toasts (REQ-REL-18, contract §2.2): only when <c>Diagnostics.ShowErrorToasts</c> is on and the
/// <see cref="NotificationThrottle"/> allows the key; at most <see cref="NotificationThrottle.MaxVisibleToasts"/> at
/// once (a new one closes the oldest), bottom-right of the game's monitor, stacking upward. Problems toast immediately
/// whether or not the game is in front; a toast never takes focus.
/// </summary>
internal sealed class ToastService(Dispatcher ui, NotificationThrottle throttle, Func<bool> enabled,
                                   Func<IntPtr> gameWindow, Action openTroubleshooting, ILog log)
{
    private const double Gap = 8;
    private readonly List<ToastWindow> _open = [];

    /// <summary>The toasts on screen, oldest first.</summary>
    public IReadOnlyList<ToastWindow> Open => _open;

    /// <summary>Any thread. Logs and never throws.</summary>
    public void Show(ProblemReport report)
    {
        if (!ui.CheckAccess())
        {
            ui.BeginInvoke(() => Show(report));
            return;
        }
        try
        {
            if (!enabled())
            {
                log.Debug("Problem toast off in settings", LogField.Of("key", report.Key));
                return;
            }
            if (!throttle.TryShowToast(report.Key))
            {
                log.Debug("Problem toast throttled", LogField.Of("key", report.Key));
                return;
            }
            while (_open.Count >= NotificationThrottle.MaxVisibleToasts) Close(_open[0]);

            var toast = new ToastWindow(report);
            toast.DetailsRequested += openTroubleshooting;
            toast.Closed += (_, _) =>
            {
                _open.Remove(toast);
                Relayout();
            };
            _open.Add(toast);
            toast.Show();
            toast.UpdateLayout();
            Relayout();
        }
        catch (Exception ex)
        {
            log.Critical("Showing a problem toast failed", ex, LogField.Of("key", report.Key));
        }
    }

    public void CloseAll()
    {
        foreach (var toast in _open.ToList()) Close(toast);
    }

    private void Close(ToastWindow toast)
    {
        _open.Remove(toast);
        if (!toast.IsClosed) toast.Close();
    }

    private void Relayout()
    {
        var area = WorkAreaDip();
        double offset = 0;
        foreach (var toast in _open)
        {
            toast.Place(area, offset);
            offset += (toast.ActualHeight > 0 ? toast.ActualHeight : 120) + Gap;
        }
    }

    /// <summary>The work area of the game's monitor (the primary one without a game), in DIPs.</summary>
    private Rect WorkAreaDip()
    {
        try
        {
            var monitor = Native.MonitorFromWindow(gameWindow(), Native.MONITOR_DEFAULTTOPRIMARY);
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
            var scale = Native.ScaleOf(monitor);
            var w = info.rcWork;
            return new Rect(w.Left / scale, w.Top / scale, (w.Right - w.Left) / scale, (w.Bottom - w.Top) / scale);
        }
        catch (Exception ex) when (ex is ExternalException or EntryPointNotFoundException or DllNotFoundException)
        {
            log.Warn("Monitor lookup failed; using the primary work area", ex);
            return SystemParameters.WorkArea;
        }
    }
}
