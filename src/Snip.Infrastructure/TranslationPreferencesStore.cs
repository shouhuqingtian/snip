using System;
using System.IO;
using System.Text.Json;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class TranslationPreferencesStore(string? path = null) : ITranslationPreferencesStore
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "translation-preferences.json");
    public TranslationPreferences? Read()
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 4096) throw new JsonException();
            var preferences = JsonSerializer.Deserialize<TranslationPreferences>(File.ReadAllText(path)) ?? throw new JsonException();
            preferences.Validate(); return preferences;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("默认翻译设置无法读取，请重新选择并保存。", error); }
    }
    public void Save(TranslationPreferences preferences)
    {
        preferences.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(preferences)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
