using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class PendingUpdatePolicyTests
{
    [Fact]
    public void Pending_package_without_flags_is_applied() => Assert.True(PendingUpdatePolicy.ShouldApplyAtStartup(true, false, false));

    [Fact]
    public void Safe_mode_argument_skips_the_apply() => Assert.False(PendingUpdatePolicy.ShouldApplyAtStartup(true, true, false));

    [Fact]
    public void Shift_held_skips_the_apply() => Assert.False(PendingUpdatePolicy.ShouldApplyAtStartup(true, false, true));

    [Fact]
    public void No_package_means_nothing_to_apply() => Assert.False(PendingUpdatePolicy.ShouldApplyAtStartup(false, false, false));

    [Fact]
    public void Process_started_by_the_updater_does_not_apply_again()
        => Assert.False(PendingUpdatePolicy.ShouldApplyAtStartup(true, false, false, startedByUpdater: true));

    [Fact]
    public void Ordinary_start_applies() => Assert.True(PendingUpdatePolicy.ShouldApplyAtStartup(true, false, false, startedByUpdater: false));
}
