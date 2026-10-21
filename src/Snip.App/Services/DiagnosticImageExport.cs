using System.Windows.Media.Imaging;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.Services;

/// <summary>Records export failures even when a view handles the exception locally.</summary>
public sealed class DiagnosticImageExport(IImageExportService inner, DiagnosticJournal diagnostics) : IImageExportService
{
    public void SavePng(BitmapSource image, string path)
    {
        using var operation = diagnostics.Begin("png-export", new { image.PixelWidth, image.PixelHeight });
        try { inner.SavePng(image, path); operation.Complete(); }
        catch (Exception error) { operation.Fail(error); throw; }
    }
    public Task CopyAsync(BitmapSource image) => CopyAsync(image, CancellationToken.None);
    public async Task CopyAsync(BitmapSource image, CancellationToken token, IProgress<ImageCopyProgress>? progress = null)
    {
        using var operation = diagnostics.Begin("clipboard-export", new { image.PixelWidth, image.PixelHeight });
        var trace = new CopyProgress(operation, progress);
        try { await inner.CopyAsync(image, token, trace); operation.Complete(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { operation.Cancel("copy-cancelled"); throw; }
        catch (Exception error) { operation.Fail(error); throw; }
    }
    private sealed class CopyProgress(DiagnosticOperation operation, IProgress<ImageCopyProgress>? next) : IProgress<ImageCopyProgress>
    {
        public void Report(ImageCopyProgress value)
        {
            operation.Stage(value.Stage, new { value.Attempt, value.BlockingProcessId, value.Bytes });
            next?.Report(value);
        }
    }
}
