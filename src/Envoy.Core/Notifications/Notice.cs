namespace Envoy.Core.Notifications;

/// <summary>The three advisory kinds (contract §2.2).</summary>
public enum NoticeKind
{
    OcrLanguageMissing,
    ProviderFailing,
    UpdateAvailable,
}

/// <summary>What <see cref="NoticeCenter.Submit"/> decided.</summary>
public enum NoticeDecision
{
    /// <summary>Show a balloon now.</summary>
    Show,

    /// <summary>Queued: the game is in front, the guide is open or the throttle refused. Re-offered by <see cref="NoticeCenter.TakeDeferred"/>.</summary>
    Defer,

    /// <summary>No balloon (dismissed, already announced, switched off or the update was announced before).</summary>
    Suppress,
}

/// <summary>What clicking a notice does; App's <c>RunNoticeAction</c> maps each id.</summary>
public static class NoticeActions
{
    public const string OpenCapture = "open-capture";
    public const string OpenTranslation = "open-translation";
    public const string UpdatePrimary = "update-primary";
}

/// <summary>
/// One advisory. <see cref="Title"/> and <see cref="Text"/> come only from <see cref="NoticeText"/> or 06's
/// <c>UpdateView.Headline</c>: never chat text, keys or exception messages (REQ-OUXA-16).
/// </summary>
/// <param name="Key">Identifies the condition: <c>ocr-missing:&lt;sorted codes&gt;</c>, <c>provider:&lt;id&gt;</c> or <c>update:&lt;version&gt;</c>.</param>
/// <param name="ActionId">A <see cref="NoticeActions"/> id.</param>
public sealed record Notice(NoticeKind Kind, string Key, string Title, string Text, string ActionId, bool IsWarning);
