using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.Core.Ux;

namespace Envoy.App.Windows;

/// <summary>
/// The Tale (spec 09b, REQ-MOT-9, 10): six chapters that explain Envoy in about a minute. Core <see cref="TalePlayer"/> holds
/// the playhead and Core <see cref="TaleText"/> every word; this window shows the chapter, replays its drawings and runs the
/// controls. At Full motion it plays by itself; under reduced motion or none it shows each chapter's finished picture and
/// moves on Next only. Opened by <c>IShell.OpenTale</c> (single instance, App owns it).
/// </summary>
public partial class TaleWindow : Window
{
    private readonly IReadOnlyList<TaleChapter> _chapters;
    private readonly Action? _setUp;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();

    /// <param name="replyHotkey">The reply combination chapter IV names ("" when none is set).</param>
    /// <param name="setUp">"Set up Envoy": opens the setup guide (the window closes first).</param>
    public TaleWindow(string? replyHotkey, Action? setUp)
    {
        InitializeComponent();
        _setUp = setUp;
        _chapters = TaleText.Chapters(replyHotkey);
        Player = new TalePlayer(autoPlay: Services.Motion.Level == MotionLevel.Full);
        BuildKeys(replyHotkey);
        BuildPromises();
        for (var i = 0; i < TalePlayer.ChapterCount; i++)
        {
            AutomationProperties.SetName(SealFor(i), _chapters[i].SealName);
            SealFor(i).ToolTip = _chapters[i].SealName;
        }
        PlayButton.Visibility = Player.AutoPlay ? Visibility.Visible : Visibility.Collapsed;
        StillHint.Visibility = Player.AutoPlay ? Visibility.Collapsed : Visibility.Visible;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher) { IsEnabled = false };
        Player.ChapterChanged += ShowChapter;
        Player.PlayingChanged += ShowPlaying;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            ShowChapter();
            ShowPlaying();
        };
        IsVisibleChanged += (_, _) => ShowPlaying();
    }

    /// <summary>The playhead (the smoke test steps it).</summary>
    public TalePlayer Player { get; }

    /// <summary>The caption's heading and sentence for the chapter shown (the smoke test reads them).</summary>
    public string CaptionHeadingText => CaptionHeading.Text;
    public string CaptionSentence => CaptionText.Text;

    /// <summary>The picture of chapter <paramref name="index"/> (0 to 5).</summary>
    internal FrameworkElement ChapterView(int index) => index switch
    {
        0 => Chapter0,
        1 => Chapter1,
        2 => Chapter2,
        3 => Chapter3,
        4 => Chapter4,
        5 => Chapter5,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, null),
    };

    internal Button SealFor(int index) => index switch
    {
        0 => Seal0,
        1 => Seal1,
        2 => Seal2,
        3 => Seal3,
        4 => Seal4,
        5 => Seal5,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, null),
    };

    /// <summary>Handles a Tale key the way the window does (the smoke test drives it); true when it was used.</summary>
    internal bool Press(TaleKey key)
    {
        var command = Player.CommandFor(key);
        if (command == TaleCommand.Close)
        {
            Close();
            return true;
        }
        Player.Run(command);
        return command != TaleCommand.None;
    }

    private void ShowChapter()
    {
        var index = Player.Chapter;
        for (var i = 0; i < TalePlayer.ChapterCount; i++)
        {
            ChapterView(i).Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            var seal = SealFor(i);
            seal.SetResourceReference(BackgroundProperty, i < index ? "Wax.Gules" : i == index ? "Wax.Or" : "Wax.Plain");
            seal.SetResourceReference(ForegroundProperty, i <= index ? "Wax.Text" : "Wax.Plain.Text");
        }
        var chapter = _chapters[index];
        ChapterCounter.Text = TaleText.ChapterOfText(index);
        CaptionHeading.Text = chapter.Heading;
        CaptionText.Text = chapter.Sentence;
        BackButton.IsEnabled = !Player.IsFirst;
        NextButton.IsEnabled = !Player.IsLast;
        RopeScale.ScaleX = Player.Progress;
        // Screen readers hear each new caption (REQ-MOT-9).
        (UIElementAutomationPeer.FromElement(CaptionText) ?? UIElementAutomationPeer.CreatePeerForElement(CaptionText))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

        var view = ChapterView(index);
        if (Player.IsPlaying) Services.Motion.Replay(view);
        else Services.Motion.Settle(view);
    }

    private void ShowPlaying()
    {
        var label = Player.IsPlaying ? TaleText.Pause : TaleText.Play;
        PlayButton.Content = label;
        AutomationProperties.SetName(PlayButton, label);
        if (Player.IsPlaying && IsVisible)
        {
            _clock.Restart();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _clock.Reset();
            // Paused: the chapter's picture finishes at once.
            if (!Player.IsPlaying && IsLoaded) Services.Motion.Settle(ChapterView(Player.Chapter));
        }
    }

    private void Tick()
    {
        var elapsed = _clock.Elapsed;
        _clock.Restart();
        Player.Advance(elapsed);
        RopeScale.ScaleX = Player.Progress;
    }

    /// <summary>One plaque per key of the reply hotkey, stamped in one after another (chapter IV).</summary>
    private void BuildKeys(string? replyHotkey)
    {
        var keys = string.IsNullOrWhiteSpace(replyHotkey)
            ? [TaleText.TrayKey]
            : replyHotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < keys.Length; i++)
        {
            var plaque = new Border
            {
                Style = (Style)FindResource("TaleKey"),
                Child = new TextBlock
                {
                    Text = keys[i],
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.Title"),
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ((TextBlock)plaque.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            Services.Motion.SetEntrance(plaque, EntranceKind.Stamp);
            Services.Motion.SetOnLoad(plaque, false);
            Services.Motion.SetDelay(plaque, TimeSpan.FromMilliseconds(600 + 150 * i));
            HotkeyKeys.Children.Add(plaque);
        }
    }

    /// <summary>The three promises of chapter VI, each sealed in turn.</summary>
    private void BuildPromises()
    {
        string[] wax = ["Wax.Gules", "Wax.Or", "Wax.Vert"];
        for (var i = 0; i < TaleText.Promises.Count; i++)
        {
            var seal = new ContentControl
            {
                Style = (Style)FindResource("WaxSeal"),
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 14, 0),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.Wordmark"),
                FontWeight = FontWeights.Normal,
                FontSize = 18,
                Content = "E",
                VerticalAlignment = VerticalAlignment.Top,
            };
            seal.SetResourceReference(BackgroundProperty, wax[i % wax.Length]);
            var delay = TimeSpan.FromMilliseconds(600 * (i + 1));
            Services.Motion.SetEntrance(seal, EntranceKind.Stamp);
            Services.Motion.SetOnLoad(seal, false);
            Services.Motion.SetDelay(seal, delay);
            DockPanel.SetDock(seal, Dock.Left);
            var text = new TextBlock { Text = TaleText.Promises[i], Style = (Style)FindResource("Text.Label"), FontSize = 19, TextWrapping = TextWrapping.Wrap };
            Services.Motion.SetEntrance(text, EntranceKind.Rise);
            Services.Motion.SetOnLoad(text, false);
            Services.Motion.SetDelay(text, delay + TimeSpan.FromMilliseconds(100));
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            row.Children.Add(seal);
            row.Children.Add(text);
            Promises.Children.Add(row);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            // Space on a focused button presses that button; elsewhere it plays or pauses.
            Key.Space when Keyboard.FocusedElement is ButtonBase => TaleKey.Other,
            Key.Space => TaleKey.Space,
            Key.Left => TaleKey.Left,
            Key.Right => TaleKey.Right,
            Key.Escape => TaleKey.Escape,
            _ => TaleKey.Other,
        };
        if (key != TaleKey.Other && Press(key)) e.Handled = true;
    }

    private void OnPlay(object sender, RoutedEventArgs e) => Player.TogglePlay();
    private void OnNext(object sender, RoutedEventArgs e) => Player.Next();
    private void OnBack(object sender, RoutedEventArgs e) => Player.Previous();
    private void OnSkip(object sender, RoutedEventArgs e) => Close();
    private void OnWatchAgain(object sender, RoutedEventArgs e) => Player.Restart();

    private void OnSeal(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
            Player.GoTo(index);
    }

    private void OnSetUp(object sender, RoutedEventArgs e)
    {
        Close();
        _setUp?.Invoke();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        Player.ChapterChanged -= ShowChapter;
        Player.PlayingChanged -= ShowPlaying;
        base.OnClosed(e);
    }
}
