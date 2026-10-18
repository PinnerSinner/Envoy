using Envoy.Core.Mvvm;
using Envoy.Core.Onboarding;

namespace Envoy.App.ViewModels;

/// <summary>One entry of the setup guide's step list (REQ-OUXA-1, 11). UI thread.</summary>
public sealed class StepItem(OnboardingStep step) : ObservableObject
{
    private StepStatus _status;
    private bool _isCurrent;

    public OnboardingStep Step { get; } = step;

    public string Title { get; } = OnboardingText.TitleOf(step);

    /// <summary>The step's number on its seal in the chain (spec 09a), 1-based.</summary>
    public string Number { get; } = ((int)step + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public StepStatus Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(AccessibleName));
            OnPropertyChanged(nameof(NeedsAttention));
            OnPropertyChanged(nameof(IsDone));
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public string StatusText => OnboardingText.StatusOf(Status);

    public bool NeedsAttention => Status == StepStatus.NeedsAttention;

    public bool IsDone => Status == StepStatus.Done;

    /// <summary>"{Title}, {status}" for screen readers.</summary>
    public string AccessibleName => OnboardingText.StepItemName(Step, Status);
}
