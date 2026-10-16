using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class InstallLayoutTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "envoy-layout-test", "Envoy"));
    private static readonly string InstalledExe = Path.Combine(Root, "current", "Envoy.exe");
    private static readonly string UpdateExe = Path.Combine(Root, "Update.exe");

    private static Func<string, bool> Files(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void Installed_layout_is_recognised() => Assert.True(InstallLayout.LooksInstalled(InstalledExe, Files(UpdateExe)));

    [Fact]
    public void Exe_nested_below_current_is_recognised()
        => Assert.True(InstallLayout.LooksInstalled(Path.Combine(Root, "current", "bin", "Envoy.exe"), Files(UpdateExe)));

    [Fact]
    public void Portable_exe_is_not_installed()
    {
        var portable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Downloads", "Envoy.exe"));
        Assert.False(InstallLayout.LooksInstalled(portable, Files(UpdateExe)));
        Assert.False(InstallLayout.ShouldRunVelopack(portable, ["--smoke-test"], Files(UpdateExe)));
    }

    [Fact]
    public void Current_folder_without_the_updater_is_not_installed() => Assert.False(InstallLayout.LooksInstalled(InstalledExe, Files()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unknown_process_path_is_not_installed(string? path) => Assert.False(InstallLayout.LooksInstalled(path, _ => true));

    [Theory]
    [InlineData("--veloapp-install", true)]
    [InlineData("--VELOAPP-uninstall", true)]
    [InlineData("--squirrel-updated", true)]
    [InlineData("--minimized", false)]
    public void Installer_hooks_always_run_velopack(string first, bool expected)
    {
        Assert.Equal(expected, InstallLayout.IsInstallerHook([first, "1.0.0"]));
        Assert.Equal(expected, InstallLayout.ShouldRunVelopack("Envoy.exe", [first, "1.0.0"], Files()));
    }

    [Fact]
    public void No_arguments_is_not_a_hook() => Assert.False(InstallLayout.IsInstallerHook([]));
}
