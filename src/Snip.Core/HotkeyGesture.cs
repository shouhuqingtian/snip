namespace Snip.Core;

public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public static HotkeyGesture Default { get; } = new(3, 0x41);
    public void Validate()
    {
        bool function = VirtualKey is >= 0x70 and <= 0x87;
        bool supported = VirtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A
            or 0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E;
        if (Modifiers > 15 || (!supported && !function) || VirtualKey == 0x7B)
            throw new ArgumentException("请选择字母、数字、方向键、导航键或功能键；F12 是 Windows 保留键。");
        if (Modifiers == 0 && !function && VirtualKey != 0x2C)
            throw new ArgumentException("普通按键需要搭配 Ctrl、Alt、Shift 或 Win，避免影响输入。");
        if (Modifiers == 4 && VirtualKey is >= 0x30 and <= 0x5A)
            throw new ArgumentException("字母和数字需要搭配 Ctrl、Alt 或 Win，不能只使用 Shift。");
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
                >= 0x70 and <= 0x87 => "F" + (VirtualKey - 0x6F),
                0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
                0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2C => "PrintScreen",
                0x2D => "Insert", 0x2E => "Delete", _ => "未设置"
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
