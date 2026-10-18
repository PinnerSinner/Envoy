using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Envoy.App.Services;

namespace Envoy.App.Controls;

/// <summary>
/// A wall torch's flame for the war table (spec 09b, REQ-MOT-8), after <c>docs/media/readme/flame.py</c>: 14 soft blobs
/// rise, shrink and cool from white-gold to deep red, merged by a blur; each has its own period, so the pattern never
/// visibly repeats. A little smoke and the odd spark rise above it. No uniform pulsing and no per-frame randomness: the
/// periods and drifts come from a fixed seed. Animated only at Full motion and while visible (<see cref="Motion.KeepLooping"/>);
/// colours are palette keys. Decoration only: not focusable, not hit-testable, hidden from screen readers.
/// </summary>
public sealed class TorchFlame : Canvas
{
    private const int Blobs = 14;
    private const int Puffs = 4;
    private const int Sparks = 3;
    private readonly List<Action> _start = [];
    private readonly List<Action> _stop = [];

    public TorchFlame() : this(1)
    {
    }

    /// <param name="seed">Gives each torch its own pattern.</param>
    public TorchFlame(int seed)
    {
        Width = 40;
        Height = 160;
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
        Build(seed);
        Motion.KeepLooping(this, () => _start.ForEach(a => a()), () => _stop.ForEach(a => a()));
    }

    /// <summary>The seed's pattern; set from XAML (<c>Seed="2"</c>) before the torch loads.</summary>
    public int Seed
    {
        get => _seed;
        set
        {
            if (_seed == value) return;
            _seed = value;
            Children.Clear();
            _start.Clear();
            _stop.Clear();
            Build(value);
        }
    }

    private int _seed = 1;

    /// <summary>A fixed pseudo-random number in [0, 1) for (seed, index, salt): the same every run.</summary>
    internal static double Unit(int seed, int index, int salt)
    {
        var x = Math.Sin(seed * 12.9898 + index * 78.233 + salt * 37.719) * 43758.5453;
        return x - Math.Floor(x);
    }

    private static double Between(double low, double high, double unit) => low + (high - low) * unit;

    private void Build(int seed)
    {
        _seed = seed;
        // The sconce: an iron bowl on a short bracket, in oak and gold.
        var bracket = new Rectangle { Width = 8, Height = 70, RadiusX = 2, RadiusY = 2 };
        bracket.SetResourceReference(Shape.FillProperty, "Oak");
        SetLeft(bracket, 16);
        SetTop(bracket, 64);
        var bowl = new System.Windows.Shapes.Path { Data = Geometry.Parse("M4,46 H36 L30,64 H10 Z"), StrokeThickness = 1.5 };
        bowl.SetResourceReference(Shape.FillProperty, "Oak.Deep");
        bowl.SetResourceReference(Shape.StrokeProperty, "Or.Dark");
        var ring = new Rectangle { Width = 26, Height = 4 };
        ring.SetResourceReference(Shape.FillProperty, "Or.Dark");
        SetLeft(ring, 7);
        SetTop(ring, 104);
        Children.Add(bracket);
        Children.Add(ring);
        Children.Add(bowl);

        // The flame's base sits at (20, 46), the bowl's rim.
        var haze = new Ellipse { Width = 36, Height = 64, Opacity = 0.55, Effect = new BlurEffect { Radius = 12 } };
        haze.SetResourceReference(Shape.FillProperty, "Candle.Flame");
        SetLeft(haze, 2);
        SetTop(haze, -6);
        Children.Add(haze);
        var hazeParts = Motion.TransformOf(haze, new Point(0.5, 1));
        _start.Add(() => Motion.Loop(hazeParts.Scale, ScaleTransform.ScaleYProperty, [1, 0.9, 1.08, 0.93], TimeSpan.FromSeconds(1.1)));
        _stop.Add(() => Motion.Stop(hazeParts.Scale, ScaleTransform.ScaleYProperty));

        // The blobs, merged by one blur, under a slow sway.
        var flame = new Canvas { Width = 40, Height = 60, Effect = new BlurEffect { Radius = 4 } };
        SetTop(flame, 0);
        var sway = Motion.TransformOf(flame, new Point(0.5, 0.8));
        _start.Add(() => Motion.Loop(sway.Skew, SkewTransform.AngleXProperty, [0, -3, 1.5, -1, 2.5], TimeSpan.FromSeconds(2.9)));
        _stop.Add(() => Motion.Stop(sway.Skew, SkewTransform.AngleXProperty));
        for (var i = 0; i < Blobs; i++) AddBlob(flame, seed, i);
        var core = new Ellipse { Width = 20, Height = 16 };
        core.SetResourceReference(Shape.FillProperty, "Vellum.Light");
        SetLeft(core, 10);
        SetTop(core, 38);
        flame.Children.Add(core);
        Children.Add(flame);

        for (var i = 0; i < Puffs; i++) AddPuff(seed, i);
        for (var i = 0; i < Sparks; i++) AddSpark(seed, i);
    }

