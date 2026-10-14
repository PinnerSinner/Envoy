using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.Core.Languages;
using Envoy.Core.Phrases;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;

namespace Envoy.App.Windows;

/// <summary>Hotkey-summoned box: type in your language, see the translation (and a back-translation), send it to the game.</summary>
public partial class ComposeWindow : Window
{
    public sealed record PhraseChip(Phrase Phrase, string Icon, string Label, string Preview);

    private readonly AppHost _host;
    private readonly DispatcherTimer _debounce;
    private IntPtr _target;
    private CancellationTokenSource? _translateCts;
    private string _translated = "";
    private string _translatedFor = "";
    private bool _opening;

    public ComposeWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Input, async (_, _) => await TranslateNowAsync(), Dispatcher) { IsEnabled = false };
        Deactivated += (_, _) => { if (!_opening && IsVisible && !SmokeTest.IsRunning) Hide(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    private string MyLanguage => _host.Settings.MyLanguage;
    private string Target => (TargetLanguage.SelectedItem as Language)?.Code ?? "en";

    public void Open(IntPtr targetWindow)
    {
        _opening = true;
        _target = targetWindow;

        var languages = LanguageCatalog.All.Where(l => l.Code != MyLanguage).ToList();
        TargetLanguage.ItemsSource = languages;
        var target = OutgoingTranslator.ResolveTarget(_host.Settings.Compose.DefaultTarget, _host.Pipeline.SuggestedReplyLanguage, MyLanguage);
        TargetLanguage.SelectedItem = languages.FirstOrDefault(l => l.Code == target) ?? languages.FirstOrDefault();

        FillContext();
        FillPhrases();
        Input.Text = "";
        ClearPreview();
        Hint.Text = _host.Settings.Compose.SendMode switch
        {
            SendMode.TypeAndSend => "Enter sends it in game chat · Ctrl+Enter copies · Esc closes",
            SendMode.TypeOnly => "Enter types it into game chat (you press Enter) · Esc closes",
            _ => "Enter copies the translation · Esc closes",
        };

        PlaceNearGame();
        Show();
        Activate();
        Input.Focus();
        Keyboard.Focus(Input);
        _opening = false;
    }

    private void PlaceNearGame()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.22;
    }

    private void FillContext()
    {
        var last = _host.History.FirstOrDefault(m => m.Status is IncomingStatus.Translated or IncomingStatus.Untranslated);
        ContextBox.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        if (last is null) return;
        ContextSender.Text = last.Chat.Sender is { } s ? s + ": " : "";
        ContextOriginal.Text = last.Chat.Text;
        ContextTranslation.Text = last.Translation is { } t ? "  →  " + t : "";
    }

    private void FillPhrases()
    {
        Phrases.ItemsSource = _host.Phrases.All
            .Select(p => new PhraseChip(p, p.Icon, p.In(MyLanguage) ?? p.In("en") ?? p.Id, p.In(Target) ?? ""))
            .ToList();
    }

    private void ClearPreview()
    {
        Preview.Text = "";
        BackTranslation.Text = "";
        PreviewSource.Text = "";
        Placeholder.Visibility = Visibility.Visible;
        _translated = "";
        _translatedFor = "";
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        if (Input.Text.Trim().Length == 0) { ClearPreview(); return; }
        _debounce.Start();
    }

    private void OnTargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _translatedFor = "";
        if (IsLoaded) FillPhrases();
        if (Input.Text.Trim().Length > 0) _ = TranslateNowAsync();
    }

    private async Task<string?> TranslateNowAsync()
    {
        _debounce.Stop();
        var text = Input.Text.Trim();
        var key = $"{Target}\u001f{text}";
        if (text.Length == 0) return null;
        if (key == _translatedFor) return _translated;

        _translateCts?.Cancel();
        var cts = _translateCts = new CancellationTokenSource();
        Preview.Text = "…";
        BackTranslation.Text = "";
        try
        {
            var result = await _host.Outgoing.TranslateAsync(text, MyLanguage, Target, cts.Token);
            if (cts.IsCancellationRequested) return null;
            _translated = result.Text;
            _translatedFor = key;
            Preview.Text = result.Text;
            PreviewSource.Text = result.ProviderId switch
            {
                "phrasebook" => "phrasebook (human translation)",
                "none" => "",
                var id => Core.Translation.ProviderCatalog.Find(id)?.Name ?? id,
            };

            // Back-translate so you can sanity-check what you're about to say.
            if (result.ProviderId is not ("phrasebook" or "none"))
            {
                var back = await _host.Translator.TranslateAsync(new(result.Text, MyLanguage, Target), cts.Token);
                if (!cts.IsCancellationRequested) BackTranslation.Text = "Means: " + back.Text;
            }
            return _translated;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Preview.Text = "";
            BackTranslation.Text = "⚠ " + ex.Message;
            return null;
        }
    }

    private async void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) await CopyAsync();
        else await SendAsync();
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();
    private async void OnCopy(object sender, RoutedEventArgs e) => await CopyAsync();

    private async void OnPhrase(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PhraseChip chip) return;
        Input.Text = chip.Label;
        Input.CaretIndex = Input.Text.Length;
        await TranslateNowAsync();
        Input.Focus();
    }

    private async Task CopyAsync()
    {
        var text = await TranslateNowAsync();
        if (string.IsNullOrEmpty(text)) return;
        SetClipboard(AppendOriginal(text));
        Hide();
    }

    private async Task SendAsync()
    {
        var settings = _host.Settings.Compose;
        var text = await TranslateNowAsync();
        if (string.IsNullOrEmpty(text)) return;
        text = AppendOriginal(text);

        if (settings.SendMode == SendMode.Clipboard)
        {
            SetClipboard(text);
            Hide();
            return;
        }

        if (settings.TypingMethod == TypingMethod.Paste) SetClipboard(text);
        Hide();
        try
        {
            await KeyboardSender.SendToWindowAsync(_target, text, settings);
        }
        catch (Exception ex)
        {
            SetClipboard(text);
            DialogService.ShowInfo(null, "Envoy", $"{ex.Message}\n\nThe translation was copied to your clipboard instead.");
        }
    }

    private string AppendOriginal(string translated) =>
        _host.Settings.Compose.AppendOriginal ? $"{translated} ({Input.Text.Trim()})" : translated;

    private static void SetClipboard(string text)
    {
        // The clipboard can be briefly locked by other apps (clipboard managers, RDP); retry a few times.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(40);
            }
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();
}
