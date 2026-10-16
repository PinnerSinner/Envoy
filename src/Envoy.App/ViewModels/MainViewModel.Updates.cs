using System.ComponentModel;
using Envoy.App.Services;
using Envoy.Core.Mvvm;
using Envoy.Core.Updates;

namespace Envoy.App.ViewModels;

/// <summary>
/// The War Room update banner, Appearance › Startup &amp; updates and About › Version &amp; updates (spec 06). The banner
/// binds <c>Host.UpdateView</c>; every button runs through <see cref="UpdateActions"/>.
/// </summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<Option> UpdateChannels { get; } =
    [
        new(UpdateChannel.Stable, "Stable (recommended)"),
        new(UpdateChannel.Beta, "Beta: early builds, may be rough"),
    ];

    /// <summary>Velopack-installed: background downloads are possible (the portable exe never updates itself).</summary>
    public bool IsInstalled => Host.Updates.Snapshot.Kind == InstallKind.Installed;

    /// <summary>Why the background-download box is off for the portable exe; null (no tooltip) when installed.</summary>
    public string? AutoDownloadTooltip => IsInstalled ? null : UpdateText.PortableAutoDownloadTooltip;

    public string InstallKindText => UpdateText.KindText(Host.Updates.Snapshot.Kind);

    public string ChannelText => UpdateText.ChannelText(Host.Updates.Snapshot.Channel);

    /// <summary>The banner's main button: Update now, Restart to update, Download, Retry or What's new.</summary>
    public RelayCommand UpdatePrimaryCommand { get; }

    /// <summary>The banner's second button: Release notes, Get the installer or Dismiss.</summary>
    public RelayCommand UpdateSecondaryCommand { get; }

    /// <summary>"Skip this version" (REQ-DIST-22).</summary>
    public RelayCommand SkipUpdateCommand { get; }

    /// <summary>About's "Check for updates": always works, also with automatic checks off and in safe mode (REQ-DIST-13).</summary>
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    private void RunUpdateAction(UpdateAction action) => UpdateActions.Run(action, Host, _shell, Host.Log.For("Updates"));

    private void AttachUpdates() => Host.PropertyChanged += OnHostUpdateChanged;

    private void DetachUpdates() => Host.PropertyChanged -= OnHostUpdateChanged;

    private void OnHostUpdateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppHost.UpdateView)) return;
        Notify(nameof(ChannelText));
        Notify(nameof(InstallKindText));
    }
}
