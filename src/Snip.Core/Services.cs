namespace Snip.Core;

public interface IHotkeyService : IDisposable
{
    event EventHandler? Triggered;
    bool Register();
}

public interface ITrayService : IDisposable
{
    event EventHandler? CaptureRequested;
    event EventHandler? OpenRequested;
    event EventHandler? ExitRequested;
}

public sealed record ImagePayload(ReadOnlyMemory<byte> PngBytes, int Width, int Height);
public sealed record OcrTextRegion(string Text, PixelRect Bounds, double? Confidence);
public sealed record OcrResult(IReadOnlyList<OcrTextRegion> Regions);
public sealed record AiImageResult(string Text);

public interface IOcrService
{
    Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken cancellationToken);
}

public interface IAiImageService
{
    Task<AiImageResult> AnalyzeAsync(ImagePayload image, string prompt, CancellationToken cancellationToken);
}
