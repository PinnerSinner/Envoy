namespace Envoy.Core.Security;

/// <summary>Whether Envoy may keep typing into the reply target.</summary>
public enum SendTargetVerdict
{
    Ok,
    /// <summary>No window was chosen when the reply box opened.</summary>
    NoTarget,
    /// <summary>The chosen window no longer exists.</summary>
    TargetClosed,
    /// <summary>Another process's window is in front.</summary>
    FocusLost,
}

/// <summary>
/// Decides where a reply may be typed (REQ-QSC-29, contract §8.8). Pure and static: the App supplies the window and
/// process facts and stops typing on any verdict other than <see cref="SendTargetVerdict.Ok"/>.
/// </summary>
public static class SendTargetGuard
{
    /// <summary>
    /// Chooses the window the reply box will type into, at the moment it opens. The detected game window wins when it
    /// still exists. Otherwise the foreground window, unless it is none, its process is unknown or Envoy itself, or it
    /// belongs to the Windows shell (taskbar, desktop), which gives no target (0).
    /// </summary>
    public static nint ChooseTarget(nint gameWindow, bool gameWindowExists, nint foregroundWindow,
                                    uint foregroundProcessId, string? foregroundProcessName, uint ownProcessId)
    {
        if (gameWindow != 0 && gameWindowExists) return gameWindow;
        if (foregroundWindow == 0 || foregroundProcessId == 0 || foregroundProcessId == ownProcessId) return 0;
        return IsShellProcess(foregroundProcessName) ? 0 : foregroundWindow;
    }

    /// <summary>"explorer" or "explorer.exe", ignoring case. explorer.exe owns Shell_TrayWnd and the desktop.</summary>
    public static bool IsShellProcess(string? processName) =>
        string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processName, "explorer.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checked after focusing and before every keystroke. A target process id of 0 means unknown, which counts as no
    /// target. Compared by process rather than window handle, so a game that recreates its top-level window
    /// (fullscreen toggle) still matches.
    /// </summary>
    public static SendTargetVerdict Check(bool hasTarget, bool targetExists, uint targetProcessId, uint foregroundProcessId)
    {
        if (!hasTarget) return SendTargetVerdict.NoTarget;
        if (!targetExists) return SendTargetVerdict.TargetClosed;
        if (targetProcessId == 0) return SendTargetVerdict.NoTarget;
        return foregroundProcessId == targetProcessId ? SendTargetVerdict.Ok : SendTargetVerdict.FocusLost;
    }
}
