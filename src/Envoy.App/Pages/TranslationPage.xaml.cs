using System.Windows.Controls;
using Envoy.App.ViewModels;

namespace Envoy.App.Pages;

/// <summary>
/// The Translation page. Its only logic: the provider-health text refreshes while the page is visible (01's
/// <c>IsVisibleChanged</c> → view-model flag pattern).
/// </summary>
public partial class TranslationPage : UserControl
{
    public TranslationPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.IsTranslationPageActive = IsVisible;
        };
    }
}
