using System.Text.Json;
using Envoy.Core.Capture;
using Envoy.Core.Games;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Capture;

public class CaptureOptionsTests
{
    [Fact]
    public void From_copies_and_clamps()
    {
        var settings = new AppSettings { Game = "aoe4", MyLanguage = "de" };
        var c = settings.Capture;
        c.Regions["aoe4"] = new ScreenRegion(1, 2, 3, 4);
        c.WindowRegions["aoe4"] = new WindowRelativeRegion(1, 2, 3, 4, 5, 6);
        c.OcrTimeoutMs = 50;
        c.MinIntervalMs = 10;
        c.MaxIntervalMs = 99999;
        c.Grayscale = false;
        c.Contrast = 2.5;
        c.Invert = true;
        c.Upscale = 3;
        c.PauseWhenCovered = false;
        c.PauseWhenNotForeground = true;
        c.Backend = "";

        var o = CaptureOptions.From(settings);

        Assert.Equal("aoe4", o.ForcedGame);
        Assert.Equal("de", o.MyLanguage);
        Assert.Equal(new ScreenRegion(1, 2, 3, 4), o.Regions["aoe4"]);
        Assert.Equal(new WindowRelativeRegion(1, 2, 3, 4, 5, 6), o.WindowRegions["aoe4"]);
        Assert.Equal(CaptureOptions.MinOcrTimeoutMs, o.OcrTimeoutMs);
        Assert.Equal(150, o.Polling.MinIntervalMs);
        Assert.Equal(5000, o.Polling.MaxIntervalMs);
        Assert.Equal(new PreprocessOptions(false, 2.5, true), o.Preprocess);
        Assert.Equal(3, o.Upscale);
        Assert.False(o.PauseWhenCovered);
        Assert.True(o.PauseWhenNotForeground);
        Assert.Equal(CaptureBackendIds.Gdi, o.Backend);

        c.OcrTimeoutMs = 999999;
        Assert.Equal(CaptureOptions.MaxOcrTimeoutMs, CaptureOptions.From(settings).OcrTimeoutMs);
    }

    [Fact]
    public void Snapshot_is_isolated_from_later_settings_edits()
    {
        var settings = new AppSettings();
        settings.Capture.Regions["any"] = new ScreenRegion(1, 2, 300, 100);
        settings.Capture.WindowRegions["aoe2de"] = new WindowRelativeRegion(1, 2, 300, 100, 1920, 1080);
        settings.Capture.AdaptivePolling = false;
        settings.Capture.IntervalMs = 700;
        var o = CaptureOptions.From(settings);

        settings.Capture.Regions["any"] = new ScreenRegion(9, 9, 9, 9);
        settings.Capture.Regions["aoe4"] = new ScreenRegion(9, 9, 9, 9);
        settings.Capture.WindowRegions.Clear();
        settings.Capture.IntervalMs = 2500;
        settings.Capture.Grayscale = false;
        settings.MyLanguage = "ja";
        settings.Capture.Regions = [];

        Assert.Equal(new ScreenRegion(1, 2, 300, 100), o.Regions["any"]);
        Assert.False(o.Regions.ContainsKey("aoe4"));
        Assert.True(o.WindowRegions.ContainsKey("aoe2de"));
        Assert.Equal(700, o.Polling.MinIntervalMs);
        Assert.True(o.Preprocess.Grayscale);
        Assert.Equal("en", o.MyLanguage);
    }

    [Theory]
    [InlineData("any", null, "any")]
    [InlineData("any", "aoe2de", "aoe2de")]
    [InlineData("aoe4", "aoe2de", "aoe4")]
    [InlineData("aoe4", null, "aoe4")]
    public void GameIdFor_matches_AppHost_rule(string forced, string? detected, string expected)
    {
        var o = CaptureOptions.From(new AppSettings { Game = forced });
        Assert.Equal(expected, o.GameIdFor(detected));
        // AppHost.CurrentGameId: Settings.Game != Any ? Settings.Game : _game?.Profile.Id ?? Any
        Assert.Equal(forced != GameCatalog.Any ? forced : detected ?? GameCatalog.Any, o.GameIdFor(detected));
    }

