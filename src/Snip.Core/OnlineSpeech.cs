namespace Snip.Core;

public enum SpeechMode { System, Online }
public enum SpeechProtocol { OpenAi, Qwen, MiniMax }
public sealed record OnlineSpeechSettings(string Provider, SpeechProtocol Protocol, string Endpoint, string Model, string Voice,
    double Speed = 1, string TranslationProfile = "", bool HasApiKey = false);
public sealed class OnlineSpeechCredentials(OnlineSpeechSettings settings, string apiKey)
{
    public OnlineSpeechSettings Settings { get; } = settings;
    public string ApiKey { get; } = apiKey;
    public override string ToString() => "Speech credentials (redacted)";
}
public interface ISpeechSettingsStore
{
    SpeechMode ReadMode();
    void SaveMode(SpeechMode mode);
    OnlineSpeechSettings? Read(string? provider = null);
    IReadOnlyList<OnlineSpeechSettings> ReadProfiles();
    OnlineSpeechCredentials ReadCredentials(string? provider = null);
    void Save(OnlineSpeechSettings settings, string? replacementApiKey, SpeechMode mode, bool removeKey = false);
    void DeleteProfile(string provider);
}
public interface IConfigurableTextSpeechService : ITextSpeechService
{
    SpeechMode Mode { get; set; }
}
public sealed record SpeechAudio(byte[] Data, string ContentType);
public interface IOnlineSpeechSynthesizer
{
    Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken token);
}
public interface ISpeechAudioPlayer
{
    Task PlayAsync(SpeechAudio audio, double speed, CancellationToken token);
}
/// <summary>Only controlled user-facing messages, without provider response bodies or credentials.</summary>
public sealed class SpeechServiceException(string hint) : InvalidOperationException(hint);

public static class SpeechProviders
{
    public static readonly string[] Names = ["OpenAI", "硅基流动", "通义千问 TTS", "MiniMax"];
    public static OnlineSpeechSettings Defaults(string provider) => provider switch
    {
        "OpenAI" => new(provider, SpeechProtocol.OpenAi, "https://api.openai.com/v1/audio/speech", "gpt-4o-mini-tts", "coral"),
        "硅基流动" => new(provider, SpeechProtocol.OpenAi, "https://api.siliconflow.cn/v1/audio/speech", "FunAudioLLM/CosyVoice2-0.5B", "FunAudioLLM/CosyVoice2-0.5B:alex"),
        "通义千问 TTS" => new(provider, SpeechProtocol.Qwen, "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation", "qwen3-tts-flash", "Cherry"),
        "MiniMax" => new(provider, SpeechProtocol.MiniMax, "https://api.minimaxi.com/v1/t2a_v2", "speech-2.8-hd", "male-qn-qingse"),
        _ => new(provider, SpeechProtocol.OpenAi, "", "", "")
    };
    public static string[] Models(string provider) => provider switch
    {
        "OpenAI" => ["gpt-4o-mini-tts", "tts-1", "tts-1-hd"],
        "硅基流动" => ["FunAudioLLM/CosyVoice2-0.5B", "FunAudioLLM/CosyVoice2-0.5B-2512"],
        "通义千问 TTS" => ["qwen3-tts-flash", "qwen-tts"],
        "MiniMax" => ["speech-2.8-hd", "speech-2.8-turbo", "speech-2.6-hd", "speech-2.6-turbo", "speech-02-hd", "speech-02-turbo"],
        _ => []
    };
    public static string[] Voices(string provider) => provider switch
    {
        "OpenAI" => ["coral", "alloy", "nova", "shimmer", "onyx", "echo"],
        "硅基流动" => ["FunAudioLLM/CosyVoice2-0.5B:alex", "FunAudioLLM/CosyVoice2-0.5B:anna", "FunAudioLLM/CosyVoice2-0.5B:david", "FunAudioLLM/CosyVoice2-0.5B:bella"],
        "通义千问 TTS" => ["Cherry", "Serena", "Ethan", "Chelsie"],
        "MiniMax" => ["male-qn-qingse", "female-shaonv", "female-yujie", "male-qn-jingying"],
        _ => []
    };
    public static Uri Endpoint(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 2048 || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http")) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请填写完整语音 API 地址，使用 HTTPS；本机服务可用 HTTP。地址不能包含密钥或查询参数。");
        return uri;
    }
    public static bool SameService(Uri speech, Uri translation) =>
        speech.Scheme == translation.Scheme && speech.Host.Equals(translation.Host, StringComparison.OrdinalIgnoreCase) && speech.Port == translation.Port;
    public static void Validate(OnlineSpeechSettings settings)
    {
        _ = Endpoint(settings.Endpoint);
        foreach (string value in new[] { settings.Provider, settings.Model, settings.Voice })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("请填写有效的配置名称、语音模型和音色。");
        if (settings.Provider.Length > 64 || !Enum.IsDefined(settings.Protocol) || !double.IsFinite(settings.Speed) || settings.Speed < .5 || settings.Speed > 2 || settings.TranslationProfile is null || settings.TranslationProfile.Length > 64 || settings.TranslationProfile.Any(char.IsControl))
            throw new ArgumentException("语音配置无效；播放速度应为 0.5–2 倍。");
    }
}
