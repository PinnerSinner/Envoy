using System.Collections.ObjectModel;
using System.Windows.Threading;
using Envoy.App.Pages;
using Envoy.App.Services;
using Envoy.Core.Diagnostics;
using Envoy.Core.Hotkeys;
using Envoy.Core.Languages;
using Envoy.Core.Mvvm;
using Envoy.Core.Onboarding;
using Envoy.Core.Privacy;
using Envoy.Core.Settings;
using Envoy.Core.Translation;

namespace Envoy.App.ViewModels;

/// <summary>
/// The setup guide (spec 07a, REQ-OUXA-1…12, 47). Navigation is Core's <see cref="OnboardingFlow"/>; every edit writes the
/// same <see cref="AppSettings"/> properties the settings pages use, through its own <see cref="SettingsAutoApplier"/>
/// into <c>AppHost.ApplySettings()</c>. The privacy box applies at once, so the translation gate is open before the Test
/// translation step runs. App-level actions go through <see cref="IShell"/>. UI thread only.
/// </summary>
public sealed class SetupWizardViewModel : ObservableObject, IDisposable
{
    private readonly IShell _shell;
    private readonly SettingsAutoApplier _applier;
    private readonly OnboardingFlow _flow;
    private IReadOnlyList<OcrRecommendation> _ocrRows = [];
    private IReadOnlyList<HotkeyRow> _hotkeyRows = [];
    private IReadOnlyList<StepItem> _attentionSteps = [];
    private string _testInput = TranslationTestText.SampleInput;
    private string _testOutput = "";
    private bool? _testSucceeded;
    private bool _finished;
    private bool _disposed;

    internal SetupWizardViewModel(AppHost host, IShell shell, Dispatcher dispatcher, OnboardingStep start)
    {
        Host = host;
        _shell = shell;
        _applier = new SettingsAutoApplier(dispatcher, TimeSpan.FromMilliseconds(400), ApplySettingsNow);
        _flow = new OnboardingFlow(OnboardingFlow.AllSteps, start);
        Steps = [.. _flow.Steps.Select(s => new StepItem(s))];
        CapturePreview = new CapturePreviewViewModel(host, dispatcher);

        NextCommand = new RelayCommand(Next, () => CanGoNext);
        BackCommand = new RelayCommand(Back, () => _flow.CanGoBack);
        SkipCommand = new RelayCommand(() => Finish(completed: false));
        GoToStepCommand = new RelayCommand<OnboardingStep>(GoTo);
        OpenPrivacyDetailsCommand = new RelayCommand(() => _shell.OpenUrl(PrivacyText.DocUrl));
        SelectRegionCommand = new RelayCommand(_shell.SelectRegion);
        RecheckDisplayCommand = new RelayCommand(RefreshDisplay);
        InstallOcrCommand = new RelayCommand<string>(InstallOcr);
        RecheckOcrCommand = new RelayCommand(RecheckOcr);
        TestTranslateCommand = new AsyncRelayCommand(TestTranslateAsync, ReportCommandError);
        OpenHotkeysCommand = new RelayCommand(() => _shell.ShowPage(PageIds.Hotkeys));

        _flow.Changed += OnStepChanged;
        Host.SettingsApplied += OnHostChanged;
        Host.GameWindowChanged += RefreshDisplay;
        Host.HotkeyResultsChanged += OnHostChanged;
        Host.RegionMoved += OnHostChanged;

        LoadOcrRows();
        _hotkeyRows = HotkeyCheatSheet.Build(Host.HotkeyResults);
        OnStepChanged();
    }

    public AppHost Host { get; }
    public AppSettings Settings => Host.Settings;
    public IReadOnlyList<Language> Languages { get; } = LanguageCatalog.All;

    /// <summary>Raised once by Finish (true) or Skip setup, Esc and the close button (false).</summary>
    public event Action<bool>? Finished;

    // ---- navigation ----

    public IReadOnlyList<StepItem> Steps { get; }
    public OnboardingStep Current => _flow.Current;
    public int StepCount => _flow.Count;
    public string CurrentTitle => OnboardingText.TitleOf(_flow.Current);
    public string StepText => OnboardingText.Counter(_flow.Index, _flow.Count);
    public string NextLabel => _flow.IsLast ? OnboardingText.FinishLabel : OnboardingText.NextLabel;
    public bool IsLast => _flow.IsLast;

    /// <summary>Next is disabled on Language &amp; privacy until "I understand" is ticked (REQ-OUXA-47).</summary>
    public bool CanGoNext => !(_flow.Current == OnboardingStep.Language && !PrivacyAcknowledged);

    public bool IsLanguageStep => _flow.Current == OnboardingStep.Language;
    public bool IsGameStep => _flow.Current == OnboardingStep.Game;
    public bool IsChatAreaStep => _flow.Current == OnboardingStep.ChatArea;
    public bool IsOcrStep => _flow.Current == OnboardingStep.OcrLanguages;
    public bool IsTestStep => _flow.Current == OnboardingStep.TestTranslation;
    public bool IsHotkeysStep => _flow.Current == OnboardingStep.Hotkeys;

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand SkipCommand { get; }

