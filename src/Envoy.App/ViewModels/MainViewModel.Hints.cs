using System.Collections.ObjectModel;
using Envoy.App.Pages;
using Envoy.App.Services;
using Envoy.Core.Capture;
using Envoy.Core.Hotkeys;
using Envoy.Core.Languages;
using Envoy.Core.Mvvm;
using Envoy.Core.Notifications;
using Envoy.Core.Onboarding;
using Envoy.Core.Privacy;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Ux;

namespace Envoy.App.ViewModels;

/// <summary>
/// The War Room's notice list and hint slot (contract §8.6, REQ-OUXA-48), the page callouts (REQ-OUXA-22, 23) and the
/// guide and privacy entry points (spec 07a). Hint text comes from Core's <see cref="PageHints"/>; every button runs a
/// command here or an <see cref="IShell"/> call.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Notice rows shown before "Show all (n)".</summary>
    public const int VisibleNoticeLimit = 3;

    private bool _showAllNotices;

    public RelayCommand<string> RunHintActionCommand { get; private set; } = null!;

    /// <summary>Parameter: a <see cref="NoticeActions"/> id.</summary>
    public RelayCommand<string> RunNoticeActionCommand { get; private set; } = null!;

    /// <summary>Parameter: the notice key ("Don't remind me", REQ-OUXA-20).</summary>
    public RelayCommand<string> DismissNoticeCommand { get; private set; } = null!;

    /// <summary>"Turn on translation" on the War Room privacy callout (REQ-OUXA-47): records the acknowledgement and applies it at once.</summary>
    public RelayCommand AcknowledgePrivacyCommand { get; private set; } = null!;

    /// <summary>Parameter: an <c>OnboardingStep</c> name, or null to resume at <c>Onboarding.LastStep</c> (REQ-OUXA-12).</summary>
    public RelayCommand<string> OpenSetupGuideCommand { get; private set; } = null!;

    // ---- War Room: notice list (slot 3) ----

    /// <summary>The active advisories, newest first (never updates; 06's banner shows those). Empty in safe mode.</summary>
    public ObservableCollection<Notice> Notices { get; } = [];

    /// <summary>At most <see cref="VisibleNoticeLimit"/> rows unless "Show all" is on.</summary>
    public IReadOnlyList<Notice> VisibleNotices => ShowAllNotices ? [.. Notices] : [.. Notices.Take(VisibleNoticeLimit)];

    public bool HasNotices => Notices.Count > 0;
    public int NoticeCount => Notices.Count;
    public bool HasMoreNotices => Notices.Count > VisibleNoticeLimit;

    /// <summary>The "Show all (n)" toggle. Not a setting.</summary>
    public bool ShowAllNotices
    {
        get => _showAllNotices;
        set
        {
            if (SetProperty(ref _showAllNotices, value)) Notify(nameof(VisibleNotices));
        }
    }

    // ---- War Room: hint slot (slot 4) ----

    /// <summary>Privacy while unacknowledged, then "Finish setting up Envoy", then the capture hint (contract §8.6).</summary>
    public HintSlotKind HintSlot => WarRoomSlots.Choose(Host.PrivacyAcknowledged, Settings.Onboarding.Completed, CaptureHint);

    public bool HasHintSlot => HintSlot != HintSlotKind.None;
    public bool IsPrivacySlot => HintSlot == HintSlotKind.Privacy;
    public bool IsFinishSetupSlot => HintSlot == HintSlotKind.FinishSetup;
    public bool IsPageHintSlot => HintSlot == HintSlotKind.PageHint;

    /// <summary>The War Room capture hint (MUST): automatic pauses never offer Resume.</summary>
    public PageHint? CaptureHint => PageHints.Capture(CaptureConditionFor(Host.Watcher));

    /// <summary>"Envoy sends chat to … Translation stays off until you confirm."</summary>
    public string PrivacyCalloutText => PrivacyText.CalloutFor(Host.ActiveProviderNames);

    public string FinishSetupText => OnboardingText.FinishSetup;
    public string FinishSetupDetail => OnboardingText.FinishSetupDetail;

    /// <summary>About › Privacy: "What leaves your PC", naming the chain as built.</summary>
    public IReadOnlyList<string> PrivacyLines => PrivacyText.Lines(Host.ActiveProviderNames);

    // ---- War Room empty states and page callouts (SHOULD, REQ-OUXA-22) ----

    public PageHint? DispatchesHint => PageHints.Dispatches(Host.History.Count);
    public PageHint? PlayersHint => PageHints.Players(Host.Players.Count);

    /// <summary>Phrasebook: why phrases.json was not loaded (MUST, REQ-OUXA-23).</summary>
    public PageHint? PhrasebookHint => PageHints.Phrasebook(Host.PhrasesLoadError);

    /// <summary>Reading Chat: no region, OCR unavailable or wanted packs missing.</summary>
    public PageHint? ReadingChatHint
    {
        get
        {
            if (!HasRegion) return PageHints.Capture(new CaptureCondition(CaptureConditionKind.NoRegion));
            var installed = OcrLanguages.Where(l => l.Installed).Select(l => l.Code).ToList();
            var missing = OcrReadiness.MissingWanted(Settings.Capture.OcrLanguages, Settings.MyLanguage, installed).Count;
            return PageHints.OcrAvailability(OcrLanguages.Count, missing);
        }
    }

    /// <summary>Translation: the first provider in the chosen chain that has no key or address.</summary>
    public PageHint? TranslationHint
    {
        get
        {
            var t = Settings.Translation;
            foreach (var id in new[] { t.Primary }.Concat(t.Fallbacks).Where(id => !string.IsNullOrEmpty(id)))
            {
                if (!ProviderCredentials.IsSet(id, t)) return PageHints.ProviderCredentials(ProviderCatalog.NameOf(id), hasCredential: false);
            }
            return null;
        }
    }

    public PageHint? OverlayHint => PageHints.OverlayHidden(Settings.Overlay.Visible, Settings.Hotkeys.ToggleOverlay ?? "");

    public PageHint? ReplyHint =>
        PageHints.ReplyHotkey(Host.HotkeyResults.FirstOrDefault(r => r.ActionId == nameof(HotkeySettings.Compose)));

    public PageHint? HotkeysHint => PageHints.Hotkeys(Host.HotkeyResults);

    /// <summary>MainViewModel maps <see cref="WatcherState"/> in one switch (spec 07a §Core: page hints).</summary>
    internal static CaptureCondition CaptureConditionFor(ChatWatcher watcher) => watcher.State switch
    {
        WatcherState.Stopped => new(CaptureConditionKind.Stopped),
        WatcherState.NoRegion => new(CaptureConditionKind.NoRegion),
        WatcherState.Running => new(CaptureConditionKind.Running),
        WatcherState.Idle => new(CaptureConditionKind.WaitingForGame),
        WatcherState.Paused => CaptureCondition.AutoPaused(watcher.PauseReason),
        WatcherState.Error => new(CaptureConditionKind.Error, Error: watcher.LastError),
        _ => new(CaptureConditionKind.Stopped),
    };

    private void CreateHintCommands()
    {
        RunHintActionCommand = new RelayCommand<string>(RunHintAction);
        RunNoticeActionCommand = new RelayCommand<string>(RunNoticeAction);
        DismissNoticeCommand = new RelayCommand<string>(DismissNotice);
        AcknowledgePrivacyCommand = new RelayCommand(() => Host.SetPrivacyAcknowledged(true));
        OpenSetupGuideCommand = new RelayCommand<string>(step => _shell.OpenSetupWizard(step ?? Settings.Onboarding.LastStep));
    }

    private void AttachHints()
    {
        if (Host.Notices is { } center) center.Changed += RefreshNotices;
        Host.SettingsApplied += RefreshHints;
        RefreshNotices();
    }

    private void DetachHints()
    {
        if (Host.Notices is { } center) center.Changed -= RefreshNotices;
        Host.SettingsApplied -= RefreshHints;
    }

    private void RefreshNotices()
    {
        if (_disposed) return;
        var active = Host.Notices?.Active ?? [];
        if (!active.SequenceEqual(Notices))
        {
            Notices.Clear();
            foreach (var n in active) Notices.Add(n);
        }
        Notify(nameof(VisibleNotices));
        Notify(nameof(HasNotices));
        Notify(nameof(NoticeCount));
        Notify(nameof(HasMoreNotices));
    }

    /// <summary>Re-reads every hint (the 1 s status timer and after each apply).</summary>
    private void RefreshHints()
    {
        if (_disposed) return;
        foreach (var name in new[]
                 {
                     nameof(HintSlot), nameof(HasHintSlot), nameof(IsPrivacySlot), nameof(IsFinishSetupSlot), nameof(IsPageHintSlot), nameof(CaptureHint),
                     nameof(PrivacyCalloutText), nameof(PrivacyLines), nameof(DispatchesHint), nameof(PlayersHint), nameof(PhrasebookHint),
                     nameof(ReadingChatHint), nameof(TranslationHint), nameof(OverlayHint), nameof(ReplyHint), nameof(HotkeysHint),
                 })
            Notify(name);
    }

    private void RunHintAction(string? actionId)
    {
        switch (actionId)
        {
            case HintActions.SelectRegion: _shell.SelectRegion(); break;
            case HintActions.ToggleCapture: Host.ToggleCapture(); break;
            case HintActions.OpenTranslation: _shell.ShowPage(PageIds.Translation); break;
            case HintActions.OpenCapture: _shell.ShowPage(PageIds.Capture); break;
            case HintActions.OpenHotkeys: _shell.ShowPage(PageIds.Hotkeys); break;
            case HintActions.EditPhrases: EditPhrases(); break;
            case HintActions.OpenSetup: _shell.OpenSetupWizard(Settings.Onboarding.LastStep); break;
            case HintActions.ShowOverlay:
                Settings.Overlay.Visible = true;
                Host.ApplySettings(); // App shows the overlay on SettingsApplied
                break;
        }
        RefreshHints();
    }

    private void RunNoticeAction(string? actionId)
    {
        switch (actionId)
        {
            case NoticeActions.OpenCapture: _shell.ShowPage(PageIds.Capture); break;
            case NoticeActions.OpenTranslation: _shell.ShowPage(PageIds.Translation); break;
            case NoticeActions.UpdatePrimary: RunUpdateAction(Host.UpdateView.Primary); break;
        }
    }

    private void DismissNotice(string? key)
    {
        if (!string.IsNullOrEmpty(key) && Host.Notices is { } center)
        {
            center.Dismiss(key);
            Host.ScheduleSave();
        }
    }
}
