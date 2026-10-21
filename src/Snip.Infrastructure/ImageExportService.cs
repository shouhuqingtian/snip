using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Media.Imaging;

namespace Snip.Infrastructure;

public interface IImageExportService
{
    void SavePng(BitmapSource image, string path);
    Task CopyAsync(BitmapSource image);
    Task CopyAsync(BitmapSource image, CancellationToken token, IProgress<ImageCopyProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        return CopyAsync(image).WaitAsync(token);
    }
}

public sealed record ImageCopyProgress(string Stage, int Attempt = 0, int? BlockingProcessId = null, int? Bytes = null);

public sealed class ImageExportService : IImageExportService
{
    public void SavePng(BitmapSource image, string path)
    {
        // Encode before touching destination and move atomically within the selected directory.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        string fullPath = Path.GetFullPath(path);
        string tempPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".snip-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
            File.Move(tempPath, fullPath, true);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    public Task CopyAsync(BitmapSource image) => CopyAsync(image, CancellationToken.None);
    public Task CopyAsync(BitmapSource image, CancellationToken token, IProgress<ImageCopyProgress>? progress = null)
        => NativeImageClipboard.CopyAsync(image, token, progress);
}
