using Envoy.Core.Diagnostics;
using Envoy.Core.Notifications;
using Forms = System.Windows.Forms;

namespace Envoy.App.Services;

/// <summary>
/// Shows advisory notices as tray balloons (spec 07a, contract §2.2). <c>AppHost.NoticeCenter</c> has already decided
/// that the notice may show (throttle, in-game deferral, switches); this only displays it and runs its action when the
/// balloon is clicked. Created at start-up slot 8 and attached to the tray at slot 11; a notice shown before
/// <see cref="Attach"/> is held (latest wins) and shown on attach. Never created in safe mode. UI thread; never throws.
/// </summary>
internal sealed class TrayNotifier(Action<string> runAction, ILog log) : IDisposable
{
    /// <summary>How long Windows is asked to keep the balloon up (it may choose otherwise).</summary>
    public const int BalloonTimeoutMs = 8000;

    private Forms.NotifyIcon? _tray;
    private Notice? _held;
    private string? _pendingAction;
    private bool _disposed;

    /// <summary>The notice waiting for <see cref="Attach"/>, if any.</summary>
    public Notice? Held => _held;

    /// <summary>The action the balloon on screen runs when clicked; null when none is up.</summary>
    public string? PendingAction => _pendingAction;

    /// <summary>Start-up slot 11, right after the tray is created. Shows the held notice, if any.</summary>
    public void Attach(Forms.NotifyIcon tray)
    {
        ArgumentNullException.ThrowIfNull(tray);
        if (_disposed) return;
        Detach();
        _tray = tray;
        _tray.BalloonTipClicked += OnClicked;
        _tray.BalloonTipClosed += OnClosed;
        if (_held is { } held)
        {
            _held = null;
            Show(held);
        }
    }

    /// <summary>Shows <paramref name="notice"/> as a balloon, or holds it until <see cref="Attach"/>. Exceptions are logged.</summary>
    public void Show(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_disposed) return;
        if (_tray is null)
        {
            _held = notice;
            return;
        }
        try
        {
            _pendingAction = notice.ActionId;
            _tray.ShowBalloonTip(BalloonTimeoutMs, notice.Title, notice.Text, notice.IsWarning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
            log.Info("Balloon shown", LogField.Of("kind", notice.Kind), LogField.Of("key", notice.Key));
        }
        catch (Exception ex)
        {
            // Focus Assist, a hidden icon or a shell problem: the War Room list still has the notice.
            _pendingAction = null;
            log.Warn("Balloon could not be shown", ex, LogField.Of("key", notice.Key));
        }
    }

    private void OnClicked(object? sender, EventArgs e)
    {
        var action = _pendingAction;
        _pendingAction = null;
        if (action is null) return;
        try
        {
            runAction(action);
        }
        catch (Exception ex)
        {
            log.Warn("Balloon action failed", ex, LogField.Of("action", action));
        }
    }

    private void OnClosed(object? sender, EventArgs e) => _pendingAction = null;

    private void Detach()
    {
        if (_tray is null) return;
        _tray.BalloonTipClicked -= OnClicked;
        _tray.BalloonTipClosed -= OnClosed;
        _tray = null;
    }

    /// <summary>Exit step 5, before the tray: unsubscribes. The tray itself belongs to App.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _held = null;
        _pendingAction = null;
    }
}
