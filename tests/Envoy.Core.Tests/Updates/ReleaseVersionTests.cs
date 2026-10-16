using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2", 1, 2, 0)]
    [InlineData(" 10.20.30 ", 10, 20, 30)]
    public void Parses_plain_and_v_prefixed(string text, int major, int minor, int patch)
    {
        var v = ReleaseVersion.Parse(text);
        Assert.Equal((major, minor, patch), (v.Major, v.Minor, v.Patch));
        Assert.False(v.IsPrerelease);
    }

    [Fact]
    public void Parses_prerelease_identifiers()
    {
        var v = ReleaseVersion.Parse("v1.2.0-beta.1");
        Assert.Equal(["beta", "1"], v.Prerelease);
        Assert.True(v.IsPrerelease);
        Assert.Equal("1.2.0-beta.1", v.ToString());
    }

    [Fact]
    public void Drops_build_metadata()
    {
        var v = ReleaseVersion.Parse("1.2.3-rc.1+0abc123.sha");
        Assert.Equal("1.2.3-rc.1", v.ToString());
        Assert.Equal(ReleaseVersion.Parse("1.2.3-rc.1+other"), v);
        Assert.Equal("1.2.3", ReleaseVersion.Parse("1.2.3+build.5").ToString());
    }

    [Fact]
    public void Prerelease_sorts_before_release()
    {
        Assert.True(ReleaseVersion.Parse("1.0.0-beta.9") < ReleaseVersion.Parse("1.0.0"));
        Assert.True(ReleaseVersion.Parse("1.0.0") > ReleaseVersion.Parse("1.0.0-rc.1"));
        Assert.True(ReleaseVersion.Parse("0.9.9") < ReleaseVersion.Parse("1.0.0-alpha"));
    }

    [Fact]
    public void Numeric_identifiers_compare_numerically()
    {
        Assert.True(ReleaseVersion.Parse("1.0.0-beta.2") < ReleaseVersion.Parse("1.0.0-beta.10"));
        Assert.True(ReleaseVersion.Parse("1.2.10") > ReleaseVersion.Parse("1.2.9"));
    }

    [Fact]
    public void Alphanumeric_vs_numeric_identifiers()
    {
        // SemVer §11: 1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta < 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0-rc.1 < 1.0.0
        string[] order = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
        for (var i = 0; i < order.Length - 1; i++)
            Assert.True(ReleaseVersion.Parse(order[i]) < ReleaseVersion.Parse(order[i + 1]), $"{order[i]} < {order[i + 1]}");
        var shuffled = order.Reverse().Select(ReleaseVersion.Parse).OrderBy(v => v).Select(v => v.ToString());
        Assert.Equal(order, shuffled);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("1")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.x")]
    [InlineData("-1.2.3")]
    public void Rejects_garbage(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
        if (text is not null) Assert.Throws<FormatException>(() => ReleaseVersion.Parse(text));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.4.0-beta.2")]
    [InlineData("2.0.0-rc.1.x-y")]
    public void ToString_round_trips(string text) => Assert.Equal(text, ReleaseVersion.Parse(text).ToString());

    [Fact]
    public void CoreString_drops_prerelease() => Assert.Equal("1.2.0", ReleaseVersion.Parse("1.2.0-beta.1").CoreString);

    [Fact]
    public void Equality_and_operators()
    {
        var a = ReleaseVersion.Parse("1.2.3-beta.1");
        var b = new ReleaseVersion(1, 2, 3, "beta", "1");
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a <= b && a >= b);
        Assert.False(a != b);
        Assert.True(a > null);
        Assert.Equal(1, a.CompareTo(null));
    }

    [Fact]
    public void FromInformational_strips_source_revision() =>
        Assert.Equal("1.2.0-beta.1", ReleaseVersion.FromInformational("1.2.0-beta.1+0abcdef", new Version(1, 2, 0, 0)).ToString());

    [Fact]
    public void FromInformational_falls_back_to_assembly_version()
    {
        Assert.Equal("0.3.1", ReleaseVersion.FromInformational("dev build", new Version(0, 3, 1, 0)).ToString());
        Assert.Equal("1.0.0", ReleaseVersion.FromInformational(null, new Version(1, 0)).ToString());
    }

    [Fact]
    public void FromInformational_falls_back_to_zero() =>
        Assert.Equal("0.0.0", ReleaseVersion.FromInformational(null, null).ToString());
}
