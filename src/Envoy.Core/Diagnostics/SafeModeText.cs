namespace Envoy.Core.Diagnostics;

/// <summary>Safe-mode text (contract §8.7).</summary>
public static class SafeModeText
{
    public const string Banner = "Envoy is in safe mode. The overlay, hotkeys and automatic chat reading are off until you restart normally.";
    public const string ReasonCommandLine = "You started Envoy with --safe-mode.";
    public const string ReasonShiftHeld = "You held Shift while Envoy started.";
    public const string ReasonRepeatedFailures = "Envoy didn't finish starting the last two times, so it started in safe mode.";
    public const string Tray = "Envoy (safe mode)";
    public const string HotkeysOff = "Hotkeys are off in safe mode.";

    /// <summary>Why safe mode is on; "" when it is off.</summary>
    public static string Reason(SafeModeReason? reason) => reason switch
    {
        SafeModeReason.CommandLine => ReasonCommandLine,
        SafeModeReason.ShiftHeld => ReasonShiftHeld,
        SafeModeReason.RepeatedStartupFailures => ReasonRepeatedFailures,
        _ => "",
    };
}
