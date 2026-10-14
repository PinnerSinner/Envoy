using System.Globalization;
using Envoy.Core.Text;
using Xunit;

namespace Envoy.Core.Tests;

public class TextTemplateTests
{
    [Fact]
    public void Format_ReplacesNamedPlaceholders()
    {
        var text = TextTemplate.Format("{who} sent {count} lines", CultureInfo.InvariantCulture, ("who", "Li"), ("count", 3));
        Assert.Equal("Li sent 3 lines", text);
    }

    [Fact]
    public void Format_AppliesFormatWithCulture()
    {
        Assert.Equal("1,234.5", TextTemplate.Format("{n:N1}", CultureInfo.InvariantCulture, ("n", 1234.5)));
        Assert.Equal("1.234,5", TextTemplate.Format("{n:N1}", CultureInfo.GetCultureInfo("de-DE"), ("n", 1234.5)));
        Assert.Equal("42", TextTemplate.Format("{ms:0}", CultureInfo.InvariantCulture, ("ms", 41.6)));
    }

    [Fact]
    public void Format_FormatMayContainColons()
    {
        var when = new DateTime(2026, 1, 2, 13, 45, 0);
        Assert.Equal("at 13:45", TextTemplate.Format("at {when:HH:mm}", CultureInfo.InvariantCulture, ("when", when)));
    }

    [Fact]
    public void Format_NullCultureUsesCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("0,5", TextTemplate.Format("{x}", null, ("x", 0.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Format_DoubledBracesAreLiteral()
    {
        Assert.Equal("{x} = 1 }", TextTemplate.Format("{{x}} = {x} }}", null, ("x", 1)));
    }

    [Fact]
    public void Format_NullValueIsEmpty()
    {
        Assert.Equal("[]", TextTemplate.Format("[{v}]", null, ("v", null)));
    }

    [Fact]
    public void Format_MatchesStringFormatForLegacyTemplates()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        var expected = string.Format(culture, "{0} lines · OCR {1:0} ms", 3, 41.6);
        Assert.Equal(expected, TextTemplate.Format("{lines} lines · OCR {ocrMs:0} ms", culture, ("lines", 3), ("ocrMs", 41.6)));
    }

    [Theory]
    [InlineData("{missing}")]
    [InlineData("{x} {missing}")]
    [InlineData("{}")]
    [InlineData("{x")]
    [InlineData("{x} }")]
    [InlineData("{:N0}")]
    public void Format_BadTemplate_Throws(string template)
    {
        Assert.Throws<FormatException>(() => TextTemplate.Format(template, null, ("x", 1)));
    }

    [Fact]
    public void Format_UnusedValue_Throws()
    {
        Assert.Throws<FormatException>(() => TextTemplate.Format("{x}", null, ("x", 1), ("y", 2)));
    }

    [Fact]
    public void Format_DuplicateValue_Throws()
    {
        Assert.Throws<FormatException>(() => TextTemplate.Format("{x}", null, ("x", 1), ("x", 2)));
    }

    [Fact]
    public void Format_NoPlaceholders_ReturnsTemplate()
    {
        Assert.Equal("plain", TextTemplate.Format("plain", null));
    }
}
