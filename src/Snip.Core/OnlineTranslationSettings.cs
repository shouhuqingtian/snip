namespace Snip.Core;

public sealed record OnlineTranslationSettings(string Provider, string Endpoint, string Model, bool DefaultOnline = false, bool HasApiKey = false, string VisionModel = "");

/// <summary>Ephemeral credentials; never serialize or put this object in diagnostics.</summary>
public sealed class OnlineTranslationCredentials(OnlineTranslationSettings settings, string apiKey)
{
    public OnlineTranslationSettings Settings { get; } = settings;
    public string ApiKey { get; } = apiKey;
    public override string ToString() => "Online translation credentials (redacted)";
}

public interface IOnlineTranslationSettingsStore
{
    OnlineTranslationSettings? Read(string? provider = null);
    OnlineTranslationCredentials ReadCredentials(string? provider = null);
    void Save(OnlineTranslationSettings settings, string? replacementApiKey, bool removeKey = false);
    IReadOnlyList<OnlineTranslationSettings> ReadProfiles();
    void DeleteProfile(string provider);
    void Delete();
}
