using System;
using System.IO;
using System.Text.Json;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class TextReadingPreferencesStore(string? path = null) : ITextReadingPreferencesStore
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "text-reading-preferences.json");
    public bool? ReadSourceExpanded()
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 4096) throw new JsonException();
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("SourceExpanded", out var expanded) || expanded.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            return expanded.GetBoolean();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { throw new InvalidOperationException("原文展开设置无法读取，请重新展开或收起原文。", error); }
    }
    public void SaveSourceExpanded(bool expanded)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { SourceExpanded = expanded }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
