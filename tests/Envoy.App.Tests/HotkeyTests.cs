using System.Globalization;
using System.Windows.Input;
using Envoy.App.Services;
using Xunit;

namespace Envoy.App.Tests;

/// <summary>Hotkey parsing and formatting (REQ-QSC-43, AC-QSC-24).</summary>
public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+T")]
    [InlineData("Alt+1")]
    [InlineData("Win+F9")]
    [InlineData("Ctrl+Alt+Space")]
    public void Round_trips(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(text, hotkey.ToString());
    }

    [Fact]
    public void Digits_map_to_D_keys()
    {
        Assert.True(Hotkey.TryParse("Ctrl+1", out var hotkey));
        Assert.Equal(Key.D1, hotkey.Key);
        Assert.Equal(ModifierKeys.Control, hotkey.Modifiers);
        Assert.Equal("Ctrl+1", hotkey.ToString());
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void ToString_is_culture_invariant(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.True(Hotkey.TryParse("Ctrl+7", out var hotkey));
            Assert.Equal("Ctrl+7", hotkey.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Ctrl+Banana")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+12")]
    [InlineData("Ctrl+-5")]
    public void Rejects_unknown_tokens(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Theory]
    [InlineData("Ctrl+Shift")]
    [InlineData("Alt")]
    public void Rejects_modifier_only(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }
}
