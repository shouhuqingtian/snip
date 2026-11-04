using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Snip.Core;

public enum TranscriptionProtocol { OpenAi, Qwen, Zhipu }
public sealed record TranscriptionSettings(string Provider, string Endpoint, string Model, string Language = "", string TranslationProfile = "", bool HasApiKey = false, TranscriptionProtocol Protocol = TranscriptionProtocol.OpenAi);
public enum VoiceInputMode { Hold, Toggle }
public sealed record VoiceInputPreferences(bool Enabled, HotkeyGesture Hotkey, VoiceInputMode Mode = VoiceInputMode.Hold)
{
    public static VoiceInputPreferences Default { get; } = new(false, new(3, 0x20));
    public void Validate()
    {
        Hotkey.Validate();
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("请选择有效的录音操作方式。");
    }
}
public sealed class TranscriptionCredentials(TranscriptionSettings settings, string apiKey)
{
    public TranscriptionSettings Settings { get; } = settings;
    public string ApiKey { get; } = apiKey;
    public override string ToString() => "Transcription credentials (redacted)";
}
public interface ITranscriptionSettingsStore
{
    TranscriptionSettings? Read(string? provider = null);
    IReadOnlyList<TranscriptionSettings> ReadProfiles();
    VoiceInputPreferences ReadPreferences();
    TranscriptionCredentials ReadCredentials();
    void Save(TranscriptionSettings settings, string? replacementKey, VoiceInputPreferences preferences, bool removeKey = false);
    void SetEnabled(bool enabled);
}
public interface IAudioTranscriber
{
    Task<string> TranscribeAsync(RecordedVoice audio, CancellationToken token);
}
public interface IMicrophoneRecorder
{
    Task<RecordedVoice> RecordAsync(CancellationToken stop, CancellationToken cancel, Action started);
}
public enum VoiceInputFailure { Unknown, NoMicrophone, DeviceAccess, NoVoice, TooShort, RecorderTimeout, Network, Service }
public sealed class VoiceInputException(string message, VoiceInputFailure failure = VoiceInputFailure.Unknown, int nativeCode = 0, Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public VoiceInputFailure Failure { get; } = failure;
    public int NativeCode { get; } = nativeCode;
}
public sealed class RecordedVoice(byte[] wav) : IDisposable
{
    public byte[] Wav { get; } = wav;
    public void Dispose() => CryptographicOperations.ZeroMemory(Wav);
    public override string ToString() => "Recorded audio (redacted)";
}
/// <summary>Preserves queued recording order when completed driver buffers wrap around.</summary>
public sealed class RecordingBufferOrder
{
    private readonly int count;
    private int next;
    public RecordingBufferOrder(int count)
    { if (count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(count)); this.count = count; }
    public void Drain(Func<int, bool> completed, Action<int> consume)
    {
        for (int i = 0; i < count && completed(next); i++)
        {
            int index = next; consume(index); next = (index + 1) % count;
        }
    }
}
public static class TranscriptionProviders
{
    public const int MaximumRecordingSeconds = 120;
    public static readonly string[] Names = ["硅基流动", "阿里云百炼", "智谱AI", "OpenAI", "Groq", "Fireworks", "自定义（兼容接口）"];
    public static TranscriptionSettings Defaults(string provider) => provider switch
    {
        "硅基流动" => new(provider, "https://api.siliconflow.cn/v1/audio/transcriptions", "FunAudioLLM/SenseVoiceSmall"),
        "阿里云百炼" => new(provider, "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation", "qwen3-asr-flash", Protocol: TranscriptionProtocol.Qwen),
        "智谱AI" => new(provider, "https://open.bigmodel.cn/api/paas/v4/audio/transcriptions", "glm-asr", Protocol: TranscriptionProtocol.Zhipu),
        "OpenAI" => new(provider, "https://api.openai.com/v1/audio/transcriptions", "gpt-4o-mini-transcribe"),
        "Groq" => new(provider, "https://api.groq.com/openai/v1/audio/transcriptions", "whisper-large-v3-turbo"),
        "Fireworks" => new(provider, "https://audio-prod.us-virginia-1.direct.fireworks.ai/v1/audio/transcriptions", "whisper-v3"),
        _ => new(provider, "", "")
    };
    public static string[] Models(string provider) => provider switch
    {
        "硅基流动" => ["FunAudioLLM/SenseVoiceSmall"],
        "阿里云百炼" => ["qwen3-asr-flash"],
        "智谱AI" => ["glm-asr"],
        "OpenAI" => ["gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1"],
        "Groq" => ["whisper-large-v3-turbo", "whisper-large-v3"],
        "Fireworks" => ["whisper-v3"],
        _ => []
    };
    public static void Validate(TranscriptionSettings settings)
    {
        _ = SpeechProviders.Endpoint(settings.Endpoint);
        if (!Enum.IsDefined(settings.Protocol)) throw new ArgumentException("请选择有效的语音识别接口协议。");
        if (string.IsNullOrWhiteSpace(settings.Provider) || settings.Provider.Length > 64 || settings.Provider.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 256 || settings.Model.Any(char.IsControl) ||
            settings.Language is not ("" or "zh" or "en") || settings.TranslationProfile is null || settings.TranslationProfile.Length > 64 || settings.TranslationProfile.Any(char.IsControl))
            throw new ArgumentException("请填写有效的语音识别配置名称、模型和语言。");
    }
}
public static class VoiceText
{
    public static string ForTyping(string text)
    {
        if (text is null || text.Length > 20000) throw new VoiceInputException("识别结果过长，请分段录音。");
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\r' or '\n' or '\t') result.Append(' ');
            else if (!Rune.IsControl(rune)) result.Append(rune.ToString());
        }
        string value = result.ToString().Trim();
        if (value.Length == 0) throw new VoiceInputException("没有识别出文字，请靠近麦克风重试。");
        return value;
    }
    public static byte[] PcmToWav(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 8000 || pcm.Length > TranscriptionProviders.MaximumRecordingSeconds * 32000 || pcm.Length % 2 != 0)
            throw new VoiceInputException("录音太短或超过上限，请重新录音。", VoiceInputFailure.TooShort);
        long energy = 0;
        for (int i = 0; i < pcm.Length; i += 2) { int sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]); energy += (long)sample * sample; }
        if (Math.Sqrt(energy / (pcm.Length / 2.0)) < 20) throw new VoiceInputException("没有录到清晰声音，请检查默认麦克风和输入音量。", VoiceInputFailure.NoVoice);
        // Trim only quiet ends; retain 200 ms context and every internal pause.
        int first = -1, last = 0;
        for (int offset = 0; offset < pcm.Length; offset += 640)
        {
            int length = Math.Min(640, pcm.Length - offset); long frameEnergy = 0;
            for (int i = offset; i < offset + length; i += 2) { int sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]); frameEnergy += (long)sample * sample; }
            if (frameEnergy >= 400L * (length / 2)) { if (first < 0) first = offset; last = offset + length; }
        }
        int start = Math.Max(0, first - 6400), end = Math.Min(pcm.Length, last + 6400);
        if (first >= 0 && end - start >= 8000) pcm = pcm[start..end];
        byte[] wav = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length); pcm.CopyTo(wav.AsSpan(44)); return wav;
    }
}
