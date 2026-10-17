using System.Globalization;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class AccessibilityTextTests
{
    [Theory]
    [InlineData(0.63, "Raised to 63% so text stays readable.")]
    [InlineData(1.0, "Raised to 100% so text stays readable.")]
    [InlineData(0.005, "Raised to 1% so text stays readable.")]
    [InlineData(1.7, "Raised to 100% so text stays readable.")]
    public void Raised_formats_percent(double alpha, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, AccessibilityText.Raised(alpha));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Templates_use_named_placeholders() =>
        Assert.DoesNotContain("{0}", AccessibilityText.OpacityRaised, StringComparison.Ordinal);
}
