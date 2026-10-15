using Snip.Core;
using Xunit;
namespace Snip.Core.Tests;
public sealed class PinHotkeySettingsTests
{
    [Fact]public void PinShortcutsAreOptionalAndDoNotReserveDefaults()
    {var settings=new PinHotkeySettings();settings.Validate();Assert.All(Enum.GetValues<PinHotkeyAction>(),action=>Assert.Null(settings.For(action)));}
    [Fact]public void DuplicateBindingsAreRejectedBeforeRegistration()
    {var key=new HotkeyGesture(3,0x75);Assert.Throws<ArgumentException>(()=>new PinHotkeySettings(key,key).Validate());}
    [Theory][InlineData(1u,0u)][InlineData(0u,0x41u)][InlineData(3u,0x7Bu)]
    public void InvalidGesturesUseTheSameRulesAsCapture(uint modifiers,uint key)
    {Assert.Throws<ArgumentException>(()=>new PinHotkeySettings(new(modifiers,key)).Validate());}
    [Fact]public void UnknownSchemaCannotSilentlyReplaceSettings()
    {Assert.Throws<ArgumentException>(()=>new PinHotkeySettings(Version:2).Validate());}
}
