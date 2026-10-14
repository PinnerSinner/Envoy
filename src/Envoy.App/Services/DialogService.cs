using System.Windows;
using Microsoft.Win32;

namespace Envoy.App.Services;

/// <summary>The single owner of modal message boxes in Envoy.App (contract §2).</summary>
internal static class DialogService
{
    /// <summary>A box with OK and the Information icon. Owned by <paramref name="owner"/> when it is non-null and loaded.</summary>
    public static void ShowInfo(Window? owner, string title, string message) =>
        Show(owner, title, message, MessageBoxImage.Information);

    /// <summary>A box with OK and the Warning icon. Only for the crash-loop message (REQ-REL-15).</summary>
    public static void ShowWarning(Window? owner, string title, string message) =>
        Show(owner, title, message, MessageBoxImage.Warning);

    /// <summary>OK/Cancel with the Warning icon; true for OK. Only for confirmations the user starts in MainWindow.</summary>
    public static bool Confirm(Window? owner, string title, string message)
    {
        var result = owner is { IsLoaded: true }
            ? MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }

    /// <summary>A save-file dialog for a .zip, starting in the Desktop folder; null when cancelled.</summary>
    public static string? ChooseSavePath(Window? owner, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultFileName,
            DefaultExt = ".zip",
            Filter = "Zip archive (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AddExtension = true,
            OverwritePrompt = true,
        };
        var ok = owner is { IsLoaded: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FileName : null;
    }

    private static void Show(Window? owner, string title, string message, MessageBoxImage icon)
    {
        if (owner is { IsLoaded: true }) MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }
}
