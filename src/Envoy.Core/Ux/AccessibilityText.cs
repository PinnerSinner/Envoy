using System.Globalization;
using Envoy.Core.Text;

namespace Envoy.Core.Ux;

/// <summary>Accessibility sentences built in code (contract §8.7, spec 07b).</summary>
public static class AccessibilityText
{
    /// <summary>{percent} = the overlay background opacity actually used, 0–100.</summary>
    public const string OpacityRaised = "Raised to {percent}% so text stays readable.";

    public const string HotkeyBoxHelp = "Press the key combination you want. Backspace clears it.";

    public const string HighContrastOn = "Windows high contrast is on.";
    public const string HighContrastOff = "Windows high contrast is off.";

    /// <summary>The Overlay page note under the background slider when the readable floor raised it (REQ-OUXB-37).</summary>
    public static string Raised(double alpha) =>
        TextTemplate.Format(OpacityRaised, CultureInfo.CurrentCulture,
            ("percent", (int)Math.Round(Math.Clamp(alpha, 0, 1) * 100, MidpointRounding.AwayFromZero)));
}
