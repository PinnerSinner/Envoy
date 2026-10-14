using System.Runtime.InteropServices;
using System.Windows.Input;
using Envoy.App.Interop;
using Envoy.Core.Settings;

namespace Envoy.App.Services;

/// <summary>
/// Types text into the game the same way an on-screen keyboard or IME does (SendInput). Nothing is injected into
/// the game's process; from the game's point of view a fast typist used the keyboard.
/// </summary>
public static class KeyboardSender
{
    public static async Task SendToWindowAsync(IntPtr gameWindow, string text, ComposeSettings settings, CancellationToken ct = default)
    {
        if (gameWindow != IntPtr.Zero && Native.IsWindow(gameWindow))
        {
            Native.SetForegroundWindow(gameWindow);
            await Task.Delay(settings.FocusDelayMs, ct);
        }

        if (!string.Equals(settings.OpenChatKey, "None", StringComparison.OrdinalIgnoreCase) && Hotkey.TryParse(settings.OpenChatKey, out var open))
        {
            PressCombination(open);
            await Task.Delay(Math.Max(60, settings.FocusDelayMs / 2), ct);
        }

        if (settings.TypingMethod == TypingMethod.Paste)
        {
            PressCombination(new Hotkey(ModifierKeys.Control, Key.V));
        }
        else
        {
            foreach (var unit in text)
            {
                ct.ThrowIfCancellationRequested();
                SendUnicode(unit);
                if (settings.KeystrokeDelayMs > 0) await Task.Delay(settings.KeystrokeDelayMs, ct);
            }
        }

        if (settings.SendMode == SendMode.TypeAndSend)
        {
            await Task.Delay(40, ct);
            PressKey(Key.Enter);
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
