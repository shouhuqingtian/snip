using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class OnlineSpeechSettingsStore(IOnlineTranslationSettingsStore translation, string? path = null) : ISpeechSettingsStore
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "online-speech.json");
    private sealed record Profile(OnlineSpeechSettings Settings, string ProtectedKey);
    private sealed record Stored(int Version, SpeechMode Mode, string ActiveProvider, Profile[] Profiles);
    private Stored Load()
    {
        if (!File.Exists(path)) return new(1, SpeechMode.System, "", []);
        try
        {
            if (new FileInfo(path).Length > 512 * 1024) throw new JsonException();
            var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new JsonException();
            if (data.Version != 1 || !Enum.IsDefined(data.Mode) || data.Profiles is null || data.Profiles.Length > 32 || data.ActiveProvider is null) throw new JsonException();
            foreach (var profile in data.Profiles)
            { if (profile?.Settings is null || profile.ProtectedKey is null) throw new JsonException(); SpeechProviders.Validate(profile.Settings); }
            if (data.Profiles.Select(p => p.Settings.Provider).Distinct().Count() != data.Profiles.Length) throw new JsonException();
            if (data.Mode == SpeechMode.Online && !data.Profiles.Any(p => p.Settings.Provider == data.ActiveProvider)) throw new JsonException();
            return data;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw new InvalidOperationException("语音设置无法读取，请打开 AI 语音设置重新保存。"); }
    }
    private static Profile? Find(Stored data, string? provider) => data.Profiles.FirstOrDefault(p => p.Settings.Provider == (provider ?? data.ActiveProvider));
    private static OnlineSpeechSettings Public(Profile profile) => profile.Settings with { HasApiKey = profile.ProtectedKey.Length > 0 };
    public SpeechMode ReadMode() => Load().Mode;
    public OnlineSpeechSettings? Read(string? provider = null) => Find(Load(), provider) is { } profile ? Public(profile) : null;
    public IReadOnlyList<OnlineSpeechSettings> ReadProfiles() => Load().Profiles.Select(Public).ToArray();
    public void SaveMode(SpeechMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("语音方式无效。");
        var data = Load();
        if (mode == SpeechMode.Online && Find(data, null) is null) throw new InvalidOperationException("请先打开 AI 语音设置，保存一组语音配置。");
        Write(data with { Mode = mode });
    }
    public OnlineSpeechCredentials ReadCredentials(string? provider = null)
    {
        var profile = Find(Load(), provider) ?? throw new InvalidOperationException("请先配置在线 AI 语音服务。");
        if (profile.Settings.TranslationProfile.Length > 0) return SharedCredentials(profile.Settings);
        if (profile.ProtectedKey.Length == 0) throw new InvalidOperationException("请在 AI 语音设置中填写密钥。");
        byte[]? bytes = null;
        try
        {
            bytes = ProtectedData.Unprotect(Convert.FromBase64String(profile.ProtectedKey), null, DataProtectionScope.CurrentUser);
            return new(Public(profile), Encoding.UTF8.GetString(bytes));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        { throw new InvalidOperationException("语音密钥无法解密，请在当前 Windows 用户下重新填写。"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    private OnlineSpeechCredentials SharedCredentials(OnlineSpeechSettings settings)
    {
        var credentials = translation.ReadCredentials(settings.TranslationProfile);
        if (!SpeechProviders.SameService(SpeechProviders.Endpoint(settings.Endpoint), SpeechProviders.Endpoint(credentials.Settings.Endpoint)))
            throw new InvalidOperationException("复用密钥需要语音和翻译 API 地址属于同一服务域名及地域；不同服务请填写独立密钥。");
        return new(settings, credentials.ApiKey);
    }
    public void Save(OnlineSpeechSettings settings, string? replacementApiKey, SpeechMode mode, bool removeKey = false)
    {
        settings = settings with { Provider = settings.Provider.Trim(), Endpoint = settings.Endpoint.Trim(), Model = settings.Model.Trim(), Voice = settings.Voice.Trim(), HasApiKey = false };
        SpeechProviders.Validate(settings);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("语音方式无效。");
        var data = Load(); var old = Find(data, settings.Provider); string encrypted = "";
        if (old is null && data.Profiles.Length >= 32) throw new InvalidOperationException("最多保存 32 组语音配置，请先删除不再使用的配置。");
        if (settings.TranslationProfile.Length > 0) _ = SharedCredentials(settings);
        else if (!removeKey)
        {
            if (!string.IsNullOrWhiteSpace(replacementApiKey))
            {
                string key = replacementApiKey.Trim();
                if (key.Length > 8192 || key.Any(char.IsControl)) throw new ArgumentException("语音密钥格式无效，请只填写 API Key。");
                var bytes = Encoding.UTF8.GetBytes(key);
                try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else if (old?.Settings.Endpoint == settings.Endpoint && old.Settings.TranslationProfile.Length == 0) encrypted = old.ProtectedKey;
        }
        if (mode == SpeechMode.Online && encrypted.Length == 0 && settings.TranslationProfile.Length == 0)
            throw new InvalidOperationException("默认在线 AI 语音需要先填写密钥或选择同服务的翻译密钥。");
        var profiles = data.Profiles.Where(p => p.Settings.Provider != settings.Provider).Append(new Profile(settings, encrypted)).ToArray();
        Write(new(1, mode, settings.Provider, profiles));
    }
    public void DeleteProfile(string provider)
    {
        var data = Load(); var remaining = data.Profiles.Where(p => p.Settings.Provider != provider).ToArray();
        if (remaining.Length == data.Profiles.Length) return;
        string active = data.ActiveProvider == provider ? remaining.FirstOrDefault()?.Settings.Provider ?? "" : data.ActiveProvider;
        Write(data with { Profiles = remaining, ActiveProvider = active, Mode = data.ActiveProvider == provider ? SpeechMode.System : data.Mode });
    }
    private void Write(Stored settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
