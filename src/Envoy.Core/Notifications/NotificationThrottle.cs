using Envoy.Core.Diagnostics;

namespace Envoy.Core.Notifications;

/// <summary>The two kinds of notification (contract §2.2).</summary>
public enum NotificationClass
{
    /// <summary>Something went wrong (02): non-activating toast.</summary>
    Problem,

    /// <summary>Something worth knowing (07a): tray balloon and War Room list.</summary>
    Advisory,
}

/// <summary>A problem toast. <see cref="Title"/> and <see cref="Detail"/> come from <see cref="ProblemText"/>; never raw exception text.</summary>
/// <param name="Key">Throttle key: the same key is shown at most once per <see cref="NotificationThrottle.SameKeyWindow"/>.</param>
/// <param name="Severity">Error (red border, 10 s) or Warning (accent border, 6 s); also the level it is logged at.</param>
public sealed record ProblemReport(string Key, string Title, string Detail, LogLevel Severity = LogLevel.Error);

/// <summary>
/// The one notification throttle (contract §2.2). A single instance lives on <c>AppHost.Notifications</c>, and every
/// surface asks it before showing anything: the same key at most once per 2 min; at most
/// <see cref="MaxVisibleToasts"/> toasts at once (enforced by the App's <c>ToastService</c>); advisory balloons at least
/// 60 s apart and at most 5 per session. Thread-safe.
/// </summary>
public sealed class NotificationThrottle(TimeProvider? clock = null)
{
    public static readonly TimeSpan SameKeyWindow = TimeSpan.FromMinutes(2);
    public const int MaxVisibleToasts = 3;
    public static readonly TimeSpan AdvisoryBalloonSpacing = TimeSpan.FromSeconds(60);
    public const int MaxAdvisoryBalloonsPerSession = 5;

    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastBalloon;
    private int _balloons;

    /// <summary>Balloons shown this session.</summary>
    public int BalloonsShown
    {
        get
        {
            lock (_gate) return _balloons;
        }
    }

    /// <summary>Problem toast: false if the same key was shown within <see cref="SameKeyWindow"/>; records the show when true.</summary>
    public bool TryShowToast(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (SeenRecently(key, now)) return false;
            _lastShown[key] = now;
            return true;
        }
    }

    /// <summary>
    /// Advisory balloon (07a): the same-key window, plus at least <see cref="AdvisoryBalloonSpacing"/> since the last
    /// balloon, plus at most <see cref="MaxAdvisoryBalloonsPerSession"/> per session. Records the show when true.
    /// </summary>
    public bool TryShowBalloon(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (SeenRecently(key, now)) return false;
            if (_balloons >= MaxAdvisoryBalloonsPerSession) return false;
            if (_lastBalloon is { } last && now - last < AdvisoryBalloonSpacing) return false;
            _lastShown[key] = now;
            _lastBalloon = now;
            _balloons++;
            return true;
        }
    }

    private bool SeenRecently(string key, DateTimeOffset now) =>
        _lastShown.TryGetValue(key, out var last) && now - last < SameKeyWindow;
}
