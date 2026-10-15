using Snip.Core;

namespace Snip.Ocr;

/// <summary>One selection per result window; local modes never consult online credentials.</summary>
public sealed class SelectableOcrService(IOcrService system, EnhancedOcrService enhanced, IOcrService online, OcrMode initialMode) : IOcrModeSelection
{
    private OcrMode mode = initialMode;
    private string description = "尚未识别";
    public OcrMode Mode
    {
        get => mode;
        set { if (!Enum.IsDefined(value)) throw new ArgumentException("识别方式无效。"); mode = value; }
    }
    public bool Enabled { get => Mode == OcrMode.EnhancedOffline; set => Mode = value ? OcrMode.EnhancedOffline : OcrMode.System; }
    public bool IsInstalled => enhanced.IsInstalled;
    public string EngineDescription => description;
    public IReadOnlyList<string> Languages => Mode == OcrMode.System ? system.Languages : ["zh-Hans-CN", "en-US"];
    public Task InstallAsync(IProgress<OcrDownloadProgress>? progress, CancellationToken token) => enhanced.InstallAsync(progress, token);
    public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
    public async Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
    {
        var selected = Mode;
        token.ThrowIfCancellationRequested();
        OcrResult result;
        switch (selected)
        {
            case OcrMode.System:
                result = await system.RecognizeAsync(image, language, token).ConfigureAwait(false);
                description = "系统 OCR（免下载）";
                break;
            case OcrMode.EnhancedOffline:
                if (!IsInstalled) throw new InvalidOperationException("增强 OCR 模型尚未下载，请在更多设置点击“下载 / 修复增强 OCR”，或选择系统识别。");
                result = await enhanced.RecognizeEnhancedAsync(image, language, token).ConfigureAwait(false);
                description = enhanced.EngineDescription;
                break;
            case OcrMode.Online:
                result = await online.RecognizeAsync(image, language, token).ConfigureAwait(false);
                description = "在线视觉 OCR（使用当前在线服务）";
                break;
            default: throw new ArgumentException("识别方式无效。");
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
