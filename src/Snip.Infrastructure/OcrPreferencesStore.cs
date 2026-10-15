using System;
using System.IO;
using System.Text.Json;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class OcrPreferencesStore(string? path = null) : IOcrPreferencesStore
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "ocr-preferences.json");
    private sealed record Settings(int Version, OcrMode Mode);
    public OcrMode? Read()
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 4096) throw new JsonException();
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            if (settings is null || settings.Version != 1 || !Enum.IsDefined(settings.Mode)) throw new JsonException();
            return settings.Mode;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("默认识别设置无法读取，请重新选择并保存。", error); }
    }
    public void Save(OcrMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("识别方式无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(1, mode))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
