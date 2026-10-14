using Envoy.Core.Security;
using Xunit;

namespace Envoy.Core.Tests.Security;

public class SendTargetGuardTests
{
    [Fact]
    public void No_target_is_NoTarget() => Assert.Equal(SendTargetVerdict.NoTarget, SendTargetGuard.Check(false, false, 0, 42));

    [Fact]
    public void Unknown_target_process_is_NoTarget() => Assert.Equal(SendTargetVerdict.NoTarget, SendTargetGuard.Check(true, true, 0, 0));

    [Fact]
    public void Closed_target_is_TargetClosed() => Assert.Equal(SendTargetVerdict.TargetClosed, SendTargetGuard.Check(true, false, 10, 10));

    [Fact]
    public void Other_foreground_process_is_FocusLost() => Assert.Equal(SendTargetVerdict.FocusLost, SendTargetGuard.Check(true, true, 10, 11));

    [Fact]
    public void Same_process_is_Ok() => Assert.Equal(SendTargetVerdict.Ok, SendTargetGuard.Check(true, true, 10, 10));

    [Fact]
    public void Game_window_wins_when_detected() => Assert.Equal(0x10, SendTargetGuard.ChooseTarget(0x10, true, 0x20, 7, "notepad", 1));

    [Fact]
    public void Dead_game_window_falls_back_to_foreground() =>
        Assert.Equal(0x20, SendTargetGuard.ChooseTarget(0x10, false, 0x20, 7, "notepad", 1));

    [Theory]
    [InlineData("explorer")]
    [InlineData("Explorer.EXE")]
    public void Taskbar_is_no_target(string name) => Assert.Equal(0, SendTargetGuard.ChooseTarget(0, false, 0x20, 7, name, 1));

    [Fact]
    public void Envoy_itself_is_no_target() => Assert.Equal(0, SendTargetGuard.ChooseTarget(0, false, 0x20, 1, "Envoy", 1));

    [Fact]
    public void No_foreground_or_unknown_process_is_no_target()
    {
        Assert.Equal(0, SendTargetGuard.ChooseTarget(0, false, 0, 7, "notepad", 1));
        Assert.Equal(0, SendTargetGuard.ChooseTarget(0, false, 0x20, 0, null, 1));
    }

    [Fact]
    public void Reopening_the_open_reply_box_keeps_its_target()
    {
        Assert.Equal(0x20, SendTargetGuard.TargetOnOpen(0x20, 0, replyBoxOpen: true));
        Assert.Equal(0x30, SendTargetGuard.TargetOnOpen(0x20, 0x30, replyBoxOpen: true));
        Assert.Equal(0, SendTargetGuard.TargetOnOpen(0x20, 0, replyBoxOpen: false));
        Assert.Equal(0x30, SendTargetGuard.TargetOnOpen(0x20, 0x30, replyBoxOpen: false));
    }

    [Fact]
    public void Unknown_process_name_still_targets_foreground() =>
        Assert.Equal(0x20, SendTargetGuard.ChooseTarget(0, false, 0x20, 7, null, 1));

    [Fact]
    public void Every_blocking_verdict_has_a_sentence()
    {
        foreach (var verdict in Enum.GetValues<SendTargetVerdict>())
        {
            var text = SecurityText.SendBlocked(verdict);
            if (verdict == SendTargetVerdict.Ok)
            {
                Assert.Equal("", text);
                continue;
            }
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("Error:", text, StringComparison.Ordinal);
            Assert.EndsWith(".", text, StringComparison.Ordinal);
        }
    }
}
