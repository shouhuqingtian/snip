using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData(0, 0x41)] // unmodified typing
    [InlineData(4, 0x41)] // Shift typing
    [InlineData(16, 0x41)] // invalid modifier flag
    [InlineData(3, 0x7B)] // globally reserved F12
    [InlineData(3, 0x11)] // modifier without a key
    public void UnsafeOrReservedCombinationsAreRejected(uint modifiers, uint key)
        => Assert.Throws<ArgumentException>(() => new HotkeyGesture(modifiers, key).Validate());
}
