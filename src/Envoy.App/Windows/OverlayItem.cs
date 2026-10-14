using System.ComponentModel;
using System.Runtime.CompilerServices;
using Envoy.Core.Languages;
using Envoy.Core.Pipeline;

namespace Envoy.App.Windows;

/// <summary>View model for one line in the overlay.</summary>
public sealed class OverlayItem(IncomingMessage message, bool showSender = true, bool showOriginal = true) : INotifyPropertyChanged
{
    private double _opacity = 1;

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

    public bool IsPending => Message.Status == IncomingStatus.Pending;
    public bool IsFailed => Message.Status == IncomingStatus.Failed;
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

    public void Refresh()
    {
        foreach (var name in new[] { nameof(Main), nameof(Original), nameof(HasOriginal), nameof(LanguageBadge), nameof(HasLanguage), nameof(LanguageName), nameof(IsPending), nameof(IsFailed), nameof(Error), nameof(Provider) })
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
