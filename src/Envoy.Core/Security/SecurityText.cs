namespace Envoy.Core.Security;

/// <summary>User-facing sentences for the security guards (contract §8.7: what happened, then what to do).</summary>
public static class SecurityText
{
    public const string SendNoTarget =
        "Nothing was typed because Envoy didn't know which window to type into. Open the reply box over the game with your hotkey.";

    public const string SendTargetClosed = "Nothing was typed because the window you were replying to has closed.";

    public const string SendFocusLost =
        "Envoy stopped typing because another window came to the front. Click back into the game and send again.";

    /// <summary>The sentence shown when a reply was not typed; "" for <see cref="SendTargetVerdict.Ok"/>.</summary>
    public static string SendBlocked(SendTargetVerdict verdict) => verdict switch
    {
        SendTargetVerdict.NoTarget => SendNoTarget,
        SendTargetVerdict.TargetClosed => SendTargetClosed,
        SendTargetVerdict.FocusLost => SendFocusLost,
        _ => "",
    };
}
