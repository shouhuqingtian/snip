using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData(4u, 0x51u, "Shift+Q")]
    [InlineData(4u, 0x31u, "Shift+1")]
    [InlineData(0u, 0x51u, "Q")]
    [InlineData(0u, 0x20u, "Space")]
    [InlineData(0u, 0x78u, "F9")]
    [InlineData(2u, 0x61u, "Ctrl+Num1")]
    [InlineData(1u, 0x6Bu, "Alt+Num+")]
    [InlineData(4u, 0xBAu, "Shift+;")]
    [InlineData(2u, 0xDCu, "Ctrl+\\")]
    [InlineData(0u, 0xAFu, "VolumeUp")]
    public void KeyboardChoicesArePreservedAcrossConfigurationRoundTrip(uint modifiers, uint key, string display)
    {
        var gesture = new HotkeyGesture(modifiers, key); gesture.Validate();
        var restored = System.Text.Json.JsonSerializer.Deserialize<HotkeyGesture>(System.Text.Json.JsonSerializer.Serialize(gesture))!;
        restored.Validate(); Assert.Equal(gesture, restored); Assert.Equal(display, restored.DisplayText);
    }
    [Theory]
    [InlineData(16u, 0x41u)]
    [InlineData(3u, 0x7Bu)]
    [InlineData(3u, 0x11u)]
    [InlineData(0u, 0u)]
    [InlineData(0u, 0xFFu)]
    public void InvalidOrReservedKeysAreRejected(uint modifiers, uint key)
        => Assert.Throws<ArgumentException>(() => new HotkeyGesture(modifiers, key).Validate());
}
