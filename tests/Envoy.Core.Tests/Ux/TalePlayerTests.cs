using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class TalePlayerTests
{
    private static readonly TimeSpan Chapter = TalePlayer.ChapterLength;

    [Fact]
    public void The_tour_lasts_about_a_minute() =>
        Assert.InRange((Chapter * TalePlayer.ChapterCount).TotalSeconds, 45, 75);

    [Fact]
    public void With_motion_it_plays_through_and_stops_at_the_end()
    {
        var player = new TalePlayer(autoPlay: true);
        var changes = 0;
        player.ChapterChanged += () => changes++;
        Assert.True(player.IsPlaying);
        for (var i = 1; i < TalePlayer.ChapterCount; i++)
        {
            player.Advance(Chapter);
            Assert.Equal(i, player.Chapter);
        }
        Assert.Equal(5, changes);
        Assert.True(player.IsLast);
        player.Advance(Chapter);
        Assert.False(player.IsPlaying);
        Assert.Equal(5, player.Chapter);
        Assert.Equal(1, player.Progress, 6);
    }

    [Fact]
    public void A_still_tale_never_moves_by_itself()
    {
        var player = new TalePlayer(autoPlay: false);
        Assert.False(player.IsPlaying);
        player.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, player.Chapter);
        player.TogglePlay();
        Assert.False(player.IsPlaying);
        player.Next();
        Assert.Equal(1, player.Chapter);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void Pause_holds_the_playhead()
    {
        var player = new TalePlayer(autoPlay: true);
        player.Advance(TimeSpan.FromSeconds(3));
        player.TogglePlay();
        Assert.False(player.IsPlaying);
        player.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, player.Chapter);
        Assert.Equal(TimeSpan.FromSeconds(3), player.Elapsed);
        player.TogglePlay();
        Assert.True(player.IsPlaying);
    }

    [Fact]
    public void Play_at_the_end_starts_again()
    {
        var player = new TalePlayer(autoPlay: true);
        player.GoTo(5);
        player.Advance(Chapter);
        Assert.False(player.IsPlaying);
        player.TogglePlay();
        Assert.True(player.IsPlaying);
        Assert.Equal(0, player.Chapter);
    }

    [Fact]
    public void Next_and_previous_stop_at_the_ends_and_restart_the_chapter()
    {
        var player = new TalePlayer(autoPlay: true);
        player.Previous();
        Assert.Equal(0, player.Chapter);
        player.Advance(TimeSpan.FromSeconds(4));
        player.Next();
        Assert.Equal(1, player.Chapter);
        Assert.Equal(TimeSpan.Zero, player.Elapsed);
        player.GoTo(99);
        Assert.Equal(5, player.Chapter);
        player.Next();
        Assert.Equal(5, player.Chapter);
        player.GoTo(-4);
        Assert.Equal(0, player.Chapter);
        player.Restart();
        Assert.Equal(0, player.Chapter);
    }

    [Fact]
    public void Progress_counts_the_whole_tale()
    {
        var player = new TalePlayer(autoPlay: true);
        Assert.Equal(0, player.Progress);
        player.GoTo(3);
        player.Advance(Chapter / 2);
        Assert.Equal(3.5 / 6, player.Progress, 6);
    }

    [Theory]
    [InlineData(true, TaleKey.Space, TaleCommand.TogglePlay)]
    [InlineData(false, TaleKey.Space, TaleCommand.Next)]
    [InlineData(true, TaleKey.Left, TaleCommand.Previous)]
    [InlineData(true, TaleKey.Right, TaleCommand.Next)]
    [InlineData(false, TaleKey.Escape, TaleCommand.Close)]
    [InlineData(true, TaleKey.Other, TaleCommand.None)]
    public void Keys_map_to_commands(bool autoPlay, TaleKey key, TaleCommand expected) =>
        Assert.Equal(expected, new TalePlayer(autoPlay).CommandFor(key));

    [Fact]
    public void Commands_run_on_the_player()
    {
        var player = new TalePlayer(autoPlay: true);
        Assert.True(player.Run(TaleCommand.Next));
        Assert.Equal(1, player.Chapter);
        Assert.True(player.Run(TaleCommand.Previous));
        Assert.False(player.Run(TaleCommand.Previous));
        Assert.True(player.Run(TaleCommand.TogglePlay));
        Assert.False(player.IsPlaying);
        Assert.False(player.Run(TaleCommand.Close));
        Assert.False(player.Run(TaleCommand.None));
        Assert.False(new TalePlayer(autoPlay: false).Run(TaleCommand.TogglePlay));
    }

    [Fact]
    public void Playing_changes_are_announced()
    {
        var player = new TalePlayer(autoPlay: true);
        var raised = 0;
        player.PlayingChanged += () => raised++;
        player.TogglePlay();
        player.TogglePlay();
        Assert.Equal(2, raised);
        player.Advance(TimeSpan.Zero);
        Assert.Equal(2, raised);
    }
}
