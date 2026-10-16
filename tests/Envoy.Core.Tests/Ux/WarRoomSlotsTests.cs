using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class WarRoomSlotsTests
{
    private static readonly PageHint Hint = new(HintSeverity.Info, "Chat reading is off.");

    [Theory]
    [InlineData(false, false, false, HintSlotKind.Privacy)]
    [InlineData(false, false, true, HintSlotKind.Privacy)]
    [InlineData(false, true, false, HintSlotKind.Privacy)]
    [InlineData(false, true, true, HintSlotKind.Privacy)]
    [InlineData(true, false, false, HintSlotKind.FinishSetup)]
    [InlineData(true, false, true, HintSlotKind.FinishSetup)]
    [InlineData(true, true, true, HintSlotKind.PageHint)]
    [InlineData(true, true, false, HintSlotKind.None)]
    public void Priority(bool privacy, bool completed, bool hasHint, HintSlotKind expected) =>
        Assert.Equal(expected, WarRoomSlots.Choose(privacy, completed, hasHint ? Hint : null));
}
