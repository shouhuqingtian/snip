using System;
using System.IO;
using System.Text.Json;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class HotkeySettingsStore(string? path = null) : IHotkeySettingsStore
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "hotkey.json");
    public HotkeyGesture Read()
    {
        if (!File.Exists(path)) return HotkeyGesture.Default;
        try
        {
            var gesture = JsonSerializer.Deserialize<HotkeyGesture>(File.ReadAllText(path)) ?? throw new JsonException();
            gesture.Validate(); return gesture;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidOperationException("快捷键配置无效，本次使用默认 Ctrl+Alt+A；可以重新保存设置。", error); }
    }
    public void Save(HotkeyGesture gesture)
    {
        gesture.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(gesture));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
