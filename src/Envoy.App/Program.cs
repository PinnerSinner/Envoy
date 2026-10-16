using Envoy.App.Services;
using Envoy.Core.Updates;
using Velopack;

namespace Envoy.App;

/// <summary>
/// The entry point (contract §4 step 1). Velopack runs before any WPF object exists: when Setup.exe or the updater
/// starts Envoy with a <c>--veloapp-*</c> hook argument, the hook runs and the process exits here. A copy that is not
/// installed (portable exe, Debug, <c>--smoke-test</c>) skips Velopack entirely, because its locator opens a log file
/// outside the data folder (REQ-DIST-10), and WPF starts as before.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Velopack's updater started this process after applying (or failing to apply) an update; start-up slot 5 then
    /// does not apply a pending package again (REQ-DIST-12). Read before <c>VelopackApp.Run</c>, which clears it.
    /// </summary>
    public static bool StartedByUpdater { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartedByUpdater = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PendingUpdatePolicy.RestartedByUpdaterVariable));
        if (InstallLayout.ShouldRunVelopack(Environment.ProcessPath, args, File.Exists))
        {
            VelopackApp.Build()
                // A downloaded update is applied at OnStartup slot 5, after the single-instance mutex (REQ-DIST-12).
                .SetAutoApplyOnStartup(false)
                // Uninstalling removes the "start at sign-in" value; the data folder is never touched (REQ-DIST-11).
                .OnBeforeUninstallFastCallback(_ => StartupRegistration.Apply(false))
                .SetArgs(args)
                .Run();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
