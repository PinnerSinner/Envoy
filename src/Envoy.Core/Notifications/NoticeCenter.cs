using Envoy.Core.Settings;

namespace Envoy.Core.Notifications;

/// <summary>
/// Decides Show, Defer or Suppress for advisory notices (REQ-OUXA-13, contract §2.2) and keeps the War Room's active
/// list, the deferral queue and the "announced" set. It asks 02's <see cref="NotificationThrottle"/> before every balloon
/// and keeps no counters of its own. UI thread only; not created in safe mode.
/// </summary>
public sealed class NoticeCenter
{
    private readonly AppSettings _settings;
    private readonly NotificationThrottle _throttle;
    private readonly List<Notice> _active = []; // newest first
    private readonly List<Notice> _deferred = []; // FIFO, one per key
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);

    public NoticeCenter(AppSettings settings, NotificationThrottle throttle)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
    }

    /// <summary>The War Room list, newest first. Never holds <see cref="NoticeKind.UpdateAvailable"/> (06's banner shows updates).</summary>
    public IReadOnlyList<Notice> Active => [.. _active];

    /// <summary>Notices waiting for a balloon, oldest first.</summary>
    public IReadOnlyList<Notice> Deferred => [.. _deferred];

    /// <summary>True while the setup guide is open: every advisory is deferred.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>Raised after <see cref="Active"/> changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// (1) Dismissed key → Suppress, not listed. (2) An update whose version is <c>Updates.LastAnnouncedVersion</c> →
    /// Suppress. (3) Non-update: added to (or replaced in) <see cref="Active"/>. (4) Already announced and still active →
    /// Suppress. (5) Balloons off, or this kind off → Suppress (it stays listed). (6) Held or the game in front → Defer.
    /// (7) The throttle refuses → Defer. (8) Show: marked announced; an update records its version. Deferred entries are
    /// one per key, latest wins.
    /// </summary>
    public NoticeDecision Submit(Notice notice, bool gameForeground)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (IsDismissed(notice.Key) || IsAnnouncedUpdate(notice)) return NoticeDecision.Suppress;
        if (notice.Kind != NoticeKind.UpdateAvailable) AddOrReplaceActive(notice);
        var decision = Decide(notice, gameForeground);
        if (decision == NoticeDecision.Defer) Enqueue(notice);
        else RemoveDeferred(notice.Key);
        return decision;
    }

    /// <summary>
    /// Re-runs steps 1–2 and 4–8 over the queue, oldest first. Suppressed entries leave the queue. Returns the first one
    /// to show (and removes it), or null.
    /// </summary>
    public Notice? TakeDeferred(bool gameForeground)
    {
        for (var i = 0; i < _deferred.Count;)
        {
            var notice = _deferred[i];
            var decision = IsDismissed(notice.Key) || IsAnnouncedUpdate(notice) ? NoticeDecision.Suppress : Decide(notice, gameForeground);
            if (decision == NoticeDecision.Defer)
            {
                i++;
                continue;
            }
            _deferred.RemoveAt(i);
            if (decision == NoticeDecision.Show) return notice;
        }
        return null;
    }

    /// <summary>Holds (true) or releases (false) every balloon; the guide holds while it is open.</summary>
    public void Hold(bool held) => IsHeld = held;

    /// <summary>The condition ended: drops the key from the list and the queue and re-arms it.</summary>
    public void Resolve(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ResolveWhere(k => string.Equals(k, key, StringComparison.Ordinal));
    }

    /// <summary><see cref="Resolve"/> for every key that matches.</summary>
    public void ResolveWhere(Func<string, bool> keyMatch)
    {
        ArgumentNullException.ThrowIfNull(keyMatch);
        _deferred.RemoveAll(n => keyMatch(n.Key));
        _announced.RemoveWhere(k => keyMatch(k));
        if (_active.RemoveAll(n => keyMatch(n.Key)) > 0) Changed?.Invoke();
    }

    /// <summary>"Don't remind me": the key is never shown or listed again (REQ-OUXA-20).</summary>
    public void Dismiss(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dismissed = _settings.Notifications.Dismissed;
        if (!dismissed.Contains(key, StringComparer.Ordinal)) dismissed.Add(key);
        Resolve(key);
    }

    private NoticeDecision Decide(Notice notice, bool gameForeground)
    {
        if (_announced.Contains(notice.Key)) return NoticeDecision.Suppress; // (4)
        if (!BalloonsOn(notice.Kind)) return NoticeDecision.Suppress; // (5)
        if (IsHeld || gameForeground) return NoticeDecision.Defer; // (6)
        if (!_throttle.TryShowBalloon(notice.Key)) return NoticeDecision.Defer; // (7)
        // (8)
        if (notice.Kind == NoticeKind.UpdateAvailable) _settings.Updates.LastAnnouncedVersion = VersionOf(notice);
        else _announced.Add(notice.Key);
        return NoticeDecision.Show;
    }

    private bool BalloonsOn(NoticeKind kind)
    {
        var n = _settings.Notifications;
        return n.Enabled && kind switch
        {
            NoticeKind.OcrLanguageMissing => n.OcrLanguageMissing,
            NoticeKind.ProviderFailing => n.ProviderFailing,
            NoticeKind.UpdateAvailable => n.UpdateAvailable,
            _ => true,
        };
    }

    private bool IsDismissed(string key) => _settings.Notifications.Dismissed.Contains(key, StringComparer.Ordinal);

    private bool IsAnnouncedUpdate(Notice notice) =>
        notice.Kind == NoticeKind.UpdateAvailable && _settings.Updates.LastAnnouncedVersion.Length > 0
        && string.Equals(VersionOf(notice), _settings.Updates.LastAnnouncedVersion, StringComparison.Ordinal);

    private static string VersionOf(Notice notice) =>
        notice.Key.StartsWith(NoticeFactory.UpdateKeyPrefix, StringComparison.Ordinal) ? notice.Key[NoticeFactory.UpdateKeyPrefix.Length..] : notice.Key;

    private void AddOrReplaceActive(Notice notice)
    {
        var i = _active.FindIndex(n => n.Key == notice.Key);
        if (i >= 0)
        {
            if (_active[i] == notice) return;
            _active[i] = notice;
        }
        else
        {
            _active.Insert(0, notice);
        }
        Changed?.Invoke();
    }

    private void Enqueue(Notice notice)
    {
        var i = _deferred.FindIndex(n => n.Key == notice.Key);
        if (i >= 0) _deferred[i] = notice;
        else _deferred.Add(notice);
    }

    private void RemoveDeferred(string key) => _deferred.RemoveAll(n => n.Key == key);
}
