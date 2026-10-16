using Envoy.Core.Privacy;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests.Privacy;

public class PrivacyTextTests
{
    [Fact]
    public void Names_every_provider_in_chain()
    {
        var settings = new TranslationSettings { Primary = ProviderCatalog.DeepL, Fallbacks = [ProviderCatalog.EdgeFree] };
        var names = TranslatorFactory.ChainIds(settings).Select(ProviderCatalog.NameOf).ToList();
        var text = PrivacyText.ChatGoesToFor(names);
        Assert.Equal(3, names.Count);
        foreach (var name in names) Assert.Contains(name, text, StringComparison.Ordinal);
        Assert.Contains("DeepL then Microsoft Translator (free) then Google Translate (free)", text, StringComparison.Ordinal);
        Assert.Contains("DeepL", PrivacyText.CalloutFor(names), StringComparison.Ordinal);
    }

    [Fact]
    public void Default_chain_names_googles_keyless_endpoint()
    {
        var names = TranslatorFactory.ChainIds(new AppSettings().Translation).Select(ProviderCatalog.NameOf).ToList();
        Assert.StartsWith("Google Translate (free)", PrivacyText.Providers(names), StringComparison.Ordinal);
        Assert.Contains("unofficial", PrivacyText.KeylessNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Mentions_github_and_local_ocr()
    {
        var lines = PrivacyText.Lines(["Google Translate (free)"]);
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.Contains("GitHub", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("OCR", StringComparison.Ordinal) && l.Contains("speech", StringComparison.Ordinal));
        Assert.StartsWith("https://github.com/", PrivacyText.DocUrl, StringComparison.Ordinal);
        Assert.EndsWith("docs/privacy.md", PrivacyText.DocUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void IsAcknowledged_compares_versions()
    {
        Assert.False(PrivacyText.IsAcknowledged(0));
        Assert.False(PrivacyText.IsAcknowledged(PrivacyText.Version - 1));
        Assert.True(PrivacyText.IsAcknowledged(PrivacyText.Version));
        Assert.True(PrivacyText.IsAcknowledged(PrivacyText.Version + 1));
        Assert.False(PrivacyText.IsAcknowledged(new AppSettings().Onboarding.PrivacyAcknowledgedVersion)); // existing files start off
    }
}
