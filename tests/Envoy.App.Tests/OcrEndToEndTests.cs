using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace Envoy.App.Tests;

/// <summary>
/// The OCR end-to-end harness on the real Windows OCR engine (REQ-QSC-44, AC-QSC-27): every case passes, or reports
/// "Skipped (no recognizer)" when it is not required and Windows has no recognizer for its language. Each case appends
/// its Markdown row to <c>$OCR_E2E_SUMMARY</c>, which the app-tests job adds to its summary.
/// </summary>
public sealed class OcrEndToEndTests(ITestOutputHelper output)
{
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromMinutes(2);
    private static readonly object SummaryGate = new();

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in OcrEndToEnd.Cases) data.Add(c.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Case_passes_or_is_skipped(string id)
    {
        var c = OcrEndToEnd.Cases.Single(x => x.Id == id);
        var result = StaRunner.Run(() => OcrEndToEnd.RunAsync(c), CaseTimeout);

        output.WriteLine($"{c.Id}: {OcrEndToEnd.Describe(result.Outcome)} - {result.Detail}");
        foreach (var line in result.Recognized) output.WriteLine("  OCR: " + line);
        var summary = Environment.GetEnvironmentVariable("OCR_E2E_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            lock (SummaryGate) File.AppendAllText(summary, OcrEndToEnd.ToMarkdownRow(result) + Environment.NewLine);
        }

        if (c.Required)
            Assert.True(result.Outcome == OcrE2EOutcome.Passed, $"{c.Id}: {result.Detail}");
        else
            Assert.True(result.Outcome is OcrE2EOutcome.Passed or OcrE2EOutcome.SkippedNoRecognizer, $"{c.Id}: {result.Detail}");
    }

    [Fact]
    public void Required_cases_cover_english()
    {
        var english = OcrEndToEnd.Cases.Where(c => c.Required && c.OcrLanguage == "en-US").ToList();
        Assert.True(english.Count >= 6, $"only {english.Count} required en-US cases");
        Assert.Equal(new[] { 14.0, 16.0, 18.0 }, english.Select(c => c.SizePx).Distinct().Order());
        Assert.Equal(2, english.Select(c => c.Effect).Distinct().Count());
        Assert.All(OcrEndToEnd.Cases.Where(c => c.OcrLanguage != "en-US"), c => Assert.False(c.Required));
        Assert.Equal(OcrEndToEnd.Cases.Count, OcrEndToEnd.Cases.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Render_is_deterministic()
    {
        var c = OcrEndToEnd.Cases[0];
        var (first, second) = StaRunner.Run(() => (OcrEndToEnd.Render(c), OcrEndToEnd.Render(c)), CaseTimeout);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Width * first.Height * 4, first.Pixels.Length);
        Assert.True(first.Pixels.AsSpan().SequenceEqual(second.Pixels), "two renders of the same case differ");
    }

    [Fact]
    public void Markdown_rows_name_the_outcome()
    {
        var rows = OcrEndToEnd.ToMarkdown(
        [
            new OcrE2EResult("a", OcrE2EOutcome.Passed, [], "3 of 3 lines read"),
            new OcrE2EResult("b", OcrE2EOutcome.SkippedNoRecognizer, [], "no Windows OCR recognizer for ja-JP"),
            new OcrE2EResult("c", OcrE2EOutcome.Failed, [], "x | y"),
        ]);
        Assert.Contains("| case | outcome | detail |", rows);
        Assert.Contains("| a | Passed | 3 of 3 lines read |", rows);
        Assert.Contains("| b | Skipped (no recognizer) | no Windows OCR recognizer for ja-JP |", rows);
        Assert.Contains("| c | Failed | x \\| y |", rows);
    }
}