    [Theory]
    [InlineData(true, false, "any", false)]
    [InlineData(true, true, "any", true)]
    [InlineData(false, false, "any", true)]
    [InlineData(true, false, "aoe4", true)]
    [InlineData(false, true, "aoe4", true)]
    public void ShouldRead_matches_v01(bool onlyWhileGameRuns, bool gameRunning, string game, bool expected)
    {
        var settings = new AppSettings { Game = game };
        settings.Capture.OnlyWhileGameRuns = onlyWhileGameRuns;
        Assert.Equal(expected, CaptureOptions.From(settings).ShouldRead(gameRunning));
        // v0.1 AppHost: !OnlyWhileGameRuns || _game is not null || Settings.Game != GameCatalog.Any
        Assert.Equal(!onlyWhileGameRuns || gameRunning || game != GameCatalog.Any, expected);
    }

    [Fact]
    public void Null_entries_from_a_hand_edited_file_are_skipped()
    {
        var settings = new AppSettings();
        settings.Capture.Regions["aoe2de"] = null!;
        settings.Capture.WindowRegions["aoe2de"] = null!;
        var o = CaptureOptions.From(settings);
        Assert.Empty(o.Regions);
        Assert.Empty(o.WindowRegions);
    }
}

public class CaptureSettingsTests
{
    [Fact]
    public void V01_capture_settings_load_with_new_defaults()
    {
        const string v01 = """
            {
              "version": 1,
              "capture": {
                "regions": { "aoe2de": { "x": 40, "y": 700, "width": 600, "height": 200 }, "any": { "x": 1, "y": 2, "width": 3, "height": 4 } },
                "intervalMs": 900,
                "upscale": 2.0,
                "grayscale": true,
                "invert": false,
                "contrast": 1.3,
                "minCharacters": 2,
                "onlyWhileGameRuns": true
              }
            }
            """;
        var settings = JsonSerializer.Deserialize<AppSettings>(v01, SettingsStore.JsonOptions)!;
        var c = settings.Capture;

        Assert.Equal(new ScreenRegion(40, 700, 600, 200), c.Regions["aoe2de"]);
        Assert.Equal(900, c.IntervalMs);
        Assert.Empty(c.WindowRegions);
        Assert.True(c.AdaptivePolling);
        Assert.Equal(300, c.MinIntervalMs);
        Assert.Equal(1500, c.MaxIntervalMs);
        Assert.Equal(4000, c.OcrTimeoutMs);
        Assert.True(c.PauseWhenCovered);
        Assert.False(c.PauseWhenNotForeground);
        Assert.Equal(CaptureBackendIds.Gdi, c.Backend);

        // With no game window the same rectangle is captured as in v0.1 (REQ-CAP-4).
        var o = CaptureOptions.From(settings);
        Assert.Equal(new ScreenRegion(40, 700, 600, 200), RegionMath.ResolveForGame(o, "aoe2de", null).Region);
        Assert.Equal(new ScreenRegion(40, 700, 600, 200), RegionMath.ResolveForGame(o, "aoe2de", new ScreenRegion(0, 0, 1920, 1080)).Region);
        Assert.Equal(new ScreenRegion(1, 2, 3, 4), RegionMath.ResolveForGame(o, "aoe4", null).Region);
    }

    [Fact]
    public void Capture_settings_round_trip_window_regions_and_polling()
    {
        var settings = new AppSettings();
        var c = settings.Capture;
        c.WindowRegions["aoe4"] = new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080);
        c.AdaptivePolling = false;
        c.MinIntervalMs = 400;
        c.MaxIntervalMs = 2000;
        c.OcrTimeoutMs = 6000;
        c.PauseWhenCovered = false;
        c.PauseWhenNotForeground = true;
        c.Backend = "wgc";

        var json = JsonSerializer.Serialize(settings, SettingsStore.JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!.Capture;

        Assert.Equal(new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080), back.WindowRegions["aoe4"]);
        Assert.False(back.AdaptivePolling);
        Assert.Equal(400, back.MinIntervalMs);
        Assert.Equal(2000, back.MaxIntervalMs);
        Assert.Equal(6000, back.OcrTimeoutMs);
        Assert.False(back.PauseWhenCovered);
        Assert.True(back.PauseWhenNotForeground);
        Assert.Equal("wgc", back.Backend);
    }
}
