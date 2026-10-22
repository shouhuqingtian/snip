using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Only explicitly listed service settings are transferable. Temporary/journal files contain Windows-protected keys, never plaintext API keys.</summary>
public sealed class SettingsConfigurationTransfer(string directory)
{
    private static readonly string[] Names = ["online-translation.json", "online-speech.json", "translation-preferences.json", "ocr-preferences.json", "text-reading-preferences.json", "hotkey.json", "pin-hotkeys.json"];
    private string JournalPath => Path.Combine(directory, "configuration-transfer-transaction.json");
    public sealed record Snapshot(string Fingerprint, IReadOnlyDictionary<string, byte[]?> Files);
    public sealed record Prepared(IReadOnlyDictionary<string, byte[]?> Files, int TranslationProfiles, int SpeechProfiles);
    private sealed class TranslationProfile
    {
        public OnlineTranslationSettings Settings { get; set; } = null!;
        public string ApiKey { get; set; } = "";
        public override string ToString() => "Translation profile (redacted)";
    }
    private sealed class SpeechProfile
    {
        public OnlineSpeechSettings Settings { get; set; } = null!;
        public string ApiKey { get; set; } = "";
        public override string ToString() => "Speech profile (redacted)";
    }
    private sealed class Bundle
    {
        public int Version { get; set; } = 1;
        public List<TranslationProfile> Translation { get; set; } = [];
        public string? ActiveTranslation { get; set; }
        public List<SpeechProfile> Speech { get; set; } = [];
        public string? ActiveSpeech { get; set; }
        public SpeechMode SpeechMode { get; set; }
        public TranslationPreferences? TranslationPreferences { get; set; }
        public OcrMode? OcrMode { get; set; }
        public bool? SourceExpanded { get; set; }
        public HotkeyGesture? CaptureHotkey { get; set; }
        public PinHotkeySettings? PinHotkeys { get; set; }
        public override string ToString() => "Settings bundle (redacted)";
    }
    public Snapshot Capture()
    {
        var first = ReadFiles(directory); var second = ReadFiles(directory);
        if (Fingerprint(first) != Fingerprint(second)) throw new InvalidOperationException("配置正在保存，请稍后重试。");
        return new(Fingerprint(first), first);
    }
    public byte[] Encrypt(Snapshot snapshot, string password)
    {
        return Shadow(snapshot.Files, folder =>
        {
            var translation = new OnlineTranslationSettingsStore(Path.Combine(folder, Names[0]));
            var speech = new OnlineSpeechSettingsStore(translation, Path.Combine(folder, Names[1]));
            var bundle = new Bundle
            {
                ActiveTranslation = translation.Read()?.Provider, ActiveSpeech = speech.Read()?.Provider, SpeechMode = speech.ReadMode(),
                TranslationPreferences = new TranslationPreferencesStore(Path.Combine(folder, Names[2])).Read(),
                OcrMode = new OcrPreferencesStore(Path.Combine(folder, Names[3])).Read(),
                SourceExpanded = new TextReadingPreferencesStore(Path.Combine(folder, Names[4])).ReadSourceExpanded(),
                CaptureHotkey = new HotkeySettingsStore(Path.Combine(folder, Names[5])).Read(),
                PinHotkeys = new PinHotkeySettingsStore(Path.Combine(folder, Names[6])).Read()
            };
            foreach (var profile in translation.ReadProfiles())
                bundle.Translation.Add(new() { Settings = profile, ApiKey = profile.HasApiKey ? translation.ReadCredentials(profile.Provider).ApiKey : "" });
            foreach (var profile in speech.ReadProfiles())
                bundle.Speech.Add(new() { Settings = profile, ApiKey = profile.HasApiKey && profile.TranslationProfile.Length == 0 ? speech.ReadCredentials(profile.Provider).ApiKey : "" });
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(bundle);
            try { return EncryptedConfigurationArchive.Encrypt(plaintext, password); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        });
    }
    public void ExportFile(Snapshot snapshot, string password, string path, CancellationToken token = default)
    {
        byte[] archive = Encrypt(snapshot, password);
        token.ThrowIfCancellationRequested();
        WriteAtomic(path, archive, token);
    }
    public Prepared ReadArchive(string path, string password)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > EncryptedConfigurationArchive.MaximumArchiveBytes) throw new InvalidDataException("配置文件超过大小上限。");
        var archive = new byte[checked((int)file.Length)]; file.ReadExactly(archive);
        return Decrypt(archive, password);
    }
    public Prepared Decrypt(byte[] archive, string password)
    {
        byte[] plaintext = EncryptedConfigurationArchive.Decrypt(archive, password);
        try
        {
            var bundle = JsonSerializer.Deserialize<Bundle>(plaintext, new JsonSerializerOptions { MaxDepth = 12 }) ?? throw new InvalidDataException();
            if (bundle.Version != 1 || bundle.Translation is null || bundle.Speech is null || bundle.Translation.Count > 32 || bundle.Speech.Count > 32 || !Enum.IsDefined(bundle.SpeechMode) ||
                bundle.Translation.Any(p => p is null || p.Settings is null || p.Settings.Provider is null || p.Settings.Endpoint is null || p.Settings.Model is null || p.Settings.VisionModel is null || p.ApiKey is null) || bundle.Speech.Any(p => p is null || p.Settings is null || p.ApiKey is null) ||
                bundle.Translation.Select(p => p.Settings.Provider).Distinct().Count() != bundle.Translation.Count || bundle.Speech.Select(p => p.Settings.Provider).Distinct().Count() != bundle.Speech.Count ||
                bundle.Translation.Count > 0 && !bundle.Translation.Any(p => p.Settings.Provider == bundle.ActiveTranslation) ||
                bundle.Speech.Count > 0 && !bundle.Speech.Any(p => p.Settings.Provider == bundle.ActiveSpeech) ||
                bundle.SpeechMode == SpeechMode.Online && bundle.Speech.Count == 0 || bundle.OcrMode is { } mode && !Enum.IsDefined(mode))
                throw new InvalidDataException();
            bundle.TranslationPreferences?.Validate();
            bundle.CaptureHotkey?.Validate(); bundle.PinHotkeys?.Validate();
            if (bundle.CaptureHotkey is { } hotkey && bundle.PinHotkeys is { } pins && new[] { pins.Restore, pins.Previous, pins.Next }.Contains(hotkey))
                throw new InvalidDataException();
            foreach (var profile in bundle.Speech) SpeechProviders.Validate(profile.Settings);
            return Shadow(new Dictionary<string, byte[]?>(), folder =>
            {
                var translation = new OnlineTranslationSettingsStore(Path.Combine(folder, Names[0]));
                foreach (var profile in bundle.Translation.OrderBy(p => p.Settings.Provider == bundle.ActiveTranslation))
                    translation.Save(profile.Settings with { DefaultOnline = profile.Settings.Provider == bundle.ActiveTranslation && profile.Settings.DefaultOnline }, profile.ApiKey, removeKey: profile.ApiKey.Length == 0);
                var speech = new OnlineSpeechSettingsStore(translation, Path.Combine(folder, Names[1]));
                foreach (var profile in bundle.Speech.OrderBy(p => p.Settings.Provider == bundle.ActiveSpeech))
                    speech.Save(profile.Settings, profile.ApiKey, profile.Settings.Provider == bundle.ActiveSpeech ? bundle.SpeechMode : SpeechMode.System, removeKey: profile.ApiKey.Length == 0);
                if (bundle.TranslationPreferences is { } prefs) new TranslationPreferencesStore(Path.Combine(folder, Names[2])).Save(prefs);
                if (bundle.OcrMode is { } ocr) new OcrPreferencesStore(Path.Combine(folder, Names[3])).Save(ocr);
                if (bundle.SourceExpanded is { } expanded) new TextReadingPreferencesStore(Path.Combine(folder, Names[4])).SaveSourceExpanded(expanded);
                if (bundle.CaptureHotkey is { } capture) new HotkeySettingsStore(Path.Combine(folder, Names[5])).Save(capture);
                if (bundle.PinHotkeys is { } pinKeys) new PinHotkeySettingsStore(Path.Combine(folder, Names[6])).Save(pinKeys);
                return new Prepared(ReadFiles(folder), bundle.Translation.Count, bundle.Speech.Count);
            });
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
        { throw new InvalidOperationException("配置内容无效或版本不受支持，本机配置未修改。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    /// <summary>Invoke on the UI dispatcher so settings saves cannot interleave with the settings commit.</summary>
    public Snapshot Apply(Prepared prepared, string expectedFingerprint, Func<bool>? stillCurrent = null)
    {
        if (stillCurrent?.Invoke() == false) throw new OperationCanceledException();
        var before = Capture();
        if (before.Fingerprint != expectedFingerprint) throw new InvalidOperationException("本机配置有新修改，请重新预览导入文件。");
        if (prepared.Files.Count != Names.Length || prepared.Files.Keys.Any(n => !Names.Contains(n))) throw new InvalidDataException();
        Directory.CreateDirectory(directory);
        // Durable rollback before any replacement. Only encrypted local files are journaled.
        WriteAtomic(JournalPath, JsonSerializer.SerializeToUtf8Bytes(before.Files));
        try
        {
            foreach (string name in Names) Replace(name, prepared.Files[name]);
            var result = Capture(); File.Delete(JournalPath); return result;
        }
        catch
        {
            foreach (string name in Names) Replace(name, before.Files[name]);
            File.Delete(JournalPath); throw;
        }
    }
    public void RecoverInterruptedImport()
    {
        if (!File.Exists(JournalPath)) return;
        if (new FileInfo(JournalPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("配置恢复记录无效。");
        var previous = JsonSerializer.Deserialize<Dictionary<string, byte[]?>>(File.ReadAllBytes(JournalPath)) ?? throw new InvalidDataException();
        if (previous.Count != Names.Length || previous.Keys.Any(n => !Names.Contains(n))) throw new InvalidDataException();
        foreach (string name in Names) Replace(name, previous[name]);
        File.Delete(JournalPath);
    }
    private void Replace(string name, byte[]? contents)
    { if (contents is null) File.Delete(Path.Combine(directory, name)); else WriteAtomic(Path.Combine(directory, name), contents); }
    private static Dictionary<string, byte[]?> ReadFiles(string folder)
    {
        var files = new Dictionary<string, byte[]?>();
        foreach (string name in Names)
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) { files[name] = null; continue; }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 512 * 1024) throw new InvalidDataException("配置超过大小上限。");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); files[name] = bytes;
        }
        return files;
    }
    private T Shadow<T>(IReadOnlyDictionary<string, byte[]?> files, Func<string, T> action)
    {
        string work = Path.Combine(directory, "ConfigurationTransferWork", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            foreach (var file in files) if (file.Value is { } bytes) File.WriteAllBytes(Path.Combine(work, file.Key), bytes);
            return action(work);
        }
        finally { Directory.Delete(work, true); }
    }
    private static string Fingerprint(IReadOnlyDictionary<string, byte[]?> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string name in Names)
        {
            byte[]? bytes = files[name]; hash.AppendData(BitConverter.GetBytes(bytes?.Length ?? -1));
            if (bytes is not null) hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static void WriteAtomic(string path, byte[] bytes, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
