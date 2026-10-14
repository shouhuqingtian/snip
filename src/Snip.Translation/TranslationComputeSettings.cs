using System.Text.Json;

namespace Snip.Translation;

public enum TranslationComputeMode { Auto, Cpu, Gpu }
public enum TranslationBackend { Cpu, Vulkan }

/// <summary>Local preferences only; reading never creates files or probes a GPU.</summary>
public sealed class TranslationComputeSettingsStore(string path)
{
    public TranslationComputeMode Read()
    {
        if (!File.Exists(path)) return TranslationComputeMode.Auto;
        try
        {
            if (new FileInfo(path).Length > 1024) throw new JsonException();
            var mode = JsonSerializer.Deserialize<TranslationComputeMode>(File.ReadAllText(path));
            if (!Enum.IsDefined(mode)) throw new JsonException();
            return mode;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("计算方式设置无法读取，本次使用自动模式；可重新选择并保存。", error); }
    }
    public void Save(TranslationComputeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(mode)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
