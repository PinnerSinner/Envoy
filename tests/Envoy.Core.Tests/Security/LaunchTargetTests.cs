using Envoy.Core.Languages;
using Envoy.Core.Security;
using Xunit;

namespace Envoy.Core.Tests.Security;

public class LaunchTargetTests
{
    private static readonly string Data = Path.Combine(Path.GetTempPath(), "envoy-data-test");

    [Theory]
    [InlineData("https://github.com/PinnerSinner/Envoy")]
    [InlineData("http://localhost:5000/")]
    public void Web_urls_are_allowed(string url) => Assert.Equal(LaunchKind.WebUrl, LaunchTarget.Classify(url, Data));

    [Fact]
    public void Settings_uri_is_allowed() =>
        Assert.Equal(LaunchKind.SettingsUri, LaunchTarget.Classify("ms-settings:regionlanguage", Data));

    [Fact]
    public void Paths_inside_data_folder_are_allowed()
    {
        Assert.Equal(LaunchKind.DataPath, LaunchTarget.Classify(Data, Data));
        Assert.Equal(LaunchKind.DataPath, LaunchTarget.Classify(Path.Combine(Data, "logs"), Data));
        Assert.Equal(LaunchKind.DataPath, LaunchTarget.Classify(Path.Combine(Data, "phrases.json"), Data));
        Assert.Equal(LaunchKind.DataPath, LaunchTarget.Classify(Data + Path.DirectorySeparatorChar, Data));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    [InlineData(".lnk")]
    [InlineData(".scr")]
    [InlineData(".js")]
    [InlineData(".vbs")]
    [InlineData(".msi")]
    [InlineData(".hta")]
    [InlineData(".com")]
    [InlineData(".url")]
    [InlineData(".EXE")]
    public void Executables_inside_data_folder_are_rejected(string extension)
    {
        Assert.Equal(LaunchKind.Rejected, LaunchTarget.Classify(Path.Combine(Data, "x" + extension), Data));
        Assert.Equal(LaunchKind.Rejected, LaunchTarget.Classify(Path.Combine(Data, "x" + extension + ". "), Data));
    }

    [Fact]
    public void Paths_escaping_or_beside_data_folder_are_rejected()
    {
        Assert.Equal(LaunchKind.Rejected, LaunchTarget.Classify(Path.Combine(Data, "..", "x.json"), Data));
        Assert.Equal(LaunchKind.Rejected, LaunchTarget.Classify(Path.Combine(Data + "2", "x.json"), Data));
        Assert.Equal(LaunchKind.Rejected, LaunchTarget.Classify(Path.GetTempPath(), Data));
        Assert.Equal(LaunchKind.Rejected, LaunchTarget.Classify(Path.Combine(Data, "a.json:x.exe"), Data));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData(@"\\server\share\a.json")]
    [InlineData("//server/share")]
    [InlineData("javascript:alert(1)")]
    [InlineData("shell:startup")]
    [InlineData("search-ms:query=x")]
    [InlineData("ms-msdt:/id")]
    [InlineData("relative/a.json")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("https://user:pw@example.com")]
    [InlineData("ms-settings:x\" & calc")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://example.com/\n")]
    public void Other_schemes_and_forms_are_rejected(string? target) =>
        Assert.Equal(LaunchKind.Rejected, LaunchTarget.Classify(target, Data));

    [Fact]
    public void Restart_arguments_map_flags_only()
    {
        Assert.Empty(LaunchTarget.RestartArguments(RestartFlags.None));
        Assert.Equal(["--safe-mode"], LaunchTarget.RestartArguments(RestartFlags.SafeMode));
        Assert.Equal(["--minimized"], LaunchTarget.RestartArguments(RestartFlags.Minimized));
        Assert.Equal(["--safe-mode", "--minimized"], LaunchTarget.RestartArguments(RestartFlags.SafeMode | RestartFlags.Minimized));
        Assert.Throws<ArgumentOutOfRangeException>(() => LaunchTarget.RestartArguments((RestartFlags)8));
    }

    [Fact]
    public void Reveal_arguments_need_a_plain_full_path()
    {
        var full = Path.Combine(Data, "export.zip");
        Assert.Equal($"/select,\"{full}\"", LaunchTarget.RevealArguments(full));
        Assert.Null(LaunchTarget.RevealArguments("relative/export.zip"));
        Assert.Null(LaunchTarget.RevealArguments(@"\\server\share\export.zip"));
        Assert.Null(LaunchTarget.RevealArguments(Path.Combine(Data, "a\"b.zip")));
        Assert.Null(LaunchTarget.RevealArguments(Path.Combine(Data, "a\nb.zip")));
        Assert.Null(LaunchTarget.RevealArguments(null));
    }

    [Fact]
    public void Ocr_install_arguments_only_for_catalog_languages()
    {
        Assert.Equal(OcrPackInstall.PowerShellArguments("ja"), LaunchTarget.OcrInstallArguments("ja"));
        Assert.Null(LaunchTarget.OcrInstallArguments("xx"));
        Assert.Null(LaunchTarget.OcrInstallArguments("ja'; calc"));
        Assert.Null(LaunchTarget.OcrInstallArguments(null));
    }
}
