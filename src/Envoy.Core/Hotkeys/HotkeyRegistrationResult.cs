namespace Envoy.Core.Hotkeys;

/// <param name="ActionId">The <c>HotkeySettings</c> property name, e.g. "Compose".</param>
/// <param name="ActionName">Display name from <see cref="HotkeyText.ActionName"/>.</param>
/// <param name="Combo">The combination as configured, e.g. "Ctrl+Shift+T".</param>
public sealed record HotkeyRegistrationResult(string ActionId, string ActionName, string Combo, HotkeyStatus Status);
