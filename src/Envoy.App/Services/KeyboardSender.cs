using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Envoy.App.Interop;
using Envoy.Core.Security;
using Envoy.Core.Settings;

namespace Envoy.App.Services;

/// <summary>Thrown when the reply target check fails, so the reply falls back to the clipboard.</summary>
public sealed class SendBlockedException(SendTargetVerdict verdict)
    : InvalidOperationException(SecurityText.SendBlocked(verdict))
{
    public SendTargetVerdict Verdict { get; } = verdict;
}

/// <summary>
/// Types text into the game the same way an on-screen keyboard or IME does (SendInput). Nothing is injected into
/// the game's process; from the game's point of view a fast typist used the keyboard. Every keystroke is guarded by
/// <see cref="SendTargetGuard"/>, so a reply never lands in a window the user didn't reply to.
/// </summary>
public static class KeyboardSender
{
    /// <summary>
    /// Chooses the reply target when the reply box opens: the detected game window, else the foreground window
    /// unless it is Envoy or the Windows shell (contract §8.8). Reads window and process facts only.
    /// </summary>
    public static IntPtr ResolveTarget(IntPtr gameWindow)
    {
        var fg = Native.GetForegroundWindow();
        var fgPid = ProcessOf(fg);
        return SendTargetGuard.ChooseTarget(gameWindow, gameWindow != IntPtr.Zero && Native.IsWindow(gameWindow),
            fg, fgPid, ProcessNameOf(fgPid), (uint)Environment.ProcessId);
    }

    /// <summary>
    /// Focuses <paramref name="target"/> and types <paramref name="text"/> (sanitised again here, defence in depth).
    /// Throws <see cref="SendBlockedException"/> before the first keystroke that could reach another window.
    /// </summary>
    public static async Task SendToWindowAsync(IntPtr target, string text, ComposeSettings settings, CancellationToken ct = default)
    {
        text = OutgoingText.Sanitize(text);
        if (text.Length == 0) return;
        var targetPid = ProcessOf(target);
        Ensure(target, targetPid, requireFocus: false);
        _ = Native.SetForegroundWindow(target);
        await Task.Delay(settings.FocusDelayMs, ct);
        Ensure(target, targetPid);

        if (!string.Equals(settings.OpenChatKey, "None", StringComparison.OrdinalIgnoreCase) && Hotkey.TryParse(settings.OpenChatKey, out var open))
        {
            PressCombination(open);
            await Task.Delay(Math.Max(60, settings.FocusDelayMs / 2), ct);
            Ensure(target, targetPid);
        }

        if (settings.TypingMethod == TypingMethod.Paste)
        {
            Ensure(target, targetPid);
            PressCombination(new Hotkey(ModifierKeys.Control, Key.V));
        }
        else
        {
            foreach (var unit in text)
            {
                ct.ThrowIfCancellationRequested();
                Ensure(target, targetPid);
                SendUnicode(unit);
                if (settings.KeystrokeDelayMs > 0) await Task.Delay(settings.KeystrokeDelayMs, ct);
            }
        }

        if (settings.SendMode == SendMode.TypeAndSend)
        {
            await Task.Delay(40, ct);
            Ensure(target, targetPid);
            PressKey(Key.Enter);
        }
    }

    private static void Ensure(IntPtr target, uint targetPid, bool requireFocus = true)
    {
        var hasTarget = target != IntPtr.Zero;
        var foregroundPid = requireFocus ? ProcessOf(Native.GetForegroundWindow()) : targetPid;
        var verdict = SendTargetGuard.Check(hasTarget, hasTarget && Native.IsWindow(target), targetPid, foregroundPid);
        if (verdict != SendTargetVerdict.Ok) throw new SendBlockedException(verdict);
    }

    /// <summary>The process that owns a window; 0 for no window or when the lookup fails.</summary>
    private static uint ProcessOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return 0;
        _ = Native.GetWindowThreadProcessId(window, out var pid);
        return pid;
    }

    /// <summary>The process name from the process list (the process itself is never opened); null on failure.</summary>
    private static string? ProcessNameOf(uint pid)
    {
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public static void PressCombination(Hotkey hotkey)
    {
        var mods = new List<Key>();
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) mods.Add(Key.LeftCtrl);
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) mods.Add(Key.LeftShift);
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) mods.Add(Key.LeftAlt);
        var inputs = new List<Native.INPUT>();
        inputs.AddRange(mods.Select(m => VirtualKey(m, up: false)));
        inputs.Add(VirtualKey(hotkey.Key, up: false));
        inputs.Add(VirtualKey(hotkey.Key, up: true));
        inputs.AddRange(mods.AsEnumerable().Reverse().Select(m => VirtualKey(m, up: true)));
        Send([.. inputs]);
    }

    public static void PressKey(Key key) => Send([VirtualKey(key, up: false), VirtualKey(key, up: true)]);

    private static void SendUnicode(char c) =>
        Send([
            new Native.INPUT { type = Native.INPUT_KEYBOARD, U = { ki = new Native.KEYBDINPUT { wScan = c, dwFlags = Native.KEYEVENTF_UNICODE } } },
            new Native.INPUT { type = Native.INPUT_KEYBOARD, U = { ki = new Native.KEYBDINPUT { wScan = c, dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP } } },
        ]);

    private static Native.INPUT VirtualKey(Key key, bool up)
    {
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        // Many games read scan codes rather than virtual keys, so send both.
        var scan = (ushort)Native.MapVirtualKeyW(vk, 0);
        var flags = up ? Native.KEYEVENTF_KEYUP : 0;
        if (key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Insert or Key.Delete or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.RightCtrl or Key.RightAlt or Key.Divide)
            flags |= Native.KEYEVENTF_EXTENDEDKEY;
        return new Native.INPUT { type = Native.INPUT_KEYBOARD, U = { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
    }

    private static void Send(Native.INPUT[] inputs)
    {
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException("Windows blocked the keystrokes. If the game runs as administrator, run Envoy as administrator too.");
    }
}
