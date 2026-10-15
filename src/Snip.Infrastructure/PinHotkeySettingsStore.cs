using System;
using System.IO;
using System.Text.Json;
using Snip.Core;
namespace Snip.Infrastructure;

public sealed class PinHotkeySettingsStore(string? path = null) : IPinHotkeySettingsStore
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "pin-hotkeys.json");
    public PinHotkeySettings Read()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<PinHotkeySettings>(File.ReadAllText(path)) ?? throw new JsonException();
            settings.Validate(); return settings;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidOperationException("贴图快捷键配置无效，请在贴图管理中重新设置。", error); }
    }
    public void Save(PinHotkeySettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
