using System.ComponentModel;
using System.Windows.Media;

namespace Envoy.App.ViewModels;

/// <summary>A livery card on the Appearance page: its shield field, name and description.</summary>
public sealed class ThemeOption(string id, string name, string inspiration, Brush shield, Func<string> current, Action<string> select) : INotifyPropertyChanged
{
    public string Id => id;
    public string Name => name;
    public string Inspiration => inspiration;
    public Brush Field => shield;

    public bool IsSelected
    {
        get => current() == id;
        set { if (value) select(id); }
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    public event PropertyChangedEventHandler? PropertyChanged;
}