    private void AddBlob(Canvas flame, int seed, int i)
    {
        var period = TimeSpan.FromSeconds(Between(0.62, 1.12, Unit(seed, i, 1)));
        var offset = TimeSpan.FromSeconds(Between(0, 0.9, Unit(seed, i, 2)));
        var size = Between(17, 25, Unit(seed, i, 3));
        var x0 = Between(-7, 7, Unit(seed, i, 4));
        var rise = Between(34, 52, Unit(seed, i, 5));
        var drift = Between(-9, 9, Unit(seed, i, 6));
        var blob = new Grid { Width = size, Height = size, Opacity = 0 };
        var cool = new Ellipse();
        cool.SetResourceReference(Shape.FillProperty, "Gules");
        var warm = new Ellipse();
        warm.SetResourceReference(Shape.FillProperty, "Candle.Flame");
        var hot = new Ellipse();
        hot.SetResourceReference(Shape.FillProperty, "Vellum.Light");
        blob.Children.Add(cool);
        blob.Children.Add(warm);
        blob.Children.Add(hot);
        SetLeft(blob, 20 + x0 - size / 2);
        SetTop(blob, 46 - size / 2);
        flame.Children.Add(blob);
        var parts = Motion.TransformOf(blob, new Point(0.5, 0.5));
        _start.Add(() =>
        {
            Motion.Cycle(parts.Translate, TranslateTransform.YProperty, [2, -rise * 0.45, -rise], period, offset);
            Motion.Cycle(parts.Translate, TranslateTransform.XProperty, [0, drift * 0.4, drift], period, offset);
            Motion.Cycle(parts.Scale, ScaleTransform.ScaleXProperty, [1, 0.75, 0.1], period, offset);
            Motion.Cycle(parts.Scale, ScaleTransform.ScaleYProperty, [1, 0.75, 0.1], period, offset);
            // Cooling: white-gold, then flame, then gules as the top layers fade.
            Motion.Cycle(hot, UIElement.OpacityProperty, [1, 0.2, 0, 0], period, offset);
            Motion.Cycle(warm, UIElement.OpacityProperty, [1, 1, 0.4, 0], period, offset);
            Motion.Cycle(blob, UIElement.OpacityProperty, [0.95, 0.9, 0.7, 0], period, offset);
        });
        _stop.Add(() =>
        {
            parts.Reset();
            Motion.Stop(hot, UIElement.OpacityProperty);
            Motion.Stop(warm, UIElement.OpacityProperty);
            Motion.Stop(blob, UIElement.OpacityProperty);
        });
    }

