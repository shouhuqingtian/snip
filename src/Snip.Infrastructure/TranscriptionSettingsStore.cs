using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Device-local ASR profiles and shortcut, saved atomically; keys are user-bound DPAPI.</summary>
public sealed class TranscriptionSettingsStore(IOnlineTranslationSettingsStore translation, string? file = null) : ITranscriptionSettingsStore
{
    private readonly string path = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "voice-input.json");
    private sealed record Profile(TranscriptionSettings Settings, string ProtectedKey);
    private sealed record Stored(int Version, string ActiveProvider, VoiceInputPreferences Preferences, Profile[] Profiles);
    private Stored Load()
    {
        if (!File.Exists(path)) return new(1, "", VoiceInputPreferences.Default, []);
        try
        {
            if (new FileInfo(path).Length > 512 * 1024) throw new JsonException();
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(path)) ?? throw new JsonException();
            if (stored.Version != 1 || stored.Preferences?.Hotkey is null || stored.Profiles is null || stored.Profiles.Length > 32 || stored.ActiveProvider is null) throw new JsonException();
            stored.Preferences.Hotkey.Validate();
            foreach (var p in stored.Profiles) { if (p?.Settings is null || p.ProtectedKey is null) throw new JsonException(); TranscriptionProviders.Validate(p.Settings); }
            if (stored.Profiles.Select(p => p.Settings.Provider).Distinct().Count() != stored.Profiles.Length || stored.Preferences.Enabled && !stored.Profiles.Any(p => p.Settings.Provider == stored.ActiveProvider)) throw new JsonException();
            return stored;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw new VoiceInputException("语音输入设置无法读取，请重新保存语音输入设置。"); }
    }
    private static Profile? Find(Stored data, string? provider) => data.Profiles.FirstOrDefault(p => p.Settings.Provider == (provider ?? data.ActiveProvider));
    private static TranscriptionSettings Public(Profile p) => p.Settings with { HasApiKey = p.ProtectedKey.Length > 0 };
    public TranscriptionSettings? Read(string? provider = null) => Find(Load(), provider) is { } p ? Public(p) : null;
    public IReadOnlyList<TranscriptionSettings> ReadProfiles() => Load().Profiles.Select(Public).ToArray();
    public VoiceInputPreferences ReadPreferences() => Load().Preferences;
    public TranscriptionCredentials ReadCredentials()
    {
        var p = Find(Load(), null) ?? throw new VoiceInputException("请先配置在线语音输入服务。");
        if (p.Settings.TranslationProfile.Length > 0) return Shared(p.Settings);
        if (p.ProtectedKey.Length == 0) throw new VoiceInputException("请在语音输入设置中填写密钥。");
        byte[]? bytes = null;
        try { bytes = ProtectedData.Unprotect(Convert.FromBase64String(p.ProtectedKey), null, DataProtectionScope.CurrentUser); return new(Public(p), Encoding.UTF8.GetString(bytes)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { throw new VoiceInputException("语音输入密钥无法解密，请在当前 Windows 用户下重新填写。"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    private TranscriptionCredentials Shared(TranscriptionSettings settings)
    {
        var credentials = translation.ReadCredentials(settings.TranslationProfile);
        if (!SpeechProviders.SameService(SpeechProviders.Endpoint(settings.Endpoint), SpeechProviders.Endpoint(credentials.Settings.Endpoint)))
            throw new VoiceInputException("复用密钥需要语音识别与翻译接口属于同一服务域名和地域。");
        return new(settings, credentials.ApiKey);
    }
    public void Save(TranscriptionSettings settings, string? replacementKey, VoiceInputPreferences preferences, bool removeKey = false)
    {
        settings = settings with { Provider = settings.Provider.Trim(), Endpoint = settings.Endpoint.Trim(), Model = settings.Model.Trim(), HasApiKey = false };
        TranscriptionProviders.Validate(settings); preferences.Hotkey.Validate();
        var data = Load(); var old = Find(data, settings.Provider); string encrypted = "";
        if (old is null && data.Profiles.Length >= 32) throw new VoiceInputException("最多保存 32 组语音输入配置。");
        if (settings.TranslationProfile.Length > 0) _ = Shared(settings);
        else if (!removeKey)
        {
            if (!string.IsNullOrWhiteSpace(replacementKey))
            {
                string key = replacementKey.Trim(); if (key.Length > 8192 || key.Any(char.IsControl)) throw new VoiceInputException("密钥格式无效，请只填写 API Key。");
                byte[] bytes = Encoding.UTF8.GetBytes(key);
                try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else if (old?.Settings.Endpoint == settings.Endpoint && old.Settings.TranslationProfile.Length == 0) encrypted = old.ProtectedKey;
        }
        if (preferences.Enabled && encrypted.Length == 0 && settings.TranslationProfile.Length == 0) throw new VoiceInputException("启用语音输入前请填写密钥，或选择同平台的翻译密钥。");
        var profiles = data.Profiles.Where(p => p.Settings.Provider != settings.Provider).Append(new Profile(settings, encrypted)).ToArray();
        Write(new(1, settings.Provider, preferences, profiles));
    }
    public void SetEnabled(bool enabled)
    {
        var data = Load(); if (enabled) _ = ReadCredentials(); Write(data with { Preferences = data.Preferences with { Enabled = enabled } });
    }
    private void Write(Stored data) => SettingsConfigurationTransfer.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(data));
}
