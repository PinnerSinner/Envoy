using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.Core.Compose;
using Envoy.Core.Diagnostics;
using Envoy.Core.Languages;
using Envoy.Core.Phrases;
using Envoy.Core.Pipeline;
using Envoy.Core.Security;
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
    private bool _recalling;

    /// <summary>
    /// Replies sent or copied this session (REQ-OUXC-39). Lives on this window, which App reuses for the whole session;
    /// memory only, never saved or logged.
    /// </summary>
    internal ReplyHistory History { get; } = new();

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
        // Opened again while open: the box itself is in front, so keep the target chosen the first time.
        _target = SendTargetGuard.TargetOnOpen(_target, targetWindow, IsVisible);
        _opening = true;

        var languages = LanguageCatalog.All.Where(l => l.Code != MyLanguage).ToList();
        TargetLanguage.ItemsSource = languages;
        var target = OutgoingTranslator.ResolveTarget(_host.Settings.Compose.DefaultTarget, _host.Pipeline.SuggestedReplyLanguage, MyLanguage);
        TargetLanguage.SelectedItem = languages.FirstOrDefault(l => l.Code == target) ?? languages.FirstOrDefault();

        FillContext();
        FillPhrases();
        Input.Text = "";
        History.EndBrowsing();
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
        var compose = _host.Settings.Compose;
        Phrases.ItemsSource = PhraseOrdering.Order(_host.Phrases.All, Target, compose.PhraseUsage, compose.PhraseOrder)
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
        UpdateCount();
    }

    /// <summary>
    /// The length of what will be sent (REQ-OUXC-41): Danger above the user's limit or when 08a's cap will cut it.
    /// Collapsed when the counter is off or nothing is translated yet. Never blocks sending.
    /// </summary>
    private void UpdateCount()
    {
        var compose = _host.Settings.Compose;
        if (!compose.ShowCharacterCount || _translated.Length == 0)
        {
            CharCount.Visibility = Visibility.Collapsed;
            return;
        }
        var info = ReplyLength.Measure(AppendOriginal(_translated), compose.MaxMessageLength);
        CharCount.Text = info.Text;
        CharCount.SetResourceReference(ForegroundProperty, info.OverLimit ? "Danger" : "Text.Muted");
        CharCount.Visibility = Visibility.Visible;
        // LiveSetting="Polite" is only announced when the live region reports a change.
        (UIElementAutomationPeer.FromElement(CharCount) ?? UIElementAutomationPeer.CreatePeerForElement(CharCount))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_recalling) History.EndBrowsing();
        _debounce.Stop();
        if (Input.Text.Trim().Length == 0) { ClearPreview(); return; }
        _debounce.Start();
    }

    private void OnTargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _translatedFor = "";
        if (IsLoaded) FillPhrases();
        if (Input.Text.Trim().Length > 0) TranslateNowAsync().Forget(_host.Log.For("Compose"), "reply preview");
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
            UpdateCount();
            PreviewSource.Text = result.ProviderId switch
            {
                "phrasebook" => "phrasebook (human translation)",
                "none" => "",
                var id => Core.Translation.ProviderCatalog.Find(id)?.Name ?? id,
            };

            // Back-translate so you can sanity-check what you're about to say.
            if (result.ProviderId is not ("phrasebook" or "none"))
            {
                var back = await _host.ComposeTranslator.TranslateAsync(new(result.Text, MyLanguage, Target), cts.Token);
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
        // Up/Down recall replies sent this session (REQ-OUXC-40); the first Up keeps the unsent draft.
        // Only unmodified arrows: Shift+Up/Down keep the TextBox's own selection behaviour.
        var plain = Keyboard.Modifiers == ModifierKeys.None;
        if (plain && e.Key == Key.Up)
        {
            e.Handled = true;
            if (History.Older(Input.Text) is { } older) SetInputFromHistory(older);
            return;
        }
        if (plain && e.Key == Key.Down)
        {
            e.Handled = true;
            if (History.Newer() is { } newer) SetInputFromHistory(newer);
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) await CopyAsync();
        else await SendAsync();
    }

    private void SetInputFromHistory(string text)
    {
        _recalling = true;
        try
        {
            Input.Text = text;
            Input.CaretIndex = Input.Text.Length;
        }
        finally
        {
            _recalling = false;
        }
    }

    /// <summary>
    /// After a successful send or copy: remembers the typed input (REQ-OUXC-39) and, when it is a quick phrase, counts it
    /// for the target language (REQ-OUXC-43).
    /// </summary>
    private void Remember(string input, string target)
    {
        History.Add(input);
        // Chips fall back to the English text when MyLanguage has none, so the lookup does too.
        if ((_host.Phrases.FindByText(input, MyLanguage) ?? _host.Phrases.FindByText(input, "en")) is not { } phrase) return;
        var compose = _host.Settings.Compose;
        PhraseOrdering.RecordUse(compose.PhraseUsage, target, phrase.Id);
        _host.ScheduleSave();
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
        var input = Input.Text;
        var target = Target;
        var text = await TranslateNowAsync();
        if (string.IsNullOrEmpty(text)) return;
        if (ClipboardHelper.TrySetText(OutgoingText.Sanitize(AppendOriginal(text)))) Remember(input, target);
        Hide();
    }

    private async Task SendAsync()
    {
        var settings = _host.Settings.Compose;
        var input = Input.Text;
        var target = Target;
        var text = await TranslateNowAsync();
        if (string.IsNullOrEmpty(text)) return;
        text = OutgoingText.Sanitize(AppendOriginal(text));
        if (text.Length == 0) return;

        if (settings.SendMode == SendMode.Clipboard)
        {
            if (ClipboardHelper.TrySetText(text)) Remember(input, target);
            Hide();
            return;
        }

        if (settings.TypingMethod == TypingMethod.Paste) ClipboardHelper.TrySetText(text);
        Hide();
        var sent = false;
        try
        {
            await KeyboardSender.SendToWindowAsync(_target, text, settings);
            sent = true;
        }
        catch (Exception ex)
        {
            // Never a modal box here: it would steal focus from the game (REQ-REL-19). Nothing joins the history.
            ClipboardHelper.TrySetText(text);
            _host.Log.For("Compose").Warn("Reply was not typed", ex);
            _host.ReportProblem(ex is SendBlockedException blocked ? ProblemText.ReplyBlocked(blocked.Message) : ProblemText.ReplySendFailed);
        }
        // Outside the try: the reply was typed, so a problem here must not report a failed send.
        if (sent) Remember(input, target);
    }

    private string AppendOriginal(string translated) =>
        _host.Settings.Compose.AppendOriginal ? $"{translated} ({Input.Text.Trim()})" : translated;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();
}
