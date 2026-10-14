using System.Windows;

namespace Envoy.App.Services;

/// <summary>The single owner of modal message boxes in Envoy.App (contract §2).</summary>
internal static class DialogService
{
    /// <summary>A box with OK and the Information icon. Owned by <paramref name="owner"/> when it is non-null and loaded.</summary>
    public static void ShowInfo(Window? owner, string title, string message) =>
        Show(owner, title, message, MessageBoxImage.Information);

    /// <summary>A box with OK and the Warning icon. Only for App's existing unhandled-exception box until 02 replaces it.</summary>
    public static void ShowWarning(Window? owner, string title, string message) =>
        Show(owner, title, message, MessageBoxImage.Warning);

    private static void Show(Window? owner, string title, string message, MessageBoxImage icon)
    {
        if (owner is { IsLoaded: true }) MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }
}
