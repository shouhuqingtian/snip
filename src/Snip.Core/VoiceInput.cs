using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Snip.Core;

public sealed record TranscriptionSettings(string Provider, string Endpoint, string Model, string Language = "", string TranslationProfile = "", bool HasApiKey = false);
public sealed record VoiceInputPreferences(bool Enabled, HotkeyGesture Hotkey)
{
    public static VoiceInputPreferences Default { get; } = new(false, new(3, 0x20));
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
    public static readonly string[] Names = ["硅基流动", "OpenAI", "自定义（兼容接口）"];
    public static TranscriptionSettings Defaults(string provider) => provider switch
    {
        "硅基流动" => new(provider, "https://api.siliconflow.cn/v1/audio/transcriptions", "FunAudioLLM/SenseVoiceSmall"),
        "OpenAI" => new(provider, "https://api.openai.com/v1/audio/transcriptions", "gpt-4o-mini-transcribe"),
        _ => new(provider, "", "")
    };
    public static string[] Models(string provider) => provider switch
    {
        "硅基流动" => ["FunAudioLLM/SenseVoiceSmall"],
        "OpenAI" => ["gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1"],
        _ => []
    };
    public static void Validate(TranscriptionSettings settings)
    {
        _ = SpeechProviders.Endpoint(settings.Endpoint);
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
            throw new VoiceInputException("录音太短或超过上限，请按住快捷键说话后再松开。", VoiceInputFailure.TooShort);
        long energy = 0;
        for (int i = 0; i < pcm.Length; i += 2) { int sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]); energy += (long)sample * sample; }
        if (Math.Sqrt(energy / (pcm.Length / 2.0)) < 20) throw new VoiceInputException("没有录到清晰声音，请检查默认麦克风和输入音量。", VoiceInputFailure.NoVoice);
        byte[] wav = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length); pcm.CopyTo(wav.AsSpan(44)); return wav;
    }
}
