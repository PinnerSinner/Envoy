using Envoy.Core.Mvvm;

namespace Envoy.App.ViewModels;

/// <summary>Chronicle (About) › "Play the tale" (spec 09b, REQ-MOT-10).</summary>
public sealed partial class MainViewModel
{
    private RelayCommand? _playTale;

    /// <summary>Opens the Tale through <see cref="IShell.OpenTale"/>.</summary>
    public RelayCommand PlayTaleCommand => _playTale ??= new RelayCommand(_shell.OpenTale);
}
