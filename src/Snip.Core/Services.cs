namespace Snip.Core;

public interface IHotkeyService : IDisposable
{
    event EventHandler? Triggered;
    bool Register();
    bool Register(HotkeyGesture gesture);
    HotkeyGesture? Current { get; }
}

public interface ITrayService : IDisposable
{
    event EventHandler? CaptureRequested;
    event EventHandler? OpenRequested;
    event EventHandler? ExitRequested;
    event EventHandler? PinsRequested;
    event EventHandler? CaptureCancelRequested;
    event EventHandler? DiagnosticsRequested;
    void SetCaptureStatus(string? status);
    void SetHotkey(HotkeyGesture gesture);
}

public sealed record ImagePayload(ReadOnlyMemory<byte> PngBytes, int Width, int Height);
public sealed record OcrTextRegion(string Text, PixelRect Bounds, double? Confidence);
public sealed record OcrResult(IReadOnlyList<OcrTextRegion> Regions);
public sealed record AiImageResult(string Text);

public interface IOcrService
{
    IReadOnlyList<string> Languages { get; }
    Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken cancellationToken);
    Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken cancellationToken);
}

public interface ITranslationService
{
    // Availability is a local hint; it must not load a model or contact a provider.
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    Task<string> TranslateAsync(string text, string direction, CancellationToken cancellationToken);
    Task<string> TranslateAsync(string text, string direction, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        => TranslateAsync(text, direction, cancellationToken);
}

public interface ITextClipboardService
{
    Task CopyAsync(string text, CancellationToken cancellationToken, IProgress<int>? retries = null);
}

public sealed record TranslationProgress(string Stage, int CompletedSegments = 0, int TotalSegments = 0, int GeneratedTokens = 0);

public interface IAiImageService
{
    Task<AiImageResult> AnalyzeAsync(ImagePayload image, string prompt, CancellationToken cancellationToken);
}
