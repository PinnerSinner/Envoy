using Envoy.Core.Settings;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class MotionPolicyTests
{
    [Theory]
    [InlineData(MotionSetting.Auto, true, MotionLevel.Full)]
    [InlineData(MotionSetting.Auto, false, MotionLevel.Reduced)]
    [InlineData(MotionSetting.Full, true, MotionLevel.Full)]
    [InlineData(MotionSetting.Full, false, MotionLevel.Full)]
    [InlineData(MotionSetting.Reduced, true, MotionLevel.Reduced)]
    [InlineData(MotionSetting.Reduced, false, MotionLevel.Reduced)]
    [InlineData(MotionSetting.Off, true, MotionLevel.Off)]
    [InlineData(MotionSetting.Off, false, MotionLevel.Off)]
    public void Setting_and_windows_flag_resolve_to_a_level(MotionSetting setting, bool clientAreaAnimation, MotionLevel expected) =>
        Assert.Equal(expected, MotionPolicy.Resolve(setting, clientAreaAnimation));

    [Fact]
    public void An_unknown_setting_follows_windows_like_auto()
    {
        Assert.Equal(MotionLevel.Full, MotionPolicy.Resolve((MotionSetting)42, true));
        Assert.Equal(MotionLevel.Reduced, MotionPolicy.Resolve((MotionSetting)42, false));
    }

    [Fact]
    public void New_settings_follow_windows() => Assert.Equal(MotionSetting.Auto, new AppSettings().Appearance.Motion);

    [Fact]
    public void The_named_durations_match_the_spec()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), Motion.Quick);
        Assert.Equal(TimeSpan.FromMilliseconds(320), Motion.Snap);
        Assert.Equal(TimeSpan.FromMilliseconds(450), Motion.Unroll);
        Assert.Equal(TimeSpan.FromMilliseconds(1400), Motion.Ink);
        Assert.Equal(TimeSpan.FromMilliseconds(300), Motion.SealDelay);
        Assert.Equal(1.8, Motion.StampFrom);
        Assert.Equal(2, Motion.Lift);
        Assert.Equal(8, Motion.TabSlide);
    }

    [Theory]
    [InlineData(MotionLevel.Full, MotionKind.Fade, true)]
    [InlineData(MotionLevel.Full, MotionKind.Move, true)]
    [InlineData(MotionLevel.Full, MotionKind.Loop, true)]
    [InlineData(MotionLevel.Reduced, MotionKind.Fade, true)]
    [InlineData(MotionLevel.Reduced, MotionKind.Move, false)]
    [InlineData(MotionLevel.Reduced, MotionKind.Loop, false)]
    [InlineData(MotionLevel.Off, MotionKind.Fade, false)]
    [InlineData(MotionLevel.Off, MotionKind.Move, false)]
    [InlineData(MotionLevel.Off, MotionKind.Loop, false)]
    public void Reduced_keeps_only_fades_and_off_keeps_nothing(MotionLevel level, MotionKind kind, bool runs)
    {
        Assert.Equal(runs, Motion.Runs(level, kind));
        Assert.Equal(runs ? Motion.Snap : TimeSpan.Zero, Motion.DurationFor(level, kind, Motion.Snap));
    }

    [Fact]
    public void A_zero_duration_never_runs() => Assert.Equal(TimeSpan.Zero, Motion.DurationFor(MotionLevel.Full, MotionKind.Move, TimeSpan.Zero));

    [Fact]
    public void Dispatches_fade_like_ink_over_the_last_two_seconds()
    {
        Assert.Equal(1, Motion.LingerOpacity(TimeSpan.FromSeconds(10), MotionLevel.Full));
        Assert.Equal(0.5, Motion.LingerOpacity(TimeSpan.FromSeconds(1), MotionLevel.Full), 3);
        Assert.Equal(0.5, Motion.LingerOpacity(TimeSpan.FromSeconds(1), MotionLevel.Reduced), 3);
        Assert.Equal(0, Motion.LingerOpacity(TimeSpan.Zero, MotionLevel.Full));
        Assert.Equal(0, Motion.LingerOpacity(TimeSpan.FromSeconds(-1), MotionLevel.Full));
    }

    [Fact]
    public void Without_motion_a_dispatch_stays_solid_until_it_goes()
    {
        Assert.Equal(1, Motion.LingerOpacity(TimeSpan.FromSeconds(1), MotionLevel.Off));
        Assert.Equal(0, Motion.LingerOpacity(TimeSpan.Zero, MotionLevel.Off));
    }

    [Fact]
    public void Every_setting_has_a_plain_label()
    {
        Assert.Equal(Enum.GetValues<MotionSetting>(), MotionText.Choices);
        Assert.Equal("Follow Windows", MotionText.LabelOf(MotionSetting.Auto));
        Assert.Equal("Fades only", MotionText.LabelOf(MotionSetting.Reduced));
        Assert.Equal(MotionText.Auto, MotionText.LabelOf((MotionSetting)42));
        Assert.Equal(MotionText.Choices.Count, MotionText.Choices.Select(MotionText.LabelOf).Distinct().Count());
    }
}
