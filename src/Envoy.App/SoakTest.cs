using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Envoy.App.Interop;
using Envoy.Core.Capture;
using Envoy.Core.Diagnostics;
using Envoy.Core.Settings;
using Envoy.Core.Translation;

namespace Envoy.App;

/// <summary>
/// <c>Envoy.exe --soak &lt;minutes&gt; [log-path]</c> (REQ-CAP-43, contract §8.5): runs a real host's watcher on a synthetic
/// chat window that gains a line every 3 s, logs resource use every minute, and exits 1 when anything grows by more than
/// 20 % between minute 5 and the end (<see cref="SoakBudget"/>). Offline: the pipeline echoes instead of translating.
/// Not part of CI; a 120-minute run is a step of the pre-release pass (docs/releasing.md, M-CAP-6).
/// </summary>
internal static class SoakTest
{
    public const string Flag = "--soak";

    private static readonly string[] ChatLines =
    [
        "[All] Bob: nice wall, rams incoming",
        "[All] 小明: 你好，再来一局？",
        "[Team] 전략가: 성 올라가요",
        "[All] Сокол: хорошая стена, но я иду на таран",
        "[All] Pierre: bien joué, une autre ?",
        "[Team] 龍騎士: 金が足りない",
    ];

    private static string _logPath = "";

    public static async void Run(App app, string[] args, LogHub log)
    {
        var soakLog = log.For("Soak");
        var minutes = MinutesArg(args);
        _logPath = LogPathArg(args) ?? Path.Combine(Environment.CurrentDirectory, "soak-test.log");
        var root = Path.Combine(Path.GetTempPath(), $"envoy-soak-{Guid.NewGuid():N}");
        var data = Path.Combine(root, "data");
        var exitCode = 0;
        AppHost? host = null;
        Window? chat = null;
        try
        {
            Write($"soak: {minutes} min, data {data}");
            host = new AppHost(Path.Combine(data, "settings.json"), app.Dispatcher, new AppHostOptions { DataFolder = data, Log = log });
            host.Pipeline.Translator = new EchoTranslator(TimeSpan.FromMilliseconds(50));

            var lines = new TextBlock { FontSize = 18, Margin = new Thickness(16), TextWrapping = TextWrapping.NoWrap };
            lines.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            chat = new Window
            {
                Title = "Envoy soak chat",
                Width = 720,
                Height = 300,
                Left = 60,
                Top = 60,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = true,
                ShowActivated = false,
                Content = lines,
            };
            chat.SetResourceReference(Control.BackgroundProperty, "Bg.Panel");
            chat.Show();
            await Pump();

            host.Settings.Capture.OnlyWhileGameRuns = false;
            var a = lines.PointToScreen(new Point(0, 0));
            var b = lines.PointToScreen(new Point(lines.ActualWidth, lines.ActualHeight));
            host.SetRegion(new ScreenRegion((int)a.X, (int)a.Y, (int)(b.X - a.X), (int)(b.Y - a.Y))); // starts the watcher

            var samples = new List<SoakSample>();
            var shown = new List<string>();
            var clock = Stopwatch.StartNew();
            var nextLine = TimeSpan.Zero;
            var nextSample = TimeSpan.FromMinutes(1);
            var lineNumber = 0;
            while (clock.Elapsed < TimeSpan.FromMinutes(minutes))
            {
                if (clock.Elapsed >= nextLine)
                {
                    shown.Add(ChatLines[lineNumber % ChatLines.Length] + " #" + lineNumber.ToString(CultureInfo.InvariantCulture));
                    if (shown.Count > 8) shown.RemoveAt(0);
                    lines.Text = string.Join(Environment.NewLine, shown);
                    lineNumber++;
                    nextLine += TimeSpan.FromSeconds(3);
                }
                if (clock.Elapsed >= nextSample)
                {
                    var sample = Sample((int)Math.Round(clock.Elapsed.TotalMinutes), host);
                    samples.Add(sample);
                    Write(string.Create(CultureInfo.InvariantCulture,
                        $"soak minute {sample.Minute}: private {sample.PrivateBytes} B, handles {sample.Handles}, gdi {sample.GdiObjects}, user {sample.UserObjects}, pool {sample.PoolOutstanding} out / {sample.PoolRetainedBytes} B, history {sample.History}, players {sample.Players}; {CaptureText.Stats(host.Watcher.Metrics)}"));
                    nextSample += TimeSpan.FromMinutes(1);
                }
                await Task.Delay(250);
            }

            var violations = SoakBudget.Violations(samples);
            foreach (var v in violations) Write("soak violation: " + v);
            exitCode = violations.Count == 0 ? 0 : 1;
            Write(exitCode == 0 ? "SOAK TEST PASSED" : "SOAK TEST FAILED");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            soakLog.Error("Soak run failed", ex);
            Write("SOAK TEST FAILED: " + ex);
        }
        finally
        {
            chat?.Close();
            host?.Dispose();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                soakLog.Warn("Could not delete the soak data folder", ex);
            }
            log.Flush();
        }
        app.Shutdown(exitCode);
    }

    private static SoakSample Sample(int minute, AppHost host)
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        return new SoakSample(minute, self.PrivateMemorySize64, self.HandleCount,
            (int)Native.GetGuiResources(self.Handle, Native.GR_GDIOBJECTS), (int)Native.GetGuiResources(self.Handle, Native.GR_USEROBJECTS),
            host.Watcher.Pool.Outstanding, host.Watcher.Pool.RetainedBytesHighWater, host.History.Count, host.Players.Count);
    }

    private static int MinutesArg(string[] args)
    {
        var i = Array.IndexOf(args, Flag);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 10;
    }

    private static string? LogPathArg(string[] args)
    {
        var i = Array.IndexOf(args, Flag);
        return i >= 0 && i + 2 < args.Length && !args[i + 2].StartsWith("--", StringComparison.Ordinal) ? args[i + 2] : null;
    }

    private static void Write(string line)
    {
        try { File.AppendAllText(_logPath, line + Environment.NewLine); }
        catch (IOException) { }
    }

    private static async Task Pump() =>
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
}

/// <summary>
/// An offline translator for the smoke and soak runs: returns the text unchanged after <paramref name="delay"/>, so a
/// watcher can run end to end without any network request.
/// </summary>
internal sealed class EchoTranslator(TimeSpan delay) : ITranslator
{
    public string Id => "echo";

    public bool IsMetered => false;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return new TranslationResult(request.Text, null, Id);
    }
}
