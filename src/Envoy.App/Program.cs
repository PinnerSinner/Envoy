using Envoy.App.Services;
using Velopack;

namespace Envoy.App;

/// <summary>
/// The entry point (contract §4 step 1). Velopack runs before any WPF object exists: when Setup.exe or the updater
/// starts Envoy with a <c>--veloapp-*</c> hook argument, the hook runs and the process exits here. Otherwise
/// <c>Run()</c> returns at once (portable exe, Debug, <c>--smoke-test</c>) and WPF starts as before.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            // A downloaded update is applied at OnStartup slot 5, after the single-instance mutex (REQ-DIST-12).
            .SetAutoApplyOnStartup(false)
            // Uninstalling removes the "start at sign-in" value; the data folder is never touched (REQ-DIST-11).
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Apply(false))
            .SetArgs(args)
            .Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
