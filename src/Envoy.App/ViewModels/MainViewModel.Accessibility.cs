using Envoy.App.Services;
using Envoy.Core.Settings;
using Envoy.Core.Ux;

namespace Envoy.App.ViewModels;

/// <summary>Appearance › Accessibility and the Overlay page's readable-text note (spec 07b).</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<Option> HighContrastModes { get; } =
    [
        new(HighContrastMode.FollowWindows, "Follow Windows"),
        new(HighContrastMode.Always, "Always"),
        new(HighContrastMode.Never, "Never"),
    ];

    /// <summary>Whether a Windows contrast theme is on now (the Accessibility caption).</summary>
    public string HighContrastStatus => Host.SystemHighContrast ? AccessibilityText.HighContrastOn : AccessibilityText.HighContrastOff;

    /// <summary>
    /// "Raised to NN% so text stays readable." when the readable-text floor raised the overlay background above the
    /// slider's value (REQ-OUXB-37), else "".
    /// </summary>
    public string OverlayOpacityNote
    {
        get
        {
            var overlay = Settings.Overlay;
            if (!overlay.EnsureReadableText) return "";
            var effective = ThemeManager.OverlayCardAlpha(overlay);
            return effective > Math.Clamp(overlay.BackgroundOpacity, 0, 1) + 0.005 ? AccessibilityText.Raised(effective) : "";
        }
    }

    private void RefreshAccessibility()
    {
        Notify(nameof(HighContrastStatus));
        Notify(nameof(OverlayOpacityNote));
    }
}
