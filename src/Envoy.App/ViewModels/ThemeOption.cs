using System.ComponentModel;
using System.Windows.Media;

namespace Envoy.App.ViewModels;

public sealed class ThemeOption(string id, string name, string inspiration, Brush[] swatches, Func<string> current, Action<string> select) : INotifyPropertyChanged
{
    public string Id => id;
    public string Name => name;
    public string Inspiration => inspiration;
    public Brush[] Swatches => swatches;

    public bool IsSelected
    {
        get => current() == id;
        set { if (value) select(id); }
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    public event PropertyChangedEventHandler? PropertyChanged;
}
