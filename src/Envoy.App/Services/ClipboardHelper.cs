using System.Runtime.InteropServices;
using System.Windows;

namespace Envoy.App.Services;

/// <summary>Puts text on the clipboard, retrying while another app briefly holds it. UI thread (STA) only.</summary>
public static class ClipboardHelper
{
    /// <summary>
    /// True when the text reached the clipboard. Clipboard managers and RDP can lock it for a moment, so this tries
    /// five times, 40 ms apart.
    /// </summary>
    public static bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}
