using System.Globalization;
using Envoy.App.Services;
using Envoy.Core.Diagnostics;
using Envoy.Core.Mvvm;

namespace Envoy.App.ViewModels;

/// <summary>About › Troubleshooting and the safe-mode banner (REQ-REL-27, 40). App actions go through <see cref="IShell"/>.</summary>
public sealed partial class MainViewModel
{
    private string _diagnosticsStatus = "";
    private string _recentProblemsKey = "";
    private IReadOnlyList<string> _recentProblems = [];
    private string _lastCrashText = "";
    private bool _hasLastCrash;

    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand ExportDiagnosticsCommand { get; }
    public RelayCommand RestartSafeModeCommand { get; }
    public RelayCommand RestartNormallyCommand { get; }

    /// <summary>Confirms, keeps a copy, resets settings and restarts (REQ-REL-38).</summary>
    public RelayCommand ResetSettingsCommand { get; }

    /// <summary>Confirms, deletes every file in the data inventory and restarts (REQ-REL-44).</summary>
    public RelayCommand DeleteAllDataCommand { get; }

    /// <summary>Deletes last-crash.json (REQ-REL-28).</summary>
    public RelayCommand DismissCrashCommand { get; }

    public bool IsSafeMode => Host.IsSafeMode;
    public string SafeModeBannerText => SafeModeText.Banner;
    public string SafeModeReasonText => SafeModeText.Reason(Host.SafeMode);
    public string DiagnosticsPrivacyText => DiagnosticsText.Privacy;

    /// <summary>The result of the last Copy or Export, e.g. "Copied. Paste it into your bug report."</summary>
    public string DiagnosticsStatus
    {
        get => _diagnosticsStatus;
        private set => SetProperty(ref _diagnosticsStatus, value);
    }

    /// <summary>The 10 most recent Warning-or-higher log lines, newest first (REQ-REL-28).</summary>
    public IReadOnlyList<string> RecentProblems
    {
        get => _recentProblems;
        private set => SetProperty(ref _recentProblems, value);
    }

    public bool HasRecentProblems => RecentProblems.Count > 0;

    public string LastCrashText
    {
        get => _lastCrashText;
        private set => SetProperty(ref _lastCrashText, value);
    }

    public bool HasLastCrash
    {
        get => _hasLastCrash;
        private set => SetProperty(ref _hasLastCrash, value);
    }

    /// <summary>Re-reads the recent problems and the crash marker; called by the 1 s status refresh.</summary>
    public void RefreshTroubleshooting()
    {
        var entries = Host.RecentLog?.Snapshot(LogLevel.Warning, 10) ?? [];
        var key = entries.Count == 0 ? "" : entries.Count.ToString(CultureInfo.InvariantCulture) + entries[^1].Time.Ticks.ToString(CultureInfo.InvariantCulture);
        if (key != _recentProblemsKey)
        {
            _recentProblemsKey = key;
            RecentProblems = [.. entries.Reverse().Select(LogFormatter.FormatLine)];
            Notify(nameof(HasRecentProblems));
        }
        var crash = CrashMarker.TryRead(Host.CrashMarkerPath);
        HasLastCrash = crash is not null;
        LastCrashText = DiagnosticsText.LastCrash(crash);
    }

    private void CopyDiagnostics()
    {
        var text = Host.BuildDiagnosticsText(DiagnosticsCollector.Collect(Host));
        DiagnosticsStatus = _shell.CopyText(text) ? DiagnosticsText.Copied : "";
    }

    private void ExportDiagnostics()
    {
        var name = "envoy-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip";
        var path = _shell.ChooseExportPath(name);
        if (path is null) return;
        try
        {
            DiagnosticsCollector.ExportBundle(Host, path);
            DiagnosticsStatus = DiagnosticsText.Exported(path);
            _shell.RevealFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Host.Log.For("Diagnostics").Warn("Export failed", ex);
            Host.ReportProblem(ProblemText.ExportFailed);
        }
    }

    private void DismissCrash()
    {
        try
        {
            File.Delete(Host.CrashMarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Host.Log.For("Diagnostics").Warn("Could not delete the crash record", ex);
        }
        RefreshTroubleshooting();
    }
}
