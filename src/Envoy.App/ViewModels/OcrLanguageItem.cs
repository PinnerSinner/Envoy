using System.ComponentModel;
using Envoy.App.Services;

namespace Envoy.App.ViewModels;

public sealed class OcrLanguageItem(OcrLanguageStatus status, bool selected, bool isMine, Action changed) : INotifyPropertyChanged
{
    private bool _selected = selected;
    public string Code => status.Code;
    public string Name => status.Name;
    public bool Installed => status.Installed;
    public bool IsMine => isMine;

    /// <summary>Only installed languages can be toggled; your own language is always read.</summary>
    public bool CanToggle => Installed && !isMine;

    public string StatusText => isMine ? "your language · always on" : Installed ? "" : "not installed";

    public bool IsSelected
    {
        get => isMine || (_selected && Installed);
        set
        {
            if (!CanToggle) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            changed();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
