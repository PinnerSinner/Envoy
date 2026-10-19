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
    public void PowerShellArguments_InstallsEveryPackInOneRun_AndSaysWhatItIsDoing()
    {
        var args = OcrPackInstall.PowerShellArguments(["zh-CN", "ko", "ja", "ru"]);
        Assert.StartsWith("-NoProfile -ExecutionPolicy Bypass -Command \"", args, StringComparison.Ordinal);
        Assert.EndsWith("\"", args, StringComparison.Ordinal);
        Assert.Contains("$caps = @('Language.OCR~~~zh-CN~0.0.1.0','Language.OCR~~~ko-KR~0.0.1.0','Language.OCR~~~ja-JP~0.0.1.0','Language.OCR~~~ru-RU~0.0.1.0')", args, StringComparison.Ordinal);
        Assert.Contains("$names = @('Chinese (Simplified)','Korean','Japanese','Russian')", args, StringComparison.Ordinal);
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", args, StringComparison.Ordinal);
        Assert.Contains("can take a few minutes", args, StringComparison.Ordinal);
        // Exactly one pair of double quotes: the -Command wrapper. Nothing inside can close it.
        Assert.Equal(2, args.Count(c => c == '"'));
    }

    [Fact]
    public void PowerShellArguments_OneLanguage_StillUsesTheSameScript()
    {
        Assert.Equal(OcrPackInstall.PowerShellArguments(["ko"]), OcrPackInstall.PowerShellArguments("ko"));
        Assert.Contains("$caps = @('Language.OCR~~~ko-KR~0.0.1.0')", OcrPackInstall.PowerShellArguments("ko"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("")]
    public void PowerShellArguments_UnknownCode_Throws(string code)
    {
        Assert.Throws<ArgumentException>(() => OcrPackInstall.PowerShellArguments(code));
    }
}
