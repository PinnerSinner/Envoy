using Envoy.Core.Chat;
using Envoy.Core.Ocr;
using Envoy.Core.Pipeline;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Envoy.Core.Tests.Golden;

/// <summary>
/// The golden corpus (REQ-CHAT-44): every <c>Golden/Captures/**/*.capture.txt</c> is run frame by frame through
/// <see cref="OcrMerger.MergeLines"/> and <see cref="ChatPipeline.ProcessAsync(IReadOnlyList{OcrLine}, CancellationToken)"/>
/// with a fake translator and a fake clock, and the emitted messages must match each frame's <c>@expect</c> rows exactly.
/// </summary>
public class ChatGoldenTests(ITestOutputHelper output)
{
    /// <summary>The fixtures REQ-CHAT-44 requires, plus those added with their SHOULDs.</summary>
    public static readonly string[] Required =
    [
        "latin-basic", "latin-wrapped", "zh-wrapped", "ja-wrapped", "ko-wrapped", "ru-wrapped", "mixed-passes", "scroll-up",
        "scroll-orphan", "orphan-unknown", "geometry-breaks", "system-patterns", "own-and-muted-wrapped", "symbol-junk",
        // SHOULD features that were built
        "repeat-in-capture", "name-misread", "system-known-player", "fullwidth",
    ];

    private static string Root => Path.Combine(AppContext.BaseDirectory, "Golden", "Captures");

    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(Root, "*.capture.txt", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Fixture_matches_expected(string file)
    {
        var fixture = CaptureFixture.Parse(await File.ReadAllTextAsync(Path.Combine(Root, file)));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var pipeline = new ChatPipeline(new FakeTranslator(), BaseOptions(fixture), clock);
        foreach (var name in List(fixture.Directives.GetValueOrDefault("players"))) pipeline.Players.Observe(name, null);

        var settings = new Dictionary<string, string>(fixture.Settings, StringComparer.Ordinal);
        for (var i = 0; i < fixture.Frames.Count; i++)
        {
            var frame = fixture.Frames[i];
            foreach (var (key, value) in frame.Settings) settings[key] = value;
            pipeline.Options = Apply(BaseOptions(fixture), settings);
            clock.Advance(TimeSpan.FromSeconds(1));

            var lines = OcrMerger.MergeLines(frame.Passes, fixture.Language);
            var emitted = await pipeline.ProcessAsync(lines, CancellationToken.None);
            var actual = emitted.Select(m => CaptureFixture.Row(m.Chat)).ToList();
            if (!actual.SequenceEqual(frame.Expect, StringComparer.Ordinal))
            {
                output.WriteLine($"{file}, frame {i + 1}: actual rows (paste under @expect):");
                foreach (var row in actual.DefaultIfEmpty("(none)")) output.WriteLine(row);
            }
            Assert.Equal(frame.Expect, actual);
        }
    }

    [Fact]
    public void Corpus_has_required_coverage()
    {
        var present = Fixtures().Select(f => (string)f[0]).ToHashSet(StringComparer.Ordinal);
        var missing = Required.Where(r => !present.Contains(r + ".capture.txt")).ToList();
        Assert.True(missing.Count == 0, "missing golden fixtures: " + string.Join(", ", missing));
        Assert.True(Required.Length >= 14);
    }

    private static ChatPipelineOptions BaseOptions(CaptureFixture fixture) => new()
    {
        MyPlayerName = fixture.Directives.GetValueOrDefault("me") ?? "",
        MyLanguage = fixture.Language,
        MutedPlayers = List(fixture.Directives.GetValueOrDefault("muted")),
        SystemLinePatterns = fixture.Patterns,
    };

    private static ChatPipelineOptions Apply(ChatPipelineOptions options, IReadOnlyDictionary<string, string> settings)
    {
        foreach (var (key, value) in settings)
        {
            options = key switch
            {
                "hideSystem" => options with { HideSystemLines = bool.Parse(value) },
                "stitch" => options with { StitchWrappedLines = bool.Parse(value) },
                "clean" => options with { CleanSymbols = bool.Parse(value) },
                "byName" => options with { DetectSystemLinesByPlayerName = bool.Parse(value) },
                "profile" => options with { Format = ChatFormatCatalog.BuiltIn.Find(value) ?? throw new InvalidOperationException($"unknown profile {value}") },
                _ => throw new InvalidOperationException($"unknown @set key {key}"),
            };
        }
        return options;
    }

    private static string[] List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
