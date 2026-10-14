using System.Windows.Controls;
using Envoy.App.ViewModels;

namespace Envoy.App.Pages;

/// <summary>
/// The Reading Chat page. Its only logic: the live view runs while the page is visible (it follows the sidebar
/// selection, and stops while the window is hidden).
/// </summary>
public partial class ReadingChatPage : UserControl
{
    public ReadingChatPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.CapturePreview.IsActive = IsVisible;
        };
    }
}
