using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class TranslationRuntimeOptionsTests
{
    [Fact]
    public void Snapshot_is_independent_of_settings()
    {
        var s = new TranslationSettings { RequestTimeoutSeconds = 20, RememberTranslations = true, CacheMaxEntries = 500 };
        var snapshot = TranslationRuntimeOptions.From(s);
        s.RequestTimeoutSeconds = 5;
        s.RememberTranslations = false;
        s.CacheMaxEntries = 9000;
        Assert.Equal(TimeSpan.FromSeconds(20), snapshot.RequestTimeout);
        Assert.True(snapshot.RememberTranslations);
        Assert.Equal(500, snapshot.CacheMaxEntries);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-5, 3)]
    [InlineData(15, 15)]
    [InlineData(600, 60)]
    public void From_clamps_out_of_range_values(int seconds, int expected)
    {
        var o = TranslationRuntimeOptions.From(new TranslationSettings { RequestTimeoutSeconds = seconds, CacheMaxEntries = seconds * 1000 });
        Assert.Equal(TimeSpan.FromSeconds(expected), o.RequestTimeout);
        Assert.InRange(o.CacheMaxEntries, 100, 20_000);
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var d = TranslationRuntimeOptions.Default;
        Assert.Equal(TimeSpan.FromSeconds(15), d.RequestTimeout);
        Assert.False(d.RememberTranslations);
        Assert.Equal(2000, d.CacheMaxEntries);
    }
}