    /// <summary>Parameter: the <see cref="OnboardingStep"/> to show (the step list and "Go to step").</summary>
    public RelayCommand<OnboardingStep> GoToStepCommand { get; }

    // ---- Language & privacy ----

    /// <summary>"What leaves your PC", naming the provider chain as built.</summary>
    public IReadOnlyList<string> PrivacyLines => PrivacyText.Lines(Host.ActiveProviderNames);

    /// <summary>The "I understand" box: records <see cref="PrivacyText.Version"/> (or 0) and applies at once.</summary>
    public bool PrivacyAcknowledged
    {
        get => Host.PrivacyAcknowledged;
        set
        {
            if (value == Host.PrivacyAcknowledged) return;
            Host.SetPrivacyAcknowledged(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanGoNext));
            NextCommand.RaiseCanExecuteChanged();
            RefreshStatuses();
        }
    }

    public RelayCommand OpenPrivacyDetailsCommand { get; }

    // ---- Game & display ----

    public string BorderlessAdvice => OnboardingText.BorderlessAdvice;
    public DisplayModeVerdict DisplayVerdict => DisplayModeAdvisor.Classify(Host.GameWindow);
    public string DisplayVerdictText => OnboardingText.Describe(DisplayVerdict);
    public bool IsLikelyExclusive => DisplayVerdict == DisplayModeVerdict.LikelyExclusiveFullscreen;
    public RelayCommand RecheckDisplayCommand { get; }

    // ---- Chat area ----

    public RelayCommand SelectRegionCommand { get; }
    public string RegionText => MainViewModel.RegionTextFor(Host);
    public bool HasRegion => Host.Watcher.CurrentRegion is { IsEmpty: false }
        || Settings.Capture.Regions.Count > 0 || Settings.Capture.WindowRegions.Count > 0;

    /// <summary>This step's own live view; active only while the Chat area step is current (REQ-OUXA-6).</summary>
    public CapturePreviewViewModel CapturePreview { get; }

    // ---- Text recognition ----

    /// <summary>Your language first, then the wanted ones, then common opponent languages (REQ-OUXA-7).</summary>
    public IReadOnlyList<OcrRecommendation> OcrRows
    {
        get => _ocrRows;
        private set => SetProperty(ref _ocrRows, value);
    }

    public int MissingOcrCount => _ocrRows.Count(r => r.Wanted && !r.Installed);

    /// <summary>Parameter: the Envoy language code whose OCR pack to install.</summary>
    public RelayCommand<string> InstallOcrCommand { get; }

    /// <summary>Re-queries Windows and re-applies the settings (the OCR notice follows).</summary>
    public RelayCommand RecheckOcrCommand { get; }

    // ---- Test translation ----

    public string TestInput
    {
        get => _testInput;
        set => SetProperty(ref _testInput, value);
    }

    public string TestOutput
    {
        get => _testOutput;
        private set => SetProperty(ref _testOutput, value);
    }

    /// <summary>"This sends the sentence to {provider}." before anything is sent (REQ-OUXA-8).</summary>
    public string TestTarget => OnboardingText.TestTranslationTarget(Host.ActiveProviderNames is [var first, ..] ? first : "");

    public AsyncRelayCommand TestTranslateCommand { get; }

    // ---- Hotkeys ----

    public IReadOnlyList<HotkeyRow> HotkeyRows
    {
        get => _hotkeyRows;
        private set => SetProperty(ref _hotkeyRows, value);
    }

    public RelayCommand OpenHotkeysCommand { get; }

    // ---- summary on the last step (REQ-OUXA-11) ----

    public IReadOnlyList<StepItem> AttentionSteps
    {
        get => _attentionSteps;
        private set => SetProperty(ref _attentionSteps, value);
    }

    public string SummaryText => OnboardingText.Summary(_attentionSteps.Count);

    /// <summary>Makes <paramref name="step"/> current (ignored when it is not built).</summary>
    public void GoTo(OnboardingStep step) => _flow.GoTo(step);

    /// <summary>The window saw an edit to a settings control: apply it shortly (01's debounce).</summary>
    public void OnEdited() => _applier.Schedule();

    /// <summary>The window closed (close button, Esc or App): counts as Skip setup unless Finish or Skip already ran.</summary>
    public void OnWindowClosed() => Finish(completed: false);

    private void Next()
    {
        if (!CanGoNext) return;
        if (_flow.IsLast) Finish(completed: true);
        else _flow.Next();
    }

    private void Back() => _flow.Back();

    private void Finish(bool completed)
    {
        if (_finished) return;
        _finished = true;
        // A reset or "Delete all Envoy data" is never undone (contract §4).
        if (!Host.SaveSuppressed) _applier.FlushIfPending();
        Finished?.Invoke(completed);
    }

    private void OnStepChanged()
    {
        Settings.Onboarding.LastStep = _flow.Current.ToString();
        Host.ScheduleSave();
        CapturePreview.IsActive = !_disposed && _flow.Current == OnboardingStep.ChatArea;
        if (_flow.Current == OnboardingStep.Hotkeys) HotkeyRows = HotkeyCheatSheet.Build(Host.HotkeyResults);
        foreach (var name in new[]
                 {
                     nameof(Current), nameof(CurrentTitle), nameof(StepText), nameof(NextLabel), nameof(IsLast), nameof(CanGoNext),
                     nameof(IsLanguageStep), nameof(IsGameStep), nameof(IsChatAreaStep), nameof(IsOcrStep), nameof(IsTestStep),
                     nameof(IsHotkeysStep), nameof(TestTarget), nameof(RegionText), nameof(HasRegion),
                 })
            OnPropertyChanged(name);
        NextCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        RefreshDisplay();
        RefreshStatuses();
    }

    /// <summary>Settings applied, region chosen or hotkeys re-registered: re-read what the steps show.</summary>
    private void OnHostChanged()
    {
        if (_disposed) return;
        HotkeyRows = HotkeyCheatSheet.Build(Host.HotkeyResults);
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(HasRegion));
        OnPropertyChanged(nameof(PrivacyLines));
        OnPropertyChanged(nameof(PrivacyAcknowledged));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(TestTarget));
        NextCommand.RaiseCanExecuteChanged();
        RefreshStatuses();
    }

    private void RefreshDisplay()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(DisplayVerdict));
        OnPropertyChanged(nameof(DisplayVerdictText));
        OnPropertyChanged(nameof(IsLikelyExclusive));
    }

    /// <summary>The step list's status (REQ-OUXA-11) and the last step's attention summary.</summary>
    private void RefreshStatuses()
    {
        var facts = new OnboardingFacts(Host.PrivacyAcknowledged, DisplayVerdict, HasRegion, MissingOcrCount, _testSucceeded,
            HotkeyCheatSheet.Problems(Host.HotkeyResults));
        foreach (var item in Steps)
        {
            item.Status = OnboardingFlow.Evaluate(item.Step, facts, _flow.Visited.Contains(item.Step));
            item.IsCurrent = item.Step == _flow.Current;
        }
        AttentionSteps = [.. Steps.Where(s => s.NeedsAttention)];
        OnPropertyChanged(nameof(SummaryText));
    }

    private void LoadOcrRows()
    {
        IEnumerable<string> installed;
        try
        {
            installed = [.. OcrService.InstalledLanguages().Select(l => l.Code)];
        }
        catch (Exception ex)
        {
            // OCR unavailable on this edition of Windows: every language shows as Missing.
            Host.Log.For("Onboarding").Warn("Text recognition languages could not be listed", ex);
            installed = [];
        }
        OcrRows = OcrReadiness.Recommend(Settings.MyLanguage, Settings.Capture.OcrLanguages, installed);
        OnPropertyChanged(nameof(MissingOcrCount));
    }

    private void InstallOcr(string? code)
    {
        if (!string.IsNullOrEmpty(code)) _shell.InstallOcrPack(code);
    }

    private void RecheckOcr()
    {
        LoadOcrRows();
        _applier.ApplyNow();
    }

    private async Task TestTranslateAsync()
    {
        _applier.FlushIfPending();
        TestOutput = TranslationTestText.Pending;
        try
        {
            var result = await Host.Translator.TranslateAsync(new TranslationRequest(TestInput, Settings.MyLanguage));
            TestOutput = TranslationTestText.Describe(result, Settings.MyLanguage);
            _testSucceeded = true;
        }
        catch (Exception ex)
        {
            TestOutput = TranslationTestText.Failure(ex);
            _testSucceeded = false;
        }
        RefreshStatuses();
    }

    private void ApplySettingsNow()
    {
        if (_disposed) return;
        Host.ApplySettings();
        LoadOcrRows();
    }

    /// <summary>The one error sink for the guide's async command (contract §8.8): log at Error and a problem toast.</summary>
    private void ReportCommandError(Exception ex)
    {
        Host.Log.For("UI").Error("Command failed", ex);
        Host.ReportProblem(ProblemText.CommandFailed("command:" + ex.GetType().FullName));
    }

    /// <summary>Unsubscribes from the host and releases the live-view lease. Called by the window on close.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flow.Changed -= OnStepChanged;
        Host.SettingsApplied -= OnHostChanged;
        Host.GameWindowChanged -= RefreshDisplay;
        Host.HotkeyResultsChanged -= OnHostChanged;
        Host.RegionMoved -= OnHostChanged;
        CapturePreview.Dispose();
    }
}
