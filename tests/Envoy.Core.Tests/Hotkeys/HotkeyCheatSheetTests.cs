using Envoy.Core.Hotkeys;
using Xunit;

namespace Envoy.Core.Tests.Hotkeys;

public class HotkeyCheatSheetTests
{
    private static HotkeyRegistrationResult R(string id, string combo, HotkeyStatus status) => new(id, HotkeyText.ActionName(id), combo, status);

    [Fact]
    public void One_row_per_result_in_order()
    {
        var rows = HotkeyCheatSheet.Build([R("Compose", "Ctrl+Shift+T", HotkeyStatus.Registered), R("ToggleOverlay", "", HotkeyStatus.NotSet),
            R("SelectRegion", "Ctrl+Shift+R", HotkeyStatus.Taken)]);
        Assert.Equal(["Reply", "Show/hide overlay", "Select chat area"], rows.Select(r => r.Action));
        Assert.Equal([HotkeyStatus.Registered, HotkeyStatus.NotSet, HotkeyStatus.Taken], rows.Select(r => r.Status));
        Assert.Empty(HotkeyCheatSheet.Build([]));
    }

    [Theory]
    [InlineData(HotkeyStatus.Registered, "Ctrl+Shift+T", false)]
    [InlineData(HotkeyStatus.NotSet, "Not set", true)]
    [InlineData(HotkeyStatus.Taken, "Ctrl+Shift+T · taken by another app", true)]
    [InlineData(HotkeyStatus.Invalid, "Ctrl+Shift+T · not a valid combination", true)]
    [InlineData(HotkeyStatus.SkippedSafeMode, "Ctrl+Shift+T · off in safe mode", false)]
    public void Display_text_per_status(HotkeyStatus status, string display, bool problem)
    {
        var row = HotkeyCheatSheet.Build([R("Compose", "Ctrl+Shift+T", status)]).Single();
        Assert.Equal(display, row.Display);
        Assert.Equal(problem, row.HasProblem);
    }

    [Fact]
    public void Problems_count_taken_and_invalid()
    {
        Assert.Equal(2, HotkeyCheatSheet.Problems([R("Compose", "x", HotkeyStatus.Taken), R("ToggleOverlay", "y", HotkeyStatus.Invalid),
            R("ToggleCapture", "", HotkeyStatus.NotSet), R("SelectRegion", "z", HotkeyStatus.Registered)]));
    }
}
