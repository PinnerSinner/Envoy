using Envoy.Core.Text;

namespace Envoy.Core.Updates;

/// <summary>
/// Every sentence the update UI shows (contract §8.7): whole sentences with named placeholders, second person, "what
/// happened, then what to do", no "Error:" prefix and never exception text.
/// </summary>
public static class UpdateText
{
    public const string AvailableTemplate = "Envoy {next} is available. You have {current}.";
    public const string AvailableAfterGameTemplate = "Envoy {next} will download after your game.";
    public const string DownloadingTemplate = "Downloading Envoy {next}… {percent}%";
    public const string ReadyTemplate = "Envoy {next} is ready. Restart to update; it takes a few seconds.";
    public const string UpdatedToTemplate = "Envoy was updated to {version}.";
    public const string UpToDateTemplate = "Up to date · checked {time}";
    public const string BetaAheadTemplate = "On a beta newer than the latest stable release · checked {time}";
    public const string LastCheckedTemplate = "Last checked {time}";
    public const string Checking = "Checking for updates…";
    public const string NeverChecked = "Not checked yet.";

    /// <summary>The tray item when an update is ready (REQ-DIST-24); one placeholder, {version}.</summary>
    public const string TrayRestart = "Restart to update to {version}";

    public const string PortableAutoDownloadTooltip = "The portable Envoy.exe can't update itself. Use the installer to get automatic updates.";

    public const string InstalledKind = "Installed";
    public const string PortableKind = "Portable";
    public const string StableChannel = "Stable";
    public const string BetaChannel = "Beta";

    // Labels for the same action in different places (UpdateAction.OpenReleasePage).
    public const string DownloadLabel = "Download";
    public const string ReleaseNotesLabel = "Release notes";
    public const string WhatsNewLabel = "What's new";

    public static string Available(ReleaseVersion next, ReleaseVersion current) =>
        TextTemplate.Format(AvailableTemplate, null, ("next", next), ("current", current));

    public static string AvailableAfterGame(ReleaseVersion next) =>
        TextTemplate.Format(AvailableAfterGameTemplate, null, ("next", next));

    public static string Downloading(ReleaseVersion next, int percent) =>
        TextTemplate.Format(DownloadingTemplate, null, ("next", next), ("percent", Math.Clamp(percent, 0, 100)));

    public static string Ready(ReleaseVersion next) => TextTemplate.Format(ReadyTemplate, null, ("next", next));

    public static string UpdatedTo(ReleaseVersion version) => TextTemplate.Format(UpdatedToTemplate, null, ("version", version));

    public static string TrayRestartItem(ReleaseVersion version) => TextTemplate.Format(TrayRestart, null, ("version", version));

    /// <summary>A failed update check, by kind.</summary>
    public static string CheckFailed(UpdateErrorKind kind) => kind switch
    {
        UpdateErrorKind.Offline => "Envoy couldn't reach GitHub to check for updates. Check your connection and try again.",
        UpdateErrorKind.RateLimited => "GitHub is limiting update checks right now. Try again in an hour.",
        UpdateErrorKind.BadResponse => "GitHub sent an answer Envoy couldn't read. Try again later.",
        UpdateErrorKind.DownloadFailed => "The update check didn't finish. Check your connection and try again.",
        UpdateErrorKind.UpdaterFailed => "Envoy couldn't start its updater. Try again, or get the installer from the release page.",
        _ => "Envoy couldn't check for updates. Try again later.",
    };

    /// <summary>A failed download, by kind.</summary>
    public static string DownloadFailed(UpdateErrorKind kind) => kind switch
    {
        UpdateErrorKind.Offline or UpdateErrorKind.DownloadFailed => "The update didn't finish downloading. Check your connection and try again.",
        UpdateErrorKind.RateLimited => "GitHub is limiting downloads right now. Try again in an hour.",
        UpdateErrorKind.BadResponse => "The downloaded update didn't match what GitHub listed, so Envoy set it aside. Try again later.",
        UpdateErrorKind.UpdaterFailed => "Envoy couldn't start its updater. Try again, or get the installer from the release page.",
        _ => "The update didn't download. Try again later.",
    };

    /// <summary>The About page's status line (REQ-DIST-25).</summary>
    public static string Status(UpdateSnapshot s, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(zone);
        string? time = s.LastChecked is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("t", System.Globalization.CultureInfo.CurrentCulture) : null;
        return s.State switch
        {
            UpdateState.Checking => Checking,
            UpdateState.Available when s.Available is { } a => s.DeferredForGame ? AvailableAfterGame(a.Version) : Available(a.Version, s.Current),
            UpdateState.Downloading when s.Available is { } a => Downloading(a.Version, s.Percent),
            UpdateState.ReadyToRestart when s.Available is { } a => Ready(a.Version),
            UpdateState.Failed => s.FailedWhileDownloading ? DownloadFailed(s.Error ?? UpdateErrorKind.Unknown) : CheckFailed(s.Error ?? UpdateErrorKind.Unknown),
            UpdateState.UpToDate when time is not null => s.Current.IsPrerelease && s.Channel == UpdateChannel.Stable
                ? TextTemplate.Format(BetaAheadTemplate, null, ("time", time))
                : TextTemplate.Format(UpToDateTemplate, null, ("time", time)),
            _ when time is not null => TextTemplate.Format(LastCheckedTemplate, null, ("time", time)),
            _ => NeverChecked,
        };
    }

    /// <summary>The default button label for an action ("" for <see cref="UpdateAction.None"/>).</summary>
    public static string ActionLabel(UpdateAction action) => action switch
    {
        UpdateAction.CheckNow => "Check for updates",
        UpdateAction.UpdateNow => "Update now",
        UpdateAction.RestartToUpdate => "Restart to update",
        UpdateAction.OpenReleasePage => ReleaseNotesLabel,
        UpdateAction.GetInstaller => "Get the installer",
        UpdateAction.Retry => "Retry",
        UpdateAction.Skip => "Skip this version",
        UpdateAction.DismissNotice => "Dismiss",
        _ => "",
    };

    public static string KindText(InstallKind kind) => kind == InstallKind.Installed ? InstalledKind : PortableKind;

    public static string ChannelText(UpdateChannel channel) => channel == UpdateChannel.Beta ? BetaChannel : StableChannel;
}
