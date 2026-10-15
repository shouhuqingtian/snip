namespace Snip.Core;

public sealed record OcrDownloadProgress(long CompletedBytes, long TotalBytes);

public interface IOcrEnhancement
{
    bool Enabled { get; set; }
    bool IsInstalled { get; }
    string EngineDescription { get; }
    Task InstallAsync(IProgress<OcrDownloadProgress>? progress, CancellationToken token);
}

public static class OcrImagePreparation
{
    public static int DetectorShortSide(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        double shorter = Math.Min(width, height), longer = Math.Max(width, height);
        return Math.Clamp((int)Math.Round(Math.Min(Math.Min(736, shorter * 2), shorter * 1600 / longer)), 32, 736);
    }
}
