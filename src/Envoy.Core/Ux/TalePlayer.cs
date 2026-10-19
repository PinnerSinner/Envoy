namespace Envoy.Core.Ux;

/// <summary>A key the Tale window handles (REQ-MOT-9).</summary>
public enum TaleKey
{
    Other,
    Space,
    Left,
    Right,
    Escape,
}

/// <summary>What a key press does in the Tale window.</summary>
public enum TaleCommand
{
    None,
    TogglePlay,
    Previous,
    Next,
    Close,
}

/// <summary>
/// The Tale's playhead (spec 09b, REQ-MOT-9, 10): six chapters of <see cref="ChapterLength"/> each. With motion it plays
/// by itself and stops at the end of chapter VI; under reduced motion or none it never plays by itself and moves on Next
/// only. UI thread; the window feeds it elapsed time from a timer.
/// </summary>
public sealed class TalePlayer
{
    public const int ChapterCount = 6;

    /// <summary>Six chapters of 10 s make the one-minute tour.</summary>
    public static readonly TimeSpan ChapterLength = TimeSpan.FromSeconds(10);

    private TimeSpan _elapsed;

    /// <param name="autoPlay">True at <see cref="MotionLevel.Full"/>: the tale plays by itself.</param>
    public TalePlayer(bool autoPlay)
    {
        AutoPlay = autoPlay;
        IsPlaying = autoPlay;
    }

    /// <summary>The tale can play by itself (Full motion). False: each chapter is a still and Next moves on.</summary>
    public bool AutoPlay { get; private set; }

    /// <summary>
    /// Follows a change of motion level while the tale is open (REQ-MOT-10). Turning autoplay off stops a playing tale
    /// for good, so it becomes a still tale; turning it on leaves the tale paused until Play.
    /// </summary>
    public void SetAutoPlay(bool autoPlay)
    {
        if (AutoPlay == autoPlay) return;
        AutoPlay = autoPlay;
        if (!autoPlay) SetPlaying(false);
    }

    /// <summary>0-based chapter shown now.</summary>
    public int Chapter { get; private set; }

    public bool IsPlaying { get; private set; }

    public bool IsFirst => Chapter == 0;

    public bool IsLast => Chapter == ChapterCount - 1;

    /// <summary>Time into the current chapter.</summary>
    public TimeSpan Elapsed => _elapsed;

    /// <summary>How far through the whole tale, 0 to 1 (the progress rope).</summary>
    public double Progress => Math.Clamp((Chapter + _elapsed / ChapterLength) / ChapterCount, 0, 1);

    /// <summary>Raised when <see cref="Chapter"/> changes or a chapter starts again.</summary>
    public event Action? ChapterChanged;

    /// <summary>Raised when <see cref="IsPlaying"/> changes.</summary>
    public event Action? PlayingChanged;

    /// <summary>Moves the playhead on by <paramref name="delta"/> while playing; at the end of a chapter the next one starts.</summary>
    public void Advance(TimeSpan delta)
    {
        if (!IsPlaying || delta <= TimeSpan.Zero) return;
        _elapsed += delta;
        if (_elapsed < ChapterLength) return;
        if (IsLast)
        {
            _elapsed = ChapterLength;
            SetPlaying(false);
            return;
        }
        GoTo(Chapter + 1);
    }

    public void Next()
    {
        if (!IsLast) GoTo(Chapter + 1);
    }

    public void Previous()
    {
        if (!IsFirst) GoTo(Chapter - 1);
    }

    /// <summary>Shows chapter <paramref name="index"/> from its start (clamped); a still tale stays still.</summary>
    public void GoTo(int index)
    {
        Chapter = Math.Clamp(index, 0, ChapterCount - 1);
        _elapsed = TimeSpan.Zero;
        if (AutoPlay) SetPlaying(true);
        ChapterChanged?.Invoke();
    }

    /// <summary>Starts again from chapter I ("Watch again").</summary>
    public void Restart() => GoTo(0);

    /// <summary>Play or pause. At the end of the tale, Play starts it again. Does nothing for a still tale.</summary>
    public void TogglePlay()
    {
        if (!AutoPlay) return;
        if (!IsPlaying && IsLast && _elapsed >= ChapterLength)
        {
            Restart();
            return;
        }
        SetPlaying(!IsPlaying);
    }

    /// <summary>Space plays or pauses (Next in a still tale), Left and Right move between chapters, Esc closes.</summary>
    public TaleCommand CommandFor(TaleKey key) => key switch
    {
        TaleKey.Space => AutoPlay ? TaleCommand.TogglePlay : TaleCommand.Next,
        TaleKey.Left => TaleCommand.Previous,
        TaleKey.Right => TaleCommand.Next,
        TaleKey.Escape => TaleCommand.Close,
        _ => TaleCommand.None,
    };

    /// <summary>Runs <paramref name="command"/> (Close is the window's job); true when it did something here.</summary>
    public bool Run(TaleCommand command)
    {
        switch (command)
        {
            case TaleCommand.TogglePlay:
                TogglePlay();
                return AutoPlay;
            case TaleCommand.Previous:
                var before = Chapter;
                Previous();
                return Chapter != before;
            case TaleCommand.Next:
                var was = Chapter;
                Next();
                return Chapter != was;
            default:
                return false;
        }
    }

    private void SetPlaying(bool playing)
    {
        if (IsPlaying == playing) return;
        IsPlaying = playing;
        PlayingChanged?.Invoke();
    }
}
