using Envoy.Core.Settings;

namespace Envoy.Core.Updates;

/// <summary>
/// The immutable snapshot of the update settings (contract §7). AppHost builds it in <c>ApplySettings</c> and passes it
/// to <see cref="UpdateCoordinator.Configure"/>; background code never reads <see cref="AppSettings"/>.
/// </summary>
public sealed record UpdatePreferences(bool CheckAutomatically, bool AutoDownload, UpdateChannel Channel,
                                       ReleaseVersion? SkippedVersion, DateTimeOffset? LastCheckUtc)
{
    /// <summary>Copies the values; an empty or unreadable <c>SkippedVersion</c> becomes null.</summary>
    public static UpdatePreferences From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var u = settings.Updates ?? new UpdateSettings();
        return new UpdatePreferences(settings.CheckForUpdates, u.AutoDownload, u.Channel,
            ReleaseVersion.TryParse(u.SkippedVersion, out var skipped) ? skipped : null, u.LastCheckUtc);
    }
}
