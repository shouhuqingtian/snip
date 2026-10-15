using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Windows DPAPI binds credentials to the signed-in user. Diagnostics never include this file.</summary>
public sealed class OnlineTranslationSettingsStore : IOnlineTranslationSettingsStore
{
    private readonly string path;
    public OnlineTranslationSettingsStore(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "online-translation.json"));
    }
    private sealed record Profile(string Provider, string Endpoint, string Model, string ProtectedKey);
    private sealed record StoredSettings(int Version, string ActiveProvider, bool DefaultOnline, Profile[] Profiles);
    private static Profile? Find(StoredSettings? stored, string? provider) => stored is null ? null : Array.Find(stored.Profiles, value => value.Provider == (provider ?? stored.ActiveProvider));
    private StoredSettings? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException();
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            if (stored.Version != 1 || stored.Profiles is null || stored.Profiles.Length > 32 ||
                Array.Exists(stored.Profiles, profile => profile is null || profile.Provider is null || profile.Endpoint is null || profile.Model is null || profile.ProtectedKey is null)) throw new InvalidDataException();
            return stored;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("在线翻译设置无法读取，请打开设置重新配置。");
        }
    }
    public OnlineTranslationSettings? Read(string? provider = null)
    {
        var stored = Load(); var profile = Find(stored, provider);
        return profile is null ? null : new(profile.Provider, profile.Endpoint, profile.Model, stored!.DefaultOnline, profile.ProtectedKey.Length > 0);
    }
    public IReadOnlyList<OnlineTranslationSettings> ReadProfiles()
    {
        var stored = Load();
        return stored?.Profiles.Select(profile => new OnlineTranslationSettings(profile.Provider, profile.Endpoint,
            profile.Model, stored.DefaultOnline, profile.ProtectedKey.Length > 0)).ToArray() ?? [];
    }
    public void DeleteProfile(string provider)
    {
        var stored = Load();
        if (stored is null) return;
        var remaining = stored.Profiles.Where(profile => profile.Provider != provider).ToArray();
        if (remaining.Length == stored.Profiles.Length) return;
        if (remaining.Length == 0) { Delete(); return; }
        string active = stored.ActiveProvider == provider ? remaining[0].Provider : stored.ActiveProvider;
        bool online = stored.DefaultOnline && remaining.Any(profile => profile.Provider == active && profile.ProtectedKey.Length > 0);
        Write(stored with { ActiveProvider = active, DefaultOnline = online, Profiles = remaining });
    }
    public OnlineTranslationCredentials ReadCredentials(string? provider = null)
    {
        var stored = Load(); var profile = Find(stored, provider) ?? throw new InvalidOperationException("请先配置在线翻译服务。");
        if (profile.ProtectedKey.Length == 0) throw new InvalidOperationException("请在在线翻译设置中填写密钥。");
        byte[]? key = null;
        try
        {
            key = ProtectedData.Unprotect(Convert.FromBase64String(profile.ProtectedKey), null, DataProtectionScope.CurrentUser);
            return new(new(profile.Provider,profile.Endpoint,profile.Model,stored!.DefaultOnline,true),Encoding.UTF8.GetString(key));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        { throw new InvalidOperationException("保存的密钥无法解密，请在当前 Windows 用户下重新填写。"); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    public void Save(OnlineTranslationSettings settings, string? replacementApiKey, bool removeKey = false)
    {
        if (string.IsNullOrWhiteSpace(settings.Provider) || settings.Provider.Length > 64 || settings.Provider.Any(char.IsControl) || string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 256)
            throw new ArgumentException("请填写服务名称和模型名称。");
        if (settings.Endpoint.Length > 2048 || !Uri.TryCreate(settings.Endpoint.Trim(),UriKind.Absolute,out var address) ||
            (address.Scheme != Uri.UriSchemeHttps && !(address.IsLoopback && address.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
            throw new ArgumentException("API 地址须使用 HTTPS，本机服务可使用 HTTP；不要在地址中填写账号、密钥、查询参数或片段。");
        var previousSettings = Load(); var previous = Find(previousSettings, settings.Provider.Trim());
        if (previous is null && previousSettings?.Profiles.Length >= 32)
            throw new InvalidOperationException("最多保存 32 组在线配置，请先删除不再使用的自定义配置。");
        string encrypted = "";
        if (!removeKey)
        {
            if (!string.IsNullOrWhiteSpace(replacementApiKey))
            {
                if (replacementApiKey.Trim().Contains((char)10) || replacementApiKey.Trim().Contains((char)13)) throw new ArgumentException("密钥不能包含换行，请只填写 API Key。");
                if (replacementApiKey.Length > 8192) throw new ArgumentException("密钥过长，请检查填写内容。");
                var bytes = Encoding.UTF8.GetBytes(replacementApiKey.Trim());
                try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else if (previous is not null && previous.Provider == settings.Provider && previous.Endpoint == settings.Endpoint.Trim())
                encrypted = previous.ProtectedKey;
        }
        if (settings.DefaultOnline && encrypted.Length == 0) throw new InvalidOperationException("默认在线翻译需要先保存密钥。");
        var profile = new Profile(settings.Provider.Trim(),settings.Endpoint.Trim(),settings.Model.Trim(),encrypted);
        var profiles = new System.Collections.Generic.List<Profile>(previousSettings?.Profiles ?? Array.Empty<Profile>());
        profiles.RemoveAll(value => value.Provider == profile.Provider); profiles.Add(profile);
        var stored = new StoredSettings(1,profile.Provider,settings.DefaultOnline,profiles.ToArray());
        Write(stored);
    }
    private void Write(StoredSettings stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary,JsonSerializer.Serialize(stored,new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary,path,true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete() => File.Delete(path);
}
