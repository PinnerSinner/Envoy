using Envoy.Core.Capture;
using Xunit;

namespace Envoy.Core.Tests.Capture;

public class CaptureGateTests
{
    private static readonly CaptureObservation Reading = new(HasRegion: true, RegionOnScreen: true, ShouldRead: true,
        WindowFound: true, Minimized: false, GameForeground: true, EnvoyForeground: false, RegionSource.WindowRelative,
        Samples: Owners(0));

    private static readonly CaptureGateOptions Defaults = new(PauseWhenCovered: true, PauseWhenNotForeground: false,
        BackendSeesOccluded: false, PreviewOpen: false);

    /// <summary>Nine sample owners: <paramref name="others"/> foreign windows, one Envoy window, the rest the game.</summary>
    private static SampleOwner[] Owners(int others) =>
        [.. Enumerable.Repeat(SampleOwner.Other, others), SampleOwner.Envoy, .. Enumerable.Repeat(SampleOwner.Game, 8 - others)];

    private static PauseReason Eval(CaptureObservation o, CaptureGateOptions? options = null) => CaptureGate.Evaluate(o, options ?? Defaults);

    [Fact]
    public void Reading_when_everything_is_fine() => Assert.Equal(PauseReason.None, Eval(Reading));

    [Fact]
    public void No_region_always_pauses_even_with_preview()
    {
        var o = Reading with { HasRegion = false, RegionOnScreen = false, Minimized = true, ShouldRead = false };
        Assert.Equal(PauseReason.NoRegion, Eval(o));
        Assert.Equal(PauseReason.NoRegion, Eval(o, Defaults with { PreviewOpen = true }));
    }

    [Fact]
    public void Off_screen_region_pauses_even_with_preview()
    {
        var o = Reading with { RegionOnScreen = false, Minimized = true };
        Assert.Equal(PauseReason.RegionOffScreen, Eval(o));
        Assert.Equal(PauseReason.RegionOffScreen, Eval(o, Defaults with { PreviewOpen = true }));
    }

    [Fact]
    public void Minimized_game_pauses_even_with_preview()
    {
        var o = Reading with { Minimized = true, ShouldRead = false };
        Assert.Equal(PauseReason.GameMinimized, Eval(o));
        Assert.Equal(PauseReason.GameMinimized, Eval(o, Defaults with { PreviewOpen = true }));
        Assert.Equal(PauseReason.None, Eval(Reading with { Minimized = true, WindowFound = false })); // no window: nothing to minimise
    }

    [Fact]
    public void No_game_pauses_unless_preview_open()
    {
        var o = Reading with { ShouldRead = false, WindowFound = false };
        Assert.Equal(PauseReason.NoGame, Eval(o));
        Assert.Equal(PauseReason.None, Eval(o, Defaults with { PreviewOpen = true }));
    }

    [Fact]
    public void Covered_region_pauses_when_enabled_and_backend_cannot_see_occluded()
    {
        Assert.Equal(PauseReason.ChatCovered, Eval(Reading with { Samples = Owners(3) }));
        Assert.Equal(PauseReason.None, Eval(Reading with { Samples = Owners(2) }));
        Assert.Equal(PauseReason.None, Eval(Reading with { Samples = Owners(8) }, Defaults with { PauseWhenCovered = false }));
        Assert.Equal(PauseReason.None, Eval(Reading with { Samples = null }));
    }

    [Fact]
    public void Covered_ignored_when_backend_sees_occluded() =>
        Assert.Equal(PauseReason.None, Eval(Reading with { Samples = Owners(8) }, Defaults with { BackendSeesOccluded = true }));

    [Theory]
    [InlineData(RegionSource.GameAbsolute)]
    [InlineData(RegionSource.AnyAbsolute)]
    public void Covered_ignored_for_absolute_regions(RegionSource source) =>
        Assert.Equal(PauseReason.None, Eval(Reading with { Source = source, Samples = Owners(8) }));

    [Fact]
    public void Envoy_windows_never_count_as_covering()
    {
        SampleOwner[] envoy = [.. Enumerable.Repeat(SampleOwner.Envoy, 9)];
        Assert.Equal(0, CaptureGate.CountCovered(envoy));
        Assert.Equal(PauseReason.None, Eval(Reading with { Samples = envoy }));
        Assert.Equal(3, CaptureGate.CountCovered(Owners(3)));
    }

    [Fact]
    public void Not_foreground_pauses_only_when_setting_on()
    {
        var o = Reading with { GameForeground = false };
        Assert.Equal(PauseReason.None, Eval(o));
        Assert.Equal(PauseReason.GameNotForeground, Eval(o, Defaults with { PauseWhenNotForeground = true }));
        Assert.Equal(PauseReason.None, Eval(Reading, Defaults with { PauseWhenNotForeground = true }));
    }

    [Fact]
    public void Envoy_foreground_does_not_count_as_not_foreground() =>
        Assert.Equal(PauseReason.None, Eval(Reading with { GameForeground = false, EnvoyForeground = true }, Defaults with { PauseWhenNotForeground = true }));

    [Fact]
    public void Preview_overrides_no_game_covered_and_not_foreground()
    {
        var preview = Defaults with { PreviewOpen = true, PauseWhenNotForeground = true };
        Assert.Equal(PauseReason.None, Eval(Reading with { ShouldRead = false }, preview));
        Assert.Equal(PauseReason.None, Eval(Reading with { Samples = Owners(9 - 1) }, preview));
        Assert.Equal(PauseReason.None, Eval(Reading with { GameForeground = false }, preview));
    }

    [Fact]
    public void Reading_is_false_when_only_preview_override_allows_capture()
    {
        var preview = Defaults with { PreviewOpen = true };
        var covered = Reading with { Samples = Owners(5) };
        Assert.Equal(PauseReason.None, Eval(covered, preview));
        Assert.False(CaptureGate.IsReading(covered, preview));
        Assert.False(CaptureGate.IsReading(Reading with { ShouldRead = false }, preview));
        Assert.True(CaptureGate.IsReading(Reading, preview));
    }
}
