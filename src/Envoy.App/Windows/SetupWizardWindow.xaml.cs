using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Envoy.App.Controls;
using Envoy.App.ViewModels;
using Envoy.Core.Onboarding;

namespace Envoy.App.Windows;

/// <summary>
/// The setup guide (spec 07a, REQ-OUXA-1…12). Opened by <c>IShell.OpenSetupWizard</c> (single instance, App owns it).
/// Edits to settings controls are applied through the view model's debounce, like MainWindow's pages. Closing it any way
/// other than Finish counts as Skip setup.
/// </summary>
public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow(AppHost host, IShell shell, OnboardingStep start)
    {
        ViewModel = new SetupWizardViewModel(host, shell, Dispatcher, start);
        InitializeComponent();
        DataContext = ViewModel;

        AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnEdited));
    }

    public SetupWizardViewModel ViewModel { get; }

    /// <summary>True once the window has started closing (App must not call <see cref="Window.Close"/> again).</summary>
    public bool IsClosing { get; private set; }

    /// <summary>The panel of each built step (the smoke test checks exactly one is visible).</summary>
    internal FrameworkElement PanelFor(OnboardingStep step) => step switch
    {
        OnboardingStep.Language => LanguagePanel,
        OnboardingStep.Game => GamePanel,
        OnboardingStep.ChatArea => ChatAreaPanel,
        OnboardingStep.OcrLanguages => OcrPanel,
        OnboardingStep.TestTranslation => TestPanel,
        OnboardingStep.Hotkeys => HotkeysPanel,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    private void OnEdited(object sender, RoutedEventArgs e)
    {
        // Controls that aren't settings (the privacy box applies at once, the test sentence) opt out with c:AutoApply.Ignore.
        if (e.OriginalSource is DependencyObject d && AutoApply.GetIgnore(d)) return;
        if (!IsLoaded) return;
        ViewModel.OnEdited();
    }

    // ---- window chrome ----
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        IsClosing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosing = true;
        ViewModel.OnWindowClosed(); // Skip, unless Finish or Skip already ran
        ViewModel.Dispose(); // releases the live view's preview lease
        base.OnClosed(e);
    }
}
