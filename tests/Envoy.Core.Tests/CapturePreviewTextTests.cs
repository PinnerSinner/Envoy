using System.Globalization;
using Envoy.Core.Ocr;
using Xunit;

namespace Envoy.Core.Tests;

public class CapturePreviewTextTests
{
    [Fact]
    public void Lines_Empty_NoText()
    {
        Assert.Equal("(no text found)", CapturePreviewText.Lines([]));
    }

    [Fact]
    public void Lines_JoinsWithNewLine()
    {
        Assert.Equal("a" + Environment.NewLine + "b", CapturePreviewText.Lines(["a", "b"]));
    }

    [Fact]
    public void Stats_MatchesLegacyFormat()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("3 lines · OCR 42 ms", CapturePreviewText.Stats(3, TimeSpan.FromMilliseconds(41.6)));
            var lines = 3;
            var ocr = TimeSpan.FromMilliseconds(41.6);
            Assert.Equal($"{lines} lines · OCR {ocr.TotalMilliseconds:0} ms", CapturePreviewText.Stats(lines, ocr));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
