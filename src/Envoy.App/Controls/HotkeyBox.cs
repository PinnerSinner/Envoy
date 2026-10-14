using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Envoy.App.Services;

namespace Envoy.App.Controls;

/// <summary>A text box that records a key combination when you press it (Backspace/Delete clears).</summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press the key combination you want";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete)
        {
            SetText("");
            return;
        }
        if (key is Key.Tab) { e.Handled = false; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        var mods = Keyboard.Modifiers;
        // Require a modifier for letters/digits so normal typing in game never triggers Envoy.
        if (mods == ModifierKeys.None && key is (>= Key.A and <= Key.Z) or (>= Key.D0 and <= Key.D9))
            return;

        SetText(new Hotkey(mods, key).ToString());
    }

    private void SetText(string value)
    {
        Text = value;
        GetBindingExpression(TextProperty)?.UpdateSource();
    }
}
