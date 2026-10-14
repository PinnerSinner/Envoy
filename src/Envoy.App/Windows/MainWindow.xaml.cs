using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.App.ViewModels;
using Envoy.Core.Languages;
using Envoy.Core.Phrases;

namespace Envoy.App.Windows;

public partial class MainWindow : Window
{
    public sealed record PhraseRow(string Icon, string Category, string Mine, string Theirs);

    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _applyTimer;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow(AppHost host)
    {
        _host = host;
        _vm = new MainViewModel(host);
        InitializeComponent();
        DataContext = _vm;

        _applyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => ApplyNow(), Dispatcher) { IsEnabled = false };
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _vm.RefreshStatus(), Dispatcher);
        _statusTimer.Start();

        // Any edit anywhere in the settings pages is applied (and saved) shortly after it happens.
        AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler(OnEdited));

        NavCapture.Checked += (_, _) => SetPreview(true);
        NavCapture.Unchecked += (_, _) => SetPreview(false);
        _host.Watcher.FrameCaptured += OnFrame;

        PhrasePreviewLanguage.SelectedItem = LanguageCatalog.Find(_host.Pipeline.LastOpponentLanguage) ?? LanguageCatalog.Find("zh-CN");
        FillPhrases();
    }

    /// <summary>Page names, for the smoke test.</summary>
    public IReadOnlyList<string> PageNames { get; } =
        ["Dashboard", "Translation", "Capture", "Overlay", "Reply", "Phrases", "Hotkeys", "Appearance", "About"];

    public void Navigate(string page)
    {
        if (FindName("Nav" + page) is RadioButton nav) nav.IsChecked = true;
    }

    private void OnEdited(object sender, RoutedEventArgs e)
    {
        // Ignore controls that aren't settings.
        if (e.OriginalSource is FrameworkElement fe &&
            (fe == TestInput || fe == PreviewText || fe == PhrasePreviewLanguage || fe is RadioButton { GroupName: "Nav" }))
            return;
        if (!IsLoaded) return;
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void ApplyNow()
    {
        _applyTimer.Stop();
        _host.ApplySettings();
        _vm.RefreshStatus();
        _vm.RefreshProviderSections();
    }

    // ---- live capture preview ----
    private void SetPreview(bool on)
    {
        _host.Watcher.PublishFrames = on;
        if (on) _host.Watcher.Invalidate();
    }

    private void OnFrame(CaptureFrame frame)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_host.Watcher.PublishFrames) return;
            var bmp = BitmapSource.Create(frame.Image.Width, frame.Image.Height, 96, 96, PixelFormats.Bgra32, null, frame.Image.Pixels, frame.Image.Stride);
            bmp.Freeze();
            PreviewImage.Source = bmp;
            PreviewText.Text = frame.Lines.Count == 0 ? "(no text found)" : string.Join(Environment.NewLine, frame.Lines);
            PreviewStats.Text = $"{frame.Lines.Count} lines · OCR {frame.OcrTime.TotalMilliseconds:0} ms";
        });
    }

    // ---- phrasebook page ----
    private void FillPhrases()
    {
        var target = (PhrasePreviewLanguage.SelectedItem as Language)?.Code ?? "zh-CN";
        var mine = _host.Settings.MyLanguage;
        PhraseList.ItemsSource = _host.Phrases.All
            .Select(p => new PhraseRow(p.Icon, p.Category, p.In(mine) ?? p.In("en") ?? p.Id, p.In(target) ?? "—"))
            .ToList();
    }

    private void OnPhrasePreviewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) FillPhrases();
    }

    private void OnEditPhrases(object sender, RoutedEventArgs e)
    {
        var path = AppHost.UserPhrasesPath;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                [
                  {
                    "id": "my-first-phrase",
                    "category": "Mine",
                    "icon": "★",
                    "text": {
                      "en": "Nice to meet you!",
                      "zh-CN": "很高兴认识你！",
                      "ko": "만나서 반가워요!"
                    }
                  }
                ]
                """);
        }
        App.OpenUrl(path);
        MessageBox.Show(this, "Your phrases file is open. Save it, then press OK to reload.\n\nUse the same \"id\" as a built-in phrase to replace it.",
            "Envoy", MessageBoxButton.OK, MessageBoxImage.Information);
        _host.ReloadPhrases();
        FillPhrases();
    }

    // ---- actions ----
    private void OnSelectRegion(object sender, RoutedEventArgs e) => App.Current.SelectRegion();
    private void OnToggleCapture(object sender, RoutedEventArgs e) => _host.ToggleCapture();
    private void OnDemo(object sender, RoutedEventArgs e) => _host.ShowDemoMessages();
    private void OnEditOverlay(object sender, RoutedEventArgs e) => App.Current.ToggleOverlayEditMode();
    private void OnCompose(object sender, RoutedEventArgs e) => App.Current.OpenCompose();
    private void OnScan(object sender, RoutedEventArgs e) => App.Current.ScanAndTranslate();
    private void OnQuit(object sender, RoutedEventArgs e) => App.Current.ExitApp();

    private void OnNewMatch(object sender, RoutedEventArgs e) => _host.NewMatch();

    private void OnTestVoice(object sender, RoutedEventArgs e)
    {
        ApplyNow();
        _host.TestVoice();
    }

    private void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (_host.Update is { } u) App.OpenUrl(u.PageUrl);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppHost.LogsFolder);
        App.OpenUrl(AppHost.LogsFolder);
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppHost.DataFolder);
        App.OpenUrl(AppHost.DataFolder);
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        App.OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private async void OnTestTranslate(object sender, RoutedEventArgs e)
    {
        ApplyNow();
        TestOutput.Text = "…";
        try
        {
            var result = await _host.Translator.TranslateAsync(new(TestInput.Text, _host.Settings.MyLanguage));
            var provider = Core.Translation.ProviderCatalog.Find(result.ProviderId)?.Name ?? result.ProviderId;
            TestOutput.Text = $"{result.Text}    ({LanguageCatalog.NameOf(result.DetectedSourceLanguage)} → {LanguageCatalog.NameOf(_host.Settings.MyLanguage)}, via {provider})";
        }
        catch (Exception ex)
        {
            TestOutput.Text = "⚠ " + ex.Message;
        }
    }

    private void OnInstallOcr(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string code || LanguageCatalog.Find(code) is not { } lang) return;
        // Windows capability names use e.g. zh-CN / ko-KR rather than script tags.
        var tag = code is "zh-CN" or "zh-TW" ? code : lang.OcrTag;
        var command = $"Add-WindowsCapability -Online -Name 'Language.OCR~~~{tag}~0.0.1.0'; Write-Host 'Done - restart Envoy to use it.'; Start-Sleep 4";
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{command}\"") { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the admin prompt: point them at Settings instead.
            App.OpenUrl("ms-settings:regionlanguage");
        }
    }

    // ---- window chrome ----
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        if (_applyTimer.IsEnabled) ApplyNow();
        _host.Watcher.FrameCaptured -= OnFrame;
        _host.Watcher.PublishFrames = false;
        _host.Settings.FirstRunCompleted = true;
        _host.SaveNow();
        base.OnClosed(e);
    }
}
