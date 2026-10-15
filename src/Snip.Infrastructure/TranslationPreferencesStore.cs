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
            string json = File.ReadAllText(path);
            var preferences = JsonSerializer.Deserialize<TranslationPreferences>(json) ?? throw new JsonException();
            preferences.Validate();
            using var document = JsonDocument.Parse(json);
            // Versions before automatic direction persisted an initial OCR guess as if it were a preference.
            // Upgrade only that old format; new explicit overrides still survive a restart.
            bool legacy = !document.RootElement.TryGetProperty("SchemaVersion", out var version);
            if (!legacy && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out _))) throw new JsonException();
            if (legacy || version.GetInt32() < 2)
                return preferences with { Direction = "auto" };
            return preferences;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("默认翻译设置无法读取，请重新选择并保存。", error); }
    }
    public void Save(TranslationPreferences preferences)
    {
        preferences.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new { SchemaVersion = 2, preferences.Mode, preferences.Direction })); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
