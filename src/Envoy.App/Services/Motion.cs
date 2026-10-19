using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Envoy.Core.Ux;
using CoreMotion = Envoy.Core.Ux.Motion;

namespace Envoy.App.Services;

/// <summary>A hover and press response for a control template part (<see cref="Motion.ReactProperty"/>).</summary>
public enum ReactKind
{
    None,

    /// <summary>A plaque button or a phrase ribbon: lifts on hover, presses down on click.</summary>
    Lift,

    /// <summary>A wax-seal button: tilts and grows a little on hover, presses in on click.</summary>
    Wax,

    /// <summary>A banner tab: slides out on hover; the checked tab stands out and sways (Full only).</summary>
    Tab,

    /// <summary>A livery card: lifts on hover, rises further when chosen.</summary>
    Card,
}

/// <summary>A one-off entrance (<see cref="Motion.EntranceProperty"/>), played on first load or by <see cref="Motion.Replay"/>.</summary>
public enum EntranceKind
{
    None,

    /// <summary>Opacity from 0.</summary>
    Fade,

    /// <summary>Up from 16 px below, fading in.</summary>
    Rise,

    /// <summary>A dispatch unrolling from its top rod.</summary>
    Unfurl,

    /// <summary>A scroll opening sideways from its middle.</summary>
    UnrollX,

    /// <summary>A seal stamped: from <see cref="CoreMotion.StampFrom"/> times its size, tilted, settling with an overshoot.</summary>
    Stamp,

    /// <summary>Text written left to right (an opacity-mask sweep).</summary>
    Ink,

    /// <summary>A drawing stroke drawn along its length (StrokeDashOffset); a Shape only.</summary>
    Draw,
}

/// <summary>
/// The one animation helper (spec 09b, REQ-MOT-1, 2). Every animation in the app starts here, and nothing else calls
/// <c>BeginAnimation</c> (typecheck R16). It reads <see cref="Level"/>: under <see cref="MotionLevel.Off"/> every call
/// shows its end state at once, under <see cref="MotionLevel.Reduced"/> only fades run, and loops run at Full only.
/// Animations change only Opacity, RenderTransform parts, a Clip's transform, an OpacityMask's stops or
/// StrokeDashOffset (<see cref="Allowed"/>); nothing animates layout. UI thread only.
/// </summary>
public static class Motion
{
    private static MotionLevel _level = MotionLevel.Full;

    /// <summary>The resolved level (<c>AppHost.ApplyMotion</c>, from <see cref="MotionPolicy.Resolve"/>).</summary>
    public static MotionLevel Level => _level;

    /// <summary>Raised on the UI thread when <see cref="Level"/> changes; loops start or stop.</summary>
    public static event Action? LevelChanged;

    /// <summary>Sets the level; running loops restart or stop to match.</summary>
    public static void Apply(MotionLevel level)
    {
        if (_level == level) return;
        _level = level;
        LevelChanged?.Invoke();
    }

    /// <summary>Whether an animation of <paramref name="kind"/> runs now.</summary>
    public static bool Runs(MotionKind kind) => CoreMotion.Runs(_level, kind);

    /// <summary>The only properties an animation may change (REQ-MOT-2).</summary>
    public static IReadOnlyList<DependencyProperty> Allowed { get; } =
    [
        UIElement.OpacityProperty,
        ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty,
        TranslateTransform.XProperty, TranslateTransform.YProperty,
        RotateTransform.AngleProperty, SkewTransform.AngleXProperty,
        GradientStop.OffsetProperty, // an OpacityMask's sweep
        Shape.StrokeDashOffsetProperty,
    ];

    // ================= The primitive =================

