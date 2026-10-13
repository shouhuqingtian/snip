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
    public async Task CopyAsync(BitmapSource image)
    {
        using var operation = diagnostics.Begin("clipboard-export", new { image.PixelWidth, image.PixelHeight });
        try { await inner.CopyAsync(image); operation.Complete(); }
        catch (Exception error) { operation.Fail(error); throw; }
    }
}
