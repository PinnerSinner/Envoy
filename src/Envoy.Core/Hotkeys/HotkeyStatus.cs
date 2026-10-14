namespace Envoy.Core.Hotkeys;

/// <summary>Outcome of registering one hotkey (contract §2.3).</summary>
public enum HotkeyStatus
{
    /// <summary>The hotkey is active.</summary>
    Registered,

    /// <summary>No combination is set for this action.</summary>
    NotSet,

    /// <summary>Another app already owns the combination.</summary>
    Taken,

    /// <summary>The combination could not be parsed.</summary>
    Invalid,
}
