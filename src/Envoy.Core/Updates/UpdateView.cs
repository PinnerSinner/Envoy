namespace Envoy.Core.Updates;

/// <summary>What a banner button, tray item or (later) balloon does. App runs it through <c>UpdateActions.Run</c>.</summary>
public enum UpdateAction
{
    None,
    CheckNow,
    UpdateNow,
    RestartToUpdate,
    OpenReleasePage,
    GetInstaller,
    Retry,
    Skip,
    DismissNotice,
}

/// <summary>The War Room update banner and the About status line, ready to bind (REQ-DIST-16, 25).</summary>
/// <param name="Notes">Plain-text release notes (<see cref="ReleaseNotesText.ToPlain"/>); "" for none.</param>
/// <param name="CanSkip">"Skip this version" is offered (REQ-DIST-22).</param>
public sealed record UpdateView(
    bool ShowBanner, string Headline, string Notes,
    UpdateAction Primary, string PrimaryLabel,
    UpdateAction Secondary, string SecondaryLabel,
    bool CanSkip, string StatusLine)
{
    public static readonly UpdateView Hidden = new(false, "", "", UpdateAction.None, "", UpdateAction.None, "", false, "");

    public bool HasNotes => Notes.Length > 0;
    public bool HasPrimary => Primary != UpdateAction.None;
    public bool HasSecondary => Secondary != UpdateAction.None;
}

/// <summary>Turns an <see cref="UpdateSnapshot"/> into the banner (spec 06, "UpdateView / UpdateViewBuilder").</summary>
public static class UpdateViewBuilder
{
    /// <param name="justUpdatedTo">The one-time "updated to" notice (REQ-DIST-23); shown only when no update banner is active.</param>
    /// <param name="zone">For the status line's time; null → local time.</param>
    public static UpdateView Build(UpdateSnapshot s, ReleaseVersion? justUpdatedTo = null, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        var status = UpdateText.Status(s, zone ?? TimeZoneInfo.Local);
        var notes = ReleaseNotesText.ToPlain(s.Available?.NotesMarkdown);
        switch (s.State)
        {
            case UpdateState.Failed:
                var kind = s.Error ?? UpdateErrorKind.Unknown;
                return new UpdateView(true, s.FailedWhileDownloading ? UpdateText.DownloadFailed(kind) : UpdateText.CheckFailed(kind), "",
                    UpdateAction.Retry, UpdateText.ActionLabel(UpdateAction.Retry), UpdateAction.None, "", false, status);
            case UpdateState.Available when s.Available is { } a && s.Kind == InstallKind.Portable:
                return new UpdateView(true, UpdateText.Available(a.Version, s.Current), notes,
                    UpdateAction.OpenReleasePage, UpdateText.DownloadLabel,
                    UpdateAction.GetInstaller, UpdateText.ActionLabel(UpdateAction.GetInstaller), true, status);
            case UpdateState.Available when s.Available is { } a:
                return new UpdateView(true, s.DeferredForGame ? UpdateText.AvailableAfterGame(a.Version) : UpdateText.Available(a.Version, s.Current), notes,
                    UpdateAction.UpdateNow, UpdateText.ActionLabel(UpdateAction.UpdateNow),
                    UpdateAction.OpenReleasePage, UpdateText.ReleaseNotesLabel, true, status);
            case UpdateState.Downloading when s.Available is { } a:
                return new UpdateView(true, UpdateText.Downloading(a.Version, s.Percent), notes,
                    UpdateAction.None, "", UpdateAction.OpenReleasePage, UpdateText.ReleaseNotesLabel, false, status);
            case UpdateState.ReadyToRestart when s.Available is { } a:
                return new UpdateView(true, UpdateText.Ready(a.Version), notes,
                    UpdateAction.RestartToUpdate, UpdateText.ActionLabel(UpdateAction.RestartToUpdate),
                    UpdateAction.OpenReleasePage, UpdateText.ReleaseNotesLabel, true, status);
        }
        if (justUpdatedTo is not null)
        {
            return new UpdateView(true, UpdateText.UpdatedTo(justUpdatedTo), "",
                UpdateAction.OpenReleasePage, UpdateText.WhatsNewLabel,
                UpdateAction.DismissNotice, UpdateText.ActionLabel(UpdateAction.DismissNotice), false, status);
        }
        return UpdateView.Hidden with { StatusLine = status };
    }
}
