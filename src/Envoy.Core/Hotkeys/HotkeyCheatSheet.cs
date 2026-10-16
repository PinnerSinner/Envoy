using Envoy.Core.Text;

namespace Envoy.Core.Hotkeys;

/// <summary>One row of the hotkey cheat sheet (REQ-OUXA-9).</summary>
/// <param name="Action">The action's display name.</param>
/// <param name="Display">The combination, or its status text.</param>
public sealed record HotkeyRow(string Action, string Display, HotkeyStatus Status)
{
    public bool HasProblem => Status is HotkeyStatus.Taken or HotkeyStatus.Invalid or HotkeyStatus.NotSet;
}

/// <summary>The guide's Hotkeys step, built only from <c>AppHost.HotkeyResults</c> (contract §2.3).</summary>
public static class HotkeyCheatSheet
{
    /// <summary>
    /// One row per result, in its order. Display: Registered → the combination; NotSet → "Not set"; Taken → "{combo} ·
    /// taken by another app"; Invalid → "{combo} · not a valid combination"; SkippedSafeMode → "{combo} · off in safe mode".
    /// </summary>
    public static IReadOnlyList<HotkeyRow> Build(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return [.. results.Select(r => new HotkeyRow(r.ActionName, Display(r), r.Status))];
    }

    /// <summary>Hotkeys that are taken or not a valid combination (the guide's "needs attention").</summary>
    public static int Problems(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Count(r => r.Status is HotkeyStatus.Taken or HotkeyStatus.Invalid);
    }

    private static string Display(HotkeyRegistrationResult r) => r.Status switch
    {
        HotkeyStatus.Registered => r.Combo,
        HotkeyStatus.NotSet => HotkeyText.CheatNotSet,
        HotkeyStatus.Taken => TextTemplate.Format(HotkeyText.CheatTakenFormat, null, ("combo", r.Combo)),
        HotkeyStatus.Invalid => TextTemplate.Format(HotkeyText.CheatInvalidFormat, null, ("combo", r.Combo)),
        HotkeyStatus.SkippedSafeMode => TextTemplate.Format(HotkeyText.CheatSafeModeFormat, null, ("combo", r.Combo)),
        _ => throw new ArgumentOutOfRangeException(nameof(r), r.Status, null),
    };
}
