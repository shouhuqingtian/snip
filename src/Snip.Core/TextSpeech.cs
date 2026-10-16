namespace Snip.Core;

public interface ITextSpeechService
{
    Task SpeakAsync(string text, string language, CancellationToken cancellationToken);
}

public sealed class SpeechUnavailableException : InvalidOperationException
{
    public SpeechUnavailableException(string language = "en") : base(language == "zh"
        ? "未安装 Windows 中文语音，请在系统“时间和语言 → 语音”中添加中文语音后重试。"
        : "未安装 Windows 英语语音，请在系统“时间和语言 → 语音”中添加英语语音后重试。") { }
}

public static class SpeechText
{
    public static string Language(string text) => TranslationDirection.Resolve(text) == "zh-en" ? "zh" : "en";
    /// <summary>Bound audio buffers and first-play latency without dropping words or splitting surrogate pairs.</summary>
    public static IReadOnlyList<string> Chunks(string text, int maximumLength = 600)
    {
        if (maximumLength < 32 || maximumLength > 1000) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (text.Length > 60000) throw new ArgumentException("朗读文字过长，请分段处理。", nameof(text));
        var result = new List<string>();
        for (int start = 0; start < text.Length;)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start == text.Length) break;
            int end = Math.Min(start + maximumLength, text.Length);
            if (end < text.Length)
            {
                int boundary = end;
                while (boundary > start + maximumLength / 2 && !char.IsWhiteSpace(text[boundary - 1]) && text[boundary - 1] is not '.' and not '!' and not '?' and not '。' and not '！' and not '？') boundary--;
                if (boundary > start + maximumLength / 2) end = boundary;
                if (char.IsHighSurrogate(text[end - 1])) end--;
            }
            var part = text[start..end].Trim();
            if (part.Length > 0) result.Add(part);
            start = end;
        }
        return result;
    }
}
