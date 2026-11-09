namespace Snip.Core;

public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public static HotkeyGesture Default { get; } = new(3, 0x41);
    public void Validate()
    {
        bool supported = VirtualKey is 0x08 or 0x09 or 0x0C or 0x0D or 0x13 or 0x14
            or >= 0x20 and <= 0x28 or 0x2C or 0x2D or 0x2E
            or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A
            or >= 0x60 and <= 0x87 or 0x90 or 0x91
            or >= 0xA6 and <= 0xB7 or >= 0xBA and <= 0xC0
            or >= 0xDB and <= 0xDF or 0xE2;
        if (Modifiers > 15) throw new ArgumentException("快捷键包含无效的修饰键。");
        if (VirtualKey == 0x7B) throw new ArgumentException("F12 是 Windows 保留键，不能设置为全局快捷键。");
        if (!supported) throw new ArgumentException("请选择普通键盘按键；Ctrl、Alt、Shift、Win 需搭配其他按键。");
    }
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & 2) != 0) parts.Add("Ctrl");
            if ((Modifiers & 1) != 0) parts.Add("Alt");
            if ((Modifiers & 4) != 0) parts.Add("Shift");
            if ((Modifiers & 8) != 0) parts.Add("Win");
            parts.Add(VirtualKey switch
            {
                >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)VirtualKey).ToString(),
                >= 0x60 and <= 0x69 => "Num" + (VirtualKey - 0x60),
                >= 0x70 and <= 0x87 => "F" + (VirtualKey - 0x6F),
                0x08 => "Backspace", 0x09 => "Tab", 0x0C => "Clear", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "CapsLock",
                0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
                0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2C => "PrintScreen",
                0x2D => "Insert", 0x2E => "Delete",
                0x6A => "Num*", 0x6B => "Num+", 0x6C => "NumSeparator", 0x6D => "Num-", 0x6E => "NumDecimal", 0x6F => "Num/",
                0x90 => "NumLock", 0x91 => "ScrollLock",
                0xA6 => "BrowserBack", 0xA7 => "BrowserForward", 0xA8 => "BrowserRefresh", 0xA9 => "BrowserStop", 0xAA => "BrowserSearch", 0xAB => "BrowserFavorites", 0xAC => "BrowserHome",
                0xAD => "VolumeMute", 0xAE => "VolumeDown", 0xAF => "VolumeUp", 0xB0 => "MediaNext", 0xB1 => "MediaPrevious", 0xB2 => "MediaStop", 0xB3 => "MediaPlayPause", 0xB4 => "LaunchMail", 0xB5 => "SelectMedia", 0xB6 => "LaunchApp1", 0xB7 => "LaunchApp2",
                0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
                0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'", 0xDF => "Oem8", 0xE2 => "Oem102", _ => "未设置"
            });
            return string.Join("+", parts);
        }
    }
}

public interface IHotkeySettingsStore
{
    HotkeyGesture Read();
    void Save(HotkeyGesture gesture);
}