    /// <summary>
    /// Animates <paramref name="property"/> of <paramref name="target"/> to <paramref name="to"/> over
    /// <paramref name="duration"/> after <paramref name="delay"/>, starting at <paramref name="from"/> (or where it is now).
    /// With <paramref name="keep"/> the value stays at <paramref name="to"/> afterwards (for parts this helper owns);
    /// without it the animation ends on the property's own value (a binding or style), so <paramref name="to"/> should be
    /// that value. When the kind does not run at <see cref="Level"/>, the end state shows at once.
    /// </summary>
    public static void Animate(DependencyObject target, DependencyProperty property, double to, TimeSpan duration, MotionKind kind,
        double? from = null, TimeSpan delay = default, IEasingFunction? ease = null, bool keep = true, Action? completed = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Allowed.Contains(property)) throw new ArgumentException($"{property.Name} is not an animatable property (REQ-MOT-2).", nameof(property));
        if (target is not IAnimatable animatable) return;
        var start = from ?? (double)target.GetValue(property);
        if (keep) target.SetValue(property, to);
        var length = CoreMotion.DurationFor(_level, kind, duration);
        if (length == TimeSpan.Zero)
        {
            animatable.BeginAnimation(property, null);
            completed?.Invoke();
            return;
        }
        var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero) frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(delay)));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + length), ease));
        if (completed is not null) frames.Completed += (_, _) => completed();
        else frames.Freeze();
        animatable.BeginAnimation(property, frames, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// A repeating decoration through <paramref name="values"/> (the first value is also the last), one cycle per
    /// <paramref name="period"/>, starting <paramref name="offset"/> in. Full only; otherwise the property rests at its own value.
    /// </summary>
    public static void Loop(DependencyObject target, DependencyProperty property, IReadOnlyList<double> values, TimeSpan period, TimeSpan offset = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Allowed.Contains(property)) throw new ArgumentException($"{property.Name} is not an animatable property (REQ-MOT-2).", nameof(property));
        if (target is not IAnimatable animatable) return;
        if (!Runs(MotionKind.Loop) || values.Count < 2 || period <= TimeSpan.Zero)
        {
            animatable.BeginAnimation(property, null);
            return;
        }
        var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = period, BeginTime = offset };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        for (var i = 0; i <= values.Count; i++)
        {
            var at = TimeSpan.FromTicks(period.Ticks * i / values.Count);
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(values[i % values.Count], KeyTime.FromTimeSpan(at), ease));
        }
        frames.Freeze();
        animatable.BeginAnimation(property, frames, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// A repeating one-way run through <paramref name="values"/> (spread evenly, the last at the end of the period), that
    /// starts over from the first value each cycle: a particle rising and fading. Full only, like <see cref="Loop"/>.
    /// </summary>
    public static void Cycle(DependencyObject target, DependencyProperty property, IReadOnlyList<double> values, TimeSpan period, TimeSpan offset = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Allowed.Contains(property)) throw new ArgumentException($"{property.Name} is not an animatable property (REQ-MOT-2).", nameof(property));
        if (target is not IAnimatable animatable) return;
        if (!Runs(MotionKind.Loop) || values.Count < 2 || period <= TimeSpan.Zero)
        {
            animatable.BeginAnimation(property, null);
            return;
        }
        var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = period, BeginTime = offset };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        for (var i = 0; i < values.Count; i++)
        {
            var at = TimeSpan.FromTicks(period.Ticks * i / (values.Count - 1));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(at), ease));
        }
        frames.Freeze();
        animatable.BeginAnimation(property, frames, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Stops any animation of <paramref name="property"/>; it shows its own value again.</summary>
    public static void Stop(DependencyObject target, DependencyProperty property)
    {
        if (target is IAnimatable animatable) animatable.BeginAnimation(property, null);
    }

    // ================= Transforms this helper owns =================

    /// <summary>The scale, skew, rotate and translate parts of an element's render transform.</summary>
    public sealed class Parts
    {
        internal Parts()
        {
            Group = new TransformGroup { Children = { Scale, Skew, Rotate, Translate } };
        }

        public ScaleTransform Scale { get; } = new();
        public SkewTransform Skew { get; } = new();
        public RotateTransform Rotate { get; } = new();
        public TranslateTransform Translate { get; } = new();
        internal TransformGroup Group { get; }

        /// <summary>Back to identity, with no animation running.</summary>
        public void Reset()
        {
            foreach (var (part, property, rest) in new (DependencyObject, DependencyProperty, double)[]
                     {
                         (Scale, ScaleTransform.ScaleXProperty, 1), (Scale, ScaleTransform.ScaleYProperty, 1),
                         (Skew, SkewTransform.AngleXProperty, 0), (Rotate, RotateTransform.AngleProperty, 0),
                         (Translate, TranslateTransform.XProperty, 0), (Translate, TranslateTransform.YProperty, 0),
                     })
            {
                Stop(part, property);
                part.SetValue(property, rest);
            }
        }

        /// <summary>The parts' current values, animated or not (the smoke test compares two samples).</summary>
        public string Snapshot =>
            FormattableString.Invariant($"{Scale.ScaleX:0.###},{Scale.ScaleY:0.###},{Skew.AngleX:0.###},{Rotate.Angle:0.###},{Translate.X:0.###},{Translate.Y:0.###}");

        /// <summary>True when nothing is moved, scaled or turned (the smoke test's settle check).</summary>
        public bool IsIdentity =>
            Scale.ScaleX == 1 && Scale.ScaleY == 1 && Skew.AngleX == 0 && Rotate.Angle == 0 && Translate.X == 0 && Translate.Y == 0;
    }

    private static readonly DependencyProperty PartsProperty =
        DependencyProperty.RegisterAttached("Parts", typeof(Parts), typeof(Motion), new PropertyMetadata(null));

    /// <summary>The element's own transform parts, created (replacing its render transform) on first use.</summary>
    public static Parts TransformOf(UIElement element, Point? origin = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.GetValue(PartsProperty) is not Parts parts)
        {
            parts = new Parts();
            element.SetValue(PartsProperty, parts);
            element.RenderTransform = parts.Group;
        }
        if (origin is { } o) element.RenderTransformOrigin = o;
        return parts;
    }

    /// <summary>The parts if this helper ever moved the element, else null.</summary>
    public static Parts? ExistingTransformOf(UIElement element) => element.GetValue(PartsProperty) as Parts;

    // ================= Building blocks =================

    private static IEasingFunction Out => new CubicEase { EasingMode = EasingMode.EaseOut };
    private static IEasingFunction In => new CubicEase { EasingMode = EasingMode.EaseIn };
    private static IEasingFunction Back(double amplitude = 0.4) => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude };

    /// <summary>Fades in from 0 to the element's own opacity (Reduced keeps it).</summary>
    public static void FadeIn(UIElement element, TimeSpan duration, TimeSpan delay = default)
    {
        Stop(element, UIElement.OpacityProperty);
        Animate(element, UIElement.OpacityProperty, element.Opacity, duration, MotionKind.Fade, from: 0, delay: delay, ease: Out, keep: false);
    }

    /// <summary>Plays a one-off entrance now (see <see cref="EntranceKind"/>).</summary>
    public static void Enter(UIElement element, EntranceKind kind, TimeSpan delay = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        switch (kind)
        {
            case EntranceKind.Fade:
                FadeIn(element, CoreMotion.Unroll, delay);
                break;
            case EntranceKind.Rise:
                FadeIn(element, CoreMotion.Unroll, delay);
                Animate(TransformOf(element).Translate, TranslateTransform.YProperty, 0, CoreMotion.Unroll, MotionKind.Move, from: 16, delay: delay, ease: Out);
                break;
            case EntranceKind.Unfurl:
                FadeIn(element, CoreMotion.Quick, delay);
                Animate(TransformOf(element, new Point(0.5, 0)).Scale, ScaleTransform.ScaleYProperty, 1, CoreMotion.Unroll, MotionKind.Move, from: 0.05,
                    delay: delay, ease: Back(0.3));
                break;
            case EntranceKind.UnrollX:
                FadeIn(element, CoreMotion.Quick, delay);
                Animate(TransformOf(element, new Point(0.5, 0.5)).Scale, ScaleTransform.ScaleXProperty, 1, CoreMotion.Unroll, MotionKind.Move, from: 0.05,
                    delay: delay, ease: Out);
                break;
            case EntranceKind.Stamp:
                Stamp(element, delay);
                break;
            case EntranceKind.Ink:
                InkWrite(element, delay);
                break;
            case EntranceKind.Draw:
                if (element is Shape shape) Draw(shape, CoreMotion.Ink, delay);
                break;
        }
    }

    /// <summary>A seal stamped (REQ-MOT-3, 5, 7): from 1.8 times its size and tilted, settling at 1 with a slight overshoot.</summary>
    public static void Stamp(UIElement element, TimeSpan delay = default)
    {
        var parts = TransformOf(element, new Point(0.5, 0.5));
        FadeIn(element, CoreMotion.Quick, delay);
        Animate(parts.Scale, ScaleTransform.ScaleXProperty, 1, CoreMotion.Snap, MotionKind.Move, from: CoreMotion.StampFrom, delay: delay, ease: Back());
        Animate(parts.Scale, ScaleTransform.ScaleYProperty, 1, CoreMotion.Snap, MotionKind.Move, from: CoreMotion.StampFrom, delay: delay, ease: Back());
        Animate(parts.Rotate, RotateTransform.AngleProperty, 0, CoreMotion.Snap, MotionKind.Move, from: -20, delay: delay, ease: Back());
    }

    /// <summary>
    /// Text that writes itself left to right (REQ-MOT-6): an opacity-mask sweep over <see cref="CoreMotion.Ink"/>. Under
    /// Reduced it fades in; under Off it simply shows.
    /// </summary>
    public static void InkWrite(UIElement element, TimeSpan delay = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Runs(MotionKind.Move))
        {
            element.ClearValue(UIElement.OpacityMaskProperty);
            FadeIn(element, CoreMotion.Snap, delay);
            return;
        }
        var edge = new GradientStop(Colors.Black, 0);
        var fade = new GradientStop(Colors.Transparent, 0.001);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(edge);
        mask.GradientStops.Add(fade);
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        element.OpacityMask = mask;
        Animate(edge, GradientStop.OffsetProperty, 1, CoreMotion.Ink, MotionKind.Move, from: 0, delay: delay);
        Animate(fade, GradientStop.OffsetProperty, 1.12, CoreMotion.Ink, MotionKind.Move, from: 0.001, delay: delay,
            completed: () => { if (ReferenceEquals(element.OpacityMask, mask)) element.ClearValue(UIElement.OpacityMaskProperty); });
    }

    /// <summary>A stroke drawn along its length (StrokeDashOffset). Full only; otherwise the finished stroke shows.</summary>
    public static void Draw(Shape shape, TimeSpan duration, TimeSpan delay = default)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Stop(shape, Shape.StrokeDashOffsetProperty);
        var geometry = shape is System.Windows.Shapes.Path path ? path.Data : shape.RenderedGeometry;
        var thickness = Math.Max(0.5, shape.StrokeThickness);
        var dash = geometry is null ? 0 : LengthOf(geometry) / thickness + 1;
        if (!Runs(MotionKind.Move) || dash <= 1)
        {
            shape.ClearValue(Shape.StrokeDashArrayProperty);
            shape.ClearValue(Shape.StrokeDashOffsetProperty);
            return;
        }
        shape.StrokeDashArray = [dash, dash];
        Animate(shape, Shape.StrokeDashOffsetProperty, 0, duration, MotionKind.Move, from: dash, delay: delay,
            ease: new CubicEase { EasingMode = EasingMode.EaseInOut },
            completed: () =>
            {
                shape.ClearValue(Shape.StrokeDashArrayProperty);
                shape.ClearValue(Shape.StrokeDashOffsetProperty);
            });
    }

    /// <summary>Draws <paramref name="shapes"/> one after another within <paramref name="total"/>, each for a share of it by length.</summary>
    public static void DrawInTurn(IReadOnlyList<Shape> shapes, TimeSpan total, TimeSpan delay = default)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        var lengths = shapes.Select(s => s is System.Windows.Shapes.Path { Data: { } g } ? LengthOf(g) : 0).ToList();
        var sum = lengths.Sum();
        var at = delay;
        for (var i = 0; i < shapes.Count; i++)
        {
            var share = sum > 0 ? TimeSpan.FromTicks((long)(total.Ticks * (lengths[i] / sum))) : TimeSpan.Zero;
            Draw(shapes[i], share > TimeSpan.FromMilliseconds(60) ? share : TimeSpan.FromMilliseconds(60), at);
            at += share;
        }
    }

    /// <summary>One <see cref="PathGeometry"/> per figure of <paramref name="geometry"/>, so a drawing can be drawn stroke by stroke.</summary>
    public static IReadOnlyList<Geometry> StrokesOf(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var path = PathGeometry.CreateFromGeometry(geometry);
        var strokes = new List<Geometry>(path.Figures.Count);
        foreach (var figure in path.Figures)
        {
            var single = new PathGeometry([figure.Clone()]);
            single.Freeze();
            strokes.Add(single);
        }
        return strokes;
    }

    /// <summary>The length of a geometry's outline (its flattened segments).</summary>
    public static double LengthOf(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var flat = geometry.GetFlattenedPathGeometry();
        double length = 0;
        foreach (var figure in flat.Figures)
        {
            var at = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                IEnumerable<Point> points = segment switch
                {
                    LineSegment line => [line.Point],
                    PolyLineSegment poly => poly.Points,
                    _ => [],
                };
                foreach (var p in points)
                {
                    length += (p - at).Length;
                    at = p;
                }
            }
            if (figure.IsClosed) length += (figure.StartPoint - at).Length;
        }
        return length;
    }

    /// <summary>
    /// The charter unrolls from the top (REQ-MOT-4): a clip opens downwards over <see cref="CoreMotion.Unroll"/> while the
    /// page settles with a back ease. Reduced fades it in instead.
    /// </summary>
    public static void UnrollDown(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Runs(MotionKind.Move))
        {
            FadeIn(element, CoreMotion.Quick);
            return;
        }
        var parts = TransformOf(element, new Point(0.5, 0));
        Animate(parts.Scale, ScaleTransform.ScaleYProperty, 1, CoreMotion.Unroll, MotionKind.Move, from: 0.96, ease: Back(0.5));
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var reveal = new ScaleTransform(1, 0);
        var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight)) { Transform = reveal };
        element.Clip = clip;
        Animate(reveal, ScaleTransform.ScaleYProperty, 1, CoreMotion.Unroll, MotionKind.Move, from: 0, ease: Out,
            completed: () => { if (ReferenceEquals(element.Clip, clip)) element.ClearValue(UIElement.ClipProperty); });
    }

    /// <summary>A page that turns in from the right (the setup guide's steps, REQ-MOT-7); Reduced fades it in.</summary>
    public static void TurnIn(FrameworkElement element, double direction = 1)
    {
        ArgumentNullException.ThrowIfNull(element);
        FadeIn(element, CoreMotion.Snap);
        Animate(TransformOf(element).Translate, TranslateTransform.XProperty, 0, CoreMotion.Snap, MotionKind.Move,
            from: CoreMotion.PageTurn * Math.Sign(direction == 0 ? 1 : direction), ease: Out);
    }

    /// <summary>
    /// The reply desk folds toward the chat and fades (REQ-MOT-6); the task ends when it has. Reduced fades it out; Off
    /// ends at once. <see cref="Unfold"/> puts it back before the desk opens again. The task always ends: a second fold or
    /// an <see cref="Unfold"/> before this one finishes ends it too, so an awaiting caller is never left waiting.
    /// </summary>
    public static Task FoldAwayAsync(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        EndFold(element);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        element.SetValue(FoldProperty, done);
        var parts = TransformOf(element, new Point(0.5, 1));
        Animate(parts.Scale, ScaleTransform.ScaleYProperty, 0.08, CoreMotion.Snap, MotionKind.Move, ease: In);
        Animate(parts.Scale, ScaleTransform.ScaleXProperty, 0.7, CoreMotion.Snap, MotionKind.Move, ease: In);
        Animate(parts.Translate, TranslateTransform.YProperty, 40, CoreMotion.Snap, MotionKind.Move, ease: In);
        Animate(element, UIElement.OpacityProperty, 0, Runs(MotionKind.Move) ? CoreMotion.Snap : CoreMotion.Quick, MotionKind.Fade,
            ease: In, completed: () =>
            {
                if (ReferenceEquals(element.GetValue(FoldProperty), done)) element.ClearValue(FoldProperty);
                done.TrySetResult();
            });
        return done.Task;
    }

    /// <summary>The task of a fold still running on an element.</summary>
    private static readonly DependencyProperty FoldProperty =
        DependencyProperty.RegisterAttached("Fold", typeof(TaskCompletionSource), typeof(Motion), new PropertyMetadata(null));

    private static void EndFold(FrameworkElement element)
    {
        if (element.GetValue(FoldProperty) is not TaskCompletionSource pending) return;
        element.ClearValue(FoldProperty);
        pending.TrySetResult();
    }

    /// <summary>Undoes <see cref="FoldAwayAsync"/>; a fold still running ends its task.</summary>
    public static void Unfold(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        EndFold(element);
        ExistingTransformOf(element)?.Reset();
        Stop(element, UIElement.OpacityProperty);
        element.Opacity = 1;
    }

    // ================= Loops that live with an element =================

    /// <summary>
    /// A loop that runs while its element is loaded, visible, its window is not minimised (a minimised window keeps
    /// IsVisible) and the level is Full.
    /// </summary>
    private sealed class LoopLease
    {
        private readonly FrameworkElement _owner;
        private readonly Action _start;
        private readonly Action _stop;
        private Window? _window;
        private bool _running;
        private bool _wanted = true;

        public LoopLease(FrameworkElement owner, Action start, Action stop)
        {
            _owner = owner;
            _start = start;
            _stop = stop;
            owner.Loaded += (_, _) => { Attach(); Update(); };
            owner.Unloaded += (_, _) => { Detach(); Halt(); };
            owner.IsVisibleChanged += (_, _) => Update();
            if (owner.IsLoaded)
            {
                Attach();
                Update();
            }
        }

        private void Attach()
        {
            Detach();
            LevelChanged += Update;
            _window = Window.GetWindow(_owner);
            if (_window is not null) _window.StateChanged += OnWindowState;
        }

        private void Detach()
        {
            LevelChanged -= Update;
            if (_window is not null) _window.StateChanged -= OnWindowState;
            _window = null;
        }

        private void OnWindowState(object? sender, EventArgs e) => Update();

        /// <summary>Sets whether the loop should run; a change starts or stops it, and <paramref name="restart"/> restarts a running one.</summary>
        public void Want(bool wanted, bool restart)
        {
            if (_wanted == wanted && !restart) return;
            _wanted = wanted;
            Update();
        }

        public void Update()
        {
            var run = _wanted && _owner.IsLoaded && _owner.IsVisible && _window is not { WindowState: WindowState.Minimized } && Runs(MotionKind.Loop);
            if (run)
            {
                _start(); // restart: the loop's shape may depend on state that just changed
                _running = true;
            }
            else
            {
                Halt();
            }
        }

        private void Halt()
        {
            if (!_running) return;
            _running = false;
            _stop();
        }
    }

    private static readonly DependencyProperty LeaseProperty =
        DependencyProperty.RegisterAttached("Lease", typeof(LoopLease), typeof(Motion), new PropertyMetadata(null));

    /// <summary>
    /// Runs <paramref name="start"/> while <paramref name="owner"/> is loaded, visible and the level is Full, and
    /// <paramref name="stop"/> otherwise, so hidden windows and pages pay nothing. One loop per owner.
    /// </summary>
    public static void KeepLooping(FrameworkElement owner, Action start, Action stop)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.GetValue(LeaseProperty) is LoopLease) return;
        owner.SetValue(LeaseProperty, new LoopLease(owner, start, stop));
    }

    /// <summary>
    /// Whether an owner's loop should run (a checked tab, a burning candle). A change starts or stops it;
    /// <paramref name="restart"/> also restarts a running loop whose shape depends on state that changed.
    /// </summary>
    public static void RefreshLoop(FrameworkElement owner, bool wanted, bool restart = false)
    {
        if (owner.GetValue(LeaseProperty) is LoopLease lease) lease.Want(wanted, restart);
    }

    // ================= Attached behaviours for XAML =================

    /// <summary>
    /// <c>s:Motion.React</c> on a template part: hover and press responses for its templated button (REQ-MOT-3). The part's
    /// render transform belongs to this helper; template triggers must not set it.
    /// </summary>
    public static readonly DependencyProperty ReactProperty =
        DependencyProperty.RegisterAttached("React", typeof(ReactKind), typeof(Motion), new PropertyMetadata(ReactKind.None, OnReactChanged));

    public static ReactKind GetReact(DependencyObject d) => (ReactKind)d.GetValue(ReactProperty);
    public static void SetReact(DependencyObject d, ReactKind value) => d.SetValue(ReactProperty, value);

    private static readonly DependencyProperty PressedProperty =
        DependencyProperty.RegisterAttached("Pressed", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    private static void OnReactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement part || (ReactKind)e.NewValue == ReactKind.None) return;
        WhenTemplated(part, control => HookReact(part, control));
    }

    /// <summary>Runs <paramref name="attach"/> once with the part's templated parent, as soon as it is known.</summary>
    private static void WhenTemplated(FrameworkElement part, Action<Control> attach)
    {
        var done = false;
        void TryAttach()
        {
            if (done || part.TemplatedParent is not Control control) return;
            done = true;
            attach(control);
        }
        TryAttach();
        if (!done) part.Loaded += (_, _) => TryAttach();
    }

    private static void HookReact(FrameworkElement part, Control control)
    {
        var kind = GetReact(part);
        var origin = kind switch
        {
            ReactKind.Wax => new Point(0.5, 0.5),
            ReactKind.Tab => new Point(0, 0.5),
            _ => new Point(0.5, 0.5),
        };
        TransformOf(part, origin);
        void Update(bool animate) => ApplyReact(part, control, kind, animate);
        control.MouseEnter += (_, _) => Update(true);
        control.MouseLeave += (_, _) =>
        {
            control.SetValue(PressedProperty, false);
            Update(true);
        };
        control.PreviewMouseLeftButtonDown += (_, _) =>
        {
            control.SetValue(PressedProperty, true);
            Update(true);
        };
        control.PreviewMouseLeftButtonUp += (_, _) =>
        {
            control.SetValue(PressedProperty, false);
            Update(true);
        };
        control.LostMouseCapture += (_, _) =>
        {
            if (!(bool)control.GetValue(PressedProperty)) return;
            control.SetValue(PressedProperty, false);
            Update(true);
        };
        if (control is ToggleButton toggle)
        {
            toggle.Checked += (_, _) => Update(true);
            toggle.Unchecked += (_, _) => Update(true);
        }
        if (kind == ReactKind.Tab)
        {
            var parts = TransformOf(part);
            KeepLooping(part,
                () => Loop(parts.Rotate, RotateTransform.AngleProperty, [0, 0.7, 0, -0.5], TimeSpan.FromSeconds(3.4)),
                () => Stop(parts.Rotate, RotateTransform.AngleProperty));
            RefreshLoop(part, control is ToggleButton { IsChecked: true });
        }
        Update(false);
    }

    private static void ApplyReact(FrameworkElement part, Control control, ReactKind kind, bool animate)
    {
        var parts = TransformOf(part);
        var hover = control.IsMouseOver && control.IsEnabled;
        var pressed = (bool)control.GetValue(PressedProperty) && hover;
        var on = control is ToggleButton { IsChecked: true };
        var time = animate ? CoreMotion.Quick : TimeSpan.Zero;
        switch (kind)
        {
            case ReactKind.Lift:
                Animate(parts.Translate, TranslateTransform.YProperty, pressed ? CoreMotion.Lift / 2 : hover ? -CoreMotion.Lift : 0, time, MotionKind.Move, ease: Out);
                break;
            case ReactKind.Card:
                Animate(parts.Translate, TranslateTransform.YProperty, on ? -6 : hover ? -3 : 0, animate ? CoreMotion.Snap : time, MotionKind.Move, ease: Back());
                break;
            case ReactKind.Wax:
                var scale = pressed ? 0.95 : hover ? 1.04 : 1;
                Animate(parts.Scale, ScaleTransform.ScaleXProperty, scale, time, MotionKind.Move, ease: Out);
                Animate(parts.Scale, ScaleTransform.ScaleYProperty, scale, time, MotionKind.Move, ease: Out);
                Animate(parts.Rotate, RotateTransform.AngleProperty, hover && !pressed ? CoreMotion.WaxTilt : 0, time, MotionKind.Move, ease: Out);
                break;
            case ReactKind.Tab:
                var x = on ? CoreMotion.TabDrop : hover ? CoreMotion.TabSlide : 0;
                Animate(parts.Translate, TranslateTransform.XProperty, x, animate ? (on ? CoreMotion.Snap : CoreMotion.Quick) : TimeSpan.Zero, MotionKind.Move,
                    ease: on ? Back() : Out);
                RefreshLoop(part, on);
                break;
        }
    }

    /// <summary>
    /// <c>s:Motion.HoverGlow</c> on a template part: its opacity rises to this value while the templated control is under the
    /// mouse (a wax button brightening), and to 0 otherwise. A fade, so Reduced keeps it.
    /// </summary>
    public static readonly DependencyProperty HoverGlowProperty =
        DependencyProperty.RegisterAttached("HoverGlow", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnHoverGlowChanged));

    public static double GetHoverGlow(DependencyObject d) => (double)d.GetValue(HoverGlowProperty);
    public static void SetHoverGlow(DependencyObject d, double value) => d.SetValue(HoverGlowProperty, value);

    private static void OnHoverGlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement part) return;
        part.Opacity = 0;
        WhenTemplated(part, control =>
        {
            void Update() => Animate(part, UIElement.OpacityProperty, control.IsMouseOver && control.IsEnabled ? GetHoverGlow(part) : 0,
                CoreMotion.Quick, MotionKind.Fade, ease: Out);
            control.MouseEnter += (_, _) => Update();
            control.MouseLeave += (_, _) => Update();
        });
    }

    /// <summary>
    /// <c>s:Motion.FocusGlow</c> on a template part: a gold glow that fades to this opacity while the templated control has
    /// keyboard focus (plaques glow gold on focus, REQ-MOT-3), and to 0 otherwise.
    /// </summary>
    public static readonly DependencyProperty FocusGlowProperty =
        DependencyProperty.RegisterAttached("FocusGlow", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnFocusGlowChanged));

    public static double GetFocusGlow(DependencyObject d) => (double)d.GetValue(FocusGlowProperty);
    public static void SetFocusGlow(DependencyObject d, double value) => d.SetValue(FocusGlowProperty, value);

    private static void OnFocusGlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement part) return;
        part.Opacity = 0;
        WhenTemplated(part, control =>
        {
            void Update() => Animate(part, UIElement.OpacityProperty, control.IsKeyboardFocusWithin ? GetFocusGlow(part) : 0,
                CoreMotion.Quick, MotionKind.Fade, ease: Out);
            control.IsKeyboardFocusWithinChanged += (_, _) => Update();
            Update();
        });
    }

    /// <summary>
    /// <c>s:Motion.Switch</c> on a seal switch's seal: the travel in pixels from off to on. Checking slides the seal across
    /// and stamps it (scale 1.8 to 1, a slight overshoot); unchecking slides it back (REQ-MOT-3). Replaces a layout trigger.
    /// </summary>
    public static readonly DependencyProperty SwitchProperty =
        DependencyProperty.RegisterAttached("Switch", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnSwitchChanged));

    public static double GetSwitch(DependencyObject d) => (double)d.GetValue(SwitchProperty);
    public static void SetSwitch(DependencyObject d, double value) => d.SetValue(SwitchProperty, value);

    private static void OnSwitchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement seal) return;
        WhenTemplated(seal, control =>
        {
            if (control is not ToggleButton toggle) return;
            var parts = TransformOf(seal, new Point(0.5, 0.5));
            void Place(bool animate)
            {
                var on = toggle.IsChecked == true;
                Animate(parts.Translate, TranslateTransform.XProperty, on ? GetSwitch(seal) : 0, animate ? CoreMotion.Snap : TimeSpan.Zero, MotionKind.Move,
                    ease: Out);
                if (animate && on)
                {
                    Animate(parts.Scale, ScaleTransform.ScaleXProperty, 1, CoreMotion.Snap, MotionKind.Move, from: CoreMotion.StampFrom, ease: Back(0.5));
                    Animate(parts.Scale, ScaleTransform.ScaleYProperty, 1, CoreMotion.Snap, MotionKind.Move, from: CoreMotion.StampFrom, ease: Back(0.5));
                }
            }
            toggle.Checked += (_, _) => Place(true);
            toggle.Unchecked += (_, _) => Place(true);
            toggle.Indeterminate += (_, _) => Place(true);
            Place(false);
        });
    }

    /// <summary><c>s:Motion.StampWhen</c>: the element is stamped each time this turns true (a step's seal when it is done).</summary>
    public static readonly DependencyProperty StampWhenProperty =
        DependencyProperty.RegisterAttached("StampWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnStampWhenChanged));

    public static bool GetStampWhen(DependencyObject d) => (bool)d.GetValue(StampWhenProperty);
    public static void SetStampWhen(DependencyObject d, bool value) => d.SetValue(StampWhenProperty, value);

    private static void OnStampWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement { IsVisible: true } element && (bool)e.NewValue && !(bool)e.OldValue) Stamp(element);
    }

    /// <summary><c>s:Motion.PulseWhen</c>: the element pulses gently while this is true (the setup guide's current seal). Full only.</summary>
    public static readonly DependencyProperty PulseWhenProperty =
        DependencyProperty.RegisterAttached("PulseWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPulseWhenChanged));

    public static bool GetPulseWhen(DependencyObject d) => (bool)d.GetValue(PulseWhenProperty);
    public static void SetPulseWhen(DependencyObject d, bool value) => d.SetValue(PulseWhenProperty, value);

    private static void OnPulseWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        var parts = TransformOf(element, new Point(0.5, 0.5));
        KeepLooping(element,
            () =>
            {
                Loop(parts.Scale, ScaleTransform.ScaleXProperty, [1, 1.1], TimeSpan.FromSeconds(1.8));
                Loop(parts.Scale, ScaleTransform.ScaleYProperty, [1, 1.1], TimeSpan.FromSeconds(1.8));
            },
            () =>
            {
                Stop(parts.Scale, ScaleTransform.ScaleXProperty);
                Stop(parts.Scale, ScaleTransform.ScaleYProperty);
            });
        RefreshLoop(element, (bool)e.NewValue);
    }

    /// <summary><c>s:Motion.Entrance</c>: a one-off entrance played when the element first loads and on <see cref="Replay"/>.</summary>
    public static readonly DependencyProperty EntranceProperty =
        DependencyProperty.RegisterAttached("Entrance", typeof(EntranceKind), typeof(Motion), new PropertyMetadata(EntranceKind.None, OnEntranceChanged));

    public static EntranceKind GetEntrance(DependencyObject d) => (EntranceKind)d.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject d, EntranceKind value) => d.SetValue(EntranceProperty, value);

    /// <summary><c>s:Motion.Delay</c>: how long after the start an entrance waits.</summary>
    public static readonly DependencyProperty DelayProperty =
        DependencyProperty.RegisterAttached("Delay", typeof(TimeSpan), typeof(Motion), new PropertyMetadata(TimeSpan.Zero));

    public static TimeSpan GetDelay(DependencyObject d) => (TimeSpan)d.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject d, TimeSpan value) => d.SetValue(DelayProperty, value);

    /// <summary><c>s:Motion.OnLoad</c>: false to play the entrance only on <see cref="Replay"/> (the Tale's chapters).</summary>
    public static readonly DependencyProperty OnLoadProperty =
        DependencyProperty.RegisterAttached("OnLoad", typeof(bool), typeof(Motion), new PropertyMetadata(true));

    public static bool GetOnLoad(DependencyObject d) => (bool)d.GetValue(OnLoadProperty);
    public static void SetOnLoad(DependencyObject d, bool value) => d.SetValue(OnLoadProperty, value);

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || (EntranceKind)e.NewValue == EntranceKind.None) return;
        var played = false;
        element.Loaded += (_, _) =>
        {
            if (played || !GetOnLoad(element)) return;
            played = true;
            Enter(element, GetEntrance(element), GetDelay(element));
        };
    }

    /// <summary>Plays every <c>s:Motion.Entrance</c> under <paramref name="root"/> again (a Tale chapter coming on).</summary>
    public static void Replay(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var element in Descendants(root).OfType<UIElement>())
        {
            var kind = GetEntrance(element);
            if (kind != EntranceKind.None) Enter(element, kind, GetDelay(element));
        }
    }

    /// <summary>Ends every entrance under <paramref name="root"/> on its finished frame (pause, or a still chapter).</summary>
    public static void Settle(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var element in Descendants(root).OfType<UIElement>())
        {
            if (GetEntrance(element) == EntranceKind.None) continue;
            Stop(element, UIElement.OpacityProperty);
            ExistingTransformOf(element)?.Reset();
            element.ClearValue(UIElement.OpacityMaskProperty);
            if (element is Shape shape)
            {
                Stop(shape, Shape.StrokeDashOffsetProperty);
                shape.ClearValue(Shape.StrokeDashArrayProperty);
                shape.ClearValue(Shape.StrokeDashOffsetProperty);
            }
        }
    }

    /// <summary>
    /// <c>s:Motion.Candle</c> on a candle template's root grid, bound to the candle's state name (Lit, Guttering, Snuffed).
    /// The parts are its children marked with <c>s:Motion.CandlePart</c>. A burning flame flickers (Full only), a change of
    /// state gutters, snuffs with rising smoke or relights with a pop (REQ-MOT-6, 7).
    /// </summary>
    public static readonly DependencyProperty CandleProperty =
        DependencyProperty.RegisterAttached("Candle", typeof(string), typeof(Motion), new PropertyMetadata(null, OnCandleChanged));

    public static string? GetCandle(DependencyObject d) => (string?)d.GetValue(CandleProperty);
    public static void SetCandle(DependencyObject d, string? value) => d.SetValue(CandleProperty, value);

    /// <summary><c>s:Motion.CandlePart</c>: Flame, Glow or Smoke.</summary>
    public static readonly DependencyProperty CandlePartProperty =
        DependencyProperty.RegisterAttached("CandlePart", typeof(string), typeof(Motion), new PropertyMetadata(null));

    public static string? GetCandlePart(DependencyObject d) => (string?)d.GetValue(CandlePartProperty);
    public static void SetCandlePart(DependencyObject d, string? value) => d.SetValue(CandlePartProperty, value);

    private static CandleState? ParseCandle(object? value) =>
        value is string s && Enum.TryParse<CandleState>(s, out var state) ? state : null;

    private static void OnCandleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel candle) return;
        var change = CandleMotion.Between(ParseCandle(e.OldValue), ParseCandle(e.NewValue) ?? CandleState.Lit);
        KeepLooping(candle, () => StartFlicker(candle), () => StopFlicker(candle));
        // After the template's own triggers for the new state have applied.
        candle.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var state = ParseCandle(GetCandle(candle)) ?? CandleState.Lit;
            RefreshLoop(candle, state != CandleState.Snuffed, restart: change != CandleChange.None);
            // The pop or gutter replaces the flame's scale loops for a moment; the flicker starts again when it ends.
            if (change != CandleChange.None && candle.IsVisible) PlayCandleChange(candle, change, () => RefreshLoop(candle, Burning(candle), restart: true));
        });
    }

    private static bool Burning(Panel candle) => (ParseCandle(GetCandle(candle)) ?? CandleState.Lit) != CandleState.Snuffed;

    private static UIElement? PartOf(Panel candle, string name) =>
        candle.Children.OfType<UIElement>().FirstOrDefault(c => GetCandlePart(c) == name);

    private static void StartFlicker(Panel candle)
    {
        var flicker = CandleMotion.FlickerFor(ParseCandle(GetCandle(candle)) ?? CandleState.Lit);
        if (flicker.Stretch <= 0)
        {
            StopFlicker(candle);
            return;
        }
        if (PartOf(candle, "Flame") is { } flame)
        {
            var parts = TransformOf(flame, new Point(0.5, 1));
            var k = flicker.Stretch;
            Loop(parts.Scale, ScaleTransform.ScaleYProperty, [1, 1 + k, 1 - k / 2, 1 + k * 0.6, 1 - k / 3], flicker.Period);
            Loop(parts.Scale, ScaleTransform.ScaleXProperty, [1, 1 - k / 2, 1 + k / 3, 1 - k / 3, 1 + k / 4], flicker.Period * 1.3);
            Loop(parts.Skew, SkewTransform.AngleXProperty, [0, -3, 2, -1, 3, 0], flicker.Period * 2.6);
        }
        if (PartOf(candle, "Glow") is { } glow)
        {
            var rest = (double)glow.GetAnimationBaseValue(UIElement.OpacityProperty);
            Loop(glow, UIElement.OpacityProperty, [rest, rest * 0.75, rest * 1.1, rest * 0.85], flicker.Period * 2.2);
        }
    }

    private static void StopFlicker(Panel candle)
    {
        if (PartOf(candle, "Flame") is { } flame) ExistingTransformOf(flame)?.Reset();
        if (PartOf(candle, "Glow") is { } glow) Stop(glow, UIElement.OpacityProperty);
    }

    /// <param name="flickerAgain">Runs when a change that animates the flame's scale has finished.</param>
    private static void PlayCandleChange(Panel candle, CandleChange change, Action flickerAgain)
    {
        var flame = PartOf(candle, "Flame");
        switch (change)
        {
            case CandleChange.Snuff when PartOf(candle, "Smoke") is { } smoke:
                FadeIn(smoke, CoreMotion.Unroll);
                Animate(TransformOf(smoke, new Point(0.5, 1)).Translate, TranslateTransform.YProperty, 0, CoreMotion.Ink, MotionKind.Move, from: 10, ease: Out);
                break;
            case CandleChange.Relight when flame is not null:
                var pop = TransformOf(flame, new Point(0.5, 1));
                Animate(pop.Scale, ScaleTransform.ScaleXProperty, 1, CoreMotion.Snap, MotionKind.Move, from: 0.2, ease: Back(0.8));
                Animate(pop.Scale, ScaleTransform.ScaleYProperty, 1, CoreMotion.Snap, MotionKind.Move, from: 0.2, ease: Back(0.8), completed: flickerAgain);
                FadeIn(flame, CoreMotion.Quick);
                break;
            case CandleChange.Gutter or CandleChange.Flare when flame is not null:
                var sway = TransformOf(flame, new Point(0.5, 1));
                Animate(sway.Scale, ScaleTransform.ScaleYProperty, 1, CoreMotion.Unroll, MotionKind.Move, from: change == CandleChange.Gutter ? 1.5 : 0.6, ease: Out,
                    completed: flickerAgain);
                break;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) stack.Push(child);
        }
    }
}
