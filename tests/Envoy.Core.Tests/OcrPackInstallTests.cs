using Envoy.Core.Languages;
using Xunit;

namespace Envoy.Core.Tests;

public class OcrPackInstallTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    public void CapabilityName_ChineseUsesCode(string code)
    {
        var language = LanguageCatalog.Find(code)!;
        Assert.Equal($"Language.OCR~~~{code}~0.0.1.0", OcrPackInstall.CapabilityName(language));
    }

    [Fact]
    public void CapabilityName_OthersUseCatalogOcrTag()
    {
        foreach (var language in LanguageCatalog.All.Where(l => l.Code is not ("zh-CN" or "zh-TW")))
            Assert.Equal($"Language.OCR~~~{language.OcrTag}~0.0.1.0", OcrPackInstall.CapabilityName(language));
    }

    [Theory]
    [InlineData("en'; rm")]
    [InlineData("en US")]
    [InlineData("en\"")]
    [InlineData("")]
    public void CapabilityName_RejectsUnsafeTag(string tag)
    {
        var language = new Language("xx", "X", "X", tag, Script.Latin);
        Assert.Throws<ArgumentException>(() => OcrPackInstall.CapabilityName(language));
    }

    [Fact]
    public void PowerShellArguments_MatchesLegacyString()
    {
        Assert.Equal(
            "-NoProfile -Command \"Add-WindowsCapability -Online -Name 'Language.OCR~~~ko-KR~0.0.1.0'; Write-Host 'Done - restart Envoy to use it.'; Start-Sleep 4\"",
            OcrPackInstall.PowerShellArguments("ko"));
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("")]
    public void PowerShellArguments_UnknownCode_Throws(string code)
    {
        Assert.Throws<ArgumentException>(() => OcrPackInstall.PowerShellArguments(code));
    }
}