    private void AddPuff(int seed, int i)
    {
        var period = TimeSpan.FromSeconds(Between(2.6, 3.8, Unit(seed, i, 7)));
        var offset = TimeSpan.FromSeconds(Between(0, 2.5, Unit(seed, i, 8)));
        var x0 = Between(-5, 5, Unit(seed, i, 9));
        var drift = Between(-18, 18, Unit(seed, i, 10));
        var puff = new Ellipse { Width = 12, Height = 12, Opacity = 0, Effect = new BlurEffect { Radius = 5 } };
        puff.SetResourceReference(Shape.FillProperty, "Ink.Soft");
        SetLeft(puff, 14 + x0);
        SetTop(puff, -20);
        Children.Add(puff);
        var parts = Motion.TransformOf(puff, new Point(0.5, 0.5));
        _start.Add(() =>
        {
            Motion.Cycle(parts.Translate, TranslateTransform.YProperty, [0, -80], period, offset);
            Motion.Cycle(parts.Translate, TranslateTransform.XProperty, [0, drift], period, offset);
            Motion.Cycle(parts.Scale, ScaleTransform.ScaleXProperty, [0.8, 2.6], period, offset);
            Motion.Cycle(parts.Scale, ScaleTransform.ScaleYProperty, [0.8, 2.6], period, offset);
            Motion.Cycle(puff, UIElement.OpacityProperty, [0, 0.22, 0], period, offset);
        });
        _stop.Add(() =>
        {
            parts.Reset();
            Motion.Stop(puff, UIElement.OpacityProperty);
        });
    }

    private void AddSpark(int seed, int i)
    {
        var period = TimeSpan.FromSeconds(Between(1.4, 2.6, Unit(seed, i, 11)));
        var offset = TimeSpan.FromSeconds(Between(0, 4, Unit(seed, i, 12)));
        var x0 = Between(-6, 6, Unit(seed, i, 13));
        var drift = Between(-30, 30, Unit(seed, i, 14));
        var rise = Between(110, 170, Unit(seed, i, 15));
        var spark = new Ellipse { Width = 2.6, Height = 2.6, Opacity = 0 };
        spark.SetResourceReference(Shape.FillProperty, "Or.Light");
        SetLeft(spark, 19 + x0);
        SetTop(spark, 6);
        Children.Add(spark);
        var parts = Motion.TransformOf(spark);
        _start.Add(() =>
        {
            Motion.Cycle(parts.Translate, TranslateTransform.YProperty, [0, -rise], period, offset);
            Motion.Cycle(parts.Translate, TranslateTransform.XProperty, [0, drift], period, offset);
            Motion.Cycle(spark, UIElement.OpacityProperty, [0, 1, 1, 0], period, offset);
        });
        _stop.Add(() =>
        {
            parts.Reset();
            Motion.Stop(spark, UIElement.OpacityProperty);
        });
    }
}

/// <summary>
/// The light a <see cref="TorchFlame"/> throws on the wall (REQ-MOT-8): a soft round glow whose brightness wavers through
/// three slow overlapping oscillations (0.83 s, 1.37 s and 3.1 s), so it never pulses evenly. It sits under the charter, so
/// it lights the oak and never the text. Full motion only, like the flame.
/// </summary>
public sealed class TorchLight : Grid
{
    public TorchLight()
    {
        Width = 440;
        Height = 440;
        IsHitTestVisible = false;
        Focusable = false;
        var outer = new Grid { Opacity = 0.93 };
        var inner = new Grid { Opacity = 0.95 };
        var glow = new Ellipse();
        glow.SetResourceReference(Shape.FillProperty, "Candle.Flame");
        var mask = new RadialGradientBrush();
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x5C, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, 0, 0, 0), 0.38));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.7));
        mask.Freeze();
        glow.OpacityMask = mask;
        inner.Children.Add(glow);
        outer.Children.Add(inner);
        Children.Add(outer);
        Motion.KeepLooping(this,
            () =>
            {
                Motion.Loop(outer, OpacityProperty, [0.93, 0.84, 0.97, 0.88], TimeSpan.FromSeconds(0.83));
                Motion.Loop(inner, OpacityProperty, [0.95, 1, 0.86, 0.98, 0.92], TimeSpan.FromSeconds(1.37));
                Motion.Loop(glow, OpacityProperty, [1, 0.9, 1, 0.94], TimeSpan.FromSeconds(3.1));
            },
            () =>
            {
                Motion.Stop(outer, OpacityProperty);
                Motion.Stop(inner, OpacityProperty);
                Motion.Stop(glow, OpacityProperty);
            });
    }
}
