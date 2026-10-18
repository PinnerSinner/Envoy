using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.Core.Languages;
using Envoy.Core.Mvvm;
using Envoy.Core.Pipeline;
using Envoy.Core.Ux;

namespace Envoy.App.Windows;

/// <summary>View model for one line in the overlay.</summary>
public sealed class OverlayItem(IncomingMessage message, bool showSender = true, bool showOriginal = true) : INotifyPropertyChanged
{
    private double _opacity = 1;
    private string _copyFeedback = "";
    private DispatcherTimer? _feedbackTimer;

    public IncomingMessage Message { get; } = message;
    public DateTime ShownAt { get; } = DateTime.UtcNow;

    public string Sender => Message.Chat.Sender ?? "";
    public string SenderPrefix => showSender && Message.Chat.Sender is { } s ? $"{s}: " : "";
    public string Channel => showSender && Message.Chat.Channel is { } c ? $"[{c}] " : "";

    public string Main => Message.Status switch
    {
        IncomingStatus.Pending => Message.Chat.Text,
        IncomingStatus.Failed => Message.Chat.Text,
        _ => Message.DisplayText,
    };

    /// <summary>Original text, shown under the translation when it differs.</summary>
    public string Original => Message.Status == IncomingStatus.Translated ? Message.Chat.Text : "";
    public bool HasOriginal => showOriginal && Original.Length > 0;

    public string LanguageBadge => Message.SourceLanguage is { } code ? Short(code) : "";
    public bool HasLanguage => LanguageBadge.Length > 0;
    public string LanguageName => LanguageCatalog.NameOf(Message.SourceLanguage);

    /// <summary>The dispatch seal (spec 09a, REQ-HRD-8): the language's glyph and wax, from Core.</summary>
    public string SealGlyph => LanguageSeal.For(Message.SourceLanguage).Glyph;

    /// <summary>The seal's wax, a <see cref="Envoy.Core.Ux.SealWax"/> name; the item templates map it to a <c>Wax.*</c> brush.</summary>
    public string SealWax => LanguageSeal.For(Message.SourceLanguage).Wax.ToString();

    public bool IsPending => Message.Status == IncomingStatus.Pending;
    /// <summary>The compact layout's trailing marks (REQ-OUXC-44).</summary>
    public string PendingMark => IsPending ? " …" : "";
    public string FailedMark => IsFailed ? " ⚠" : "";
    /// <summary>A real failure shows the ⚠ row; a cancelled translation (new match, exit) just shows the original (REQ-CHAT-47).</summary>
    public bool IsFailed => Message.Status == IncomingStatus.Failed && !Message.WasCancelled;
    public string? Error => Message.Error;
    public string Provider => Message.FromGlossary ? "slang glossary" : Message.ProviderId ?? "";

    public double Opacity
    {
        get => _opacity;
        set
        {
            if (Math.Abs(_opacity - value) < 0.01) return;
            _opacity = value;
            Notify();
        }
    }

    /// <summary>"Copied" or "Couldn't copy" for 1.5 s after the edit-mode Copy button (REQ-OUXC-45).</summary>
    public string CopyFeedback
    {
        get => _copyFeedback;
        private set
        {
            if (_copyFeedback == value) return;
            _copyFeedback = value;
            Notify();
        }
    }

    private RelayCommand? _copyCommand;
    public ICommand CopyCommand => _copyCommand ??= new RelayCommand(Copy);

    /// <summary>Puts sender, translation and original on the clipboard; the overlay copy is not typed into a game, so no OutgoingText.</summary>
    public void Copy()
    {
        CopyFeedback = ClipboardHelper.TrySetText(OverlayCopyText.Format(Message)) ? OverlayCopyText.Copied : OverlayCopyText.CopyFailed;
        _feedbackTimer?.Stop();
        _feedbackTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            CopyFeedback = "";
        }, Dispatcher.CurrentDispatcher);
    }

    public void Refresh()
    {
        foreach (var name in new[] { nameof(Main), nameof(Original), nameof(HasOriginal), nameof(LanguageBadge), nameof(HasLanguage), nameof(LanguageName), nameof(SealGlyph), nameof(SealWax), nameof(IsPending), nameof(IsFailed), nameof(PendingMark), nameof(FailedMark), nameof(Error), nameof(Provider) })
            Notify(name);
    }

    private static string Short(string code) => code switch
    {
        "zh-CN" => "中",
        "zh-TW" => "繁",
        "ko" => "한",
        "ja" => "日",
        _ => code.Split('-')[0].ToUpperInvariant(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
