using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Snip.Infrastructure;

public interface IImageExportService
{
    void SavePng(BitmapSource image, string path);
    Task CopyAsync(BitmapSource image);
}

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

    public async Task CopyAsync(BitmapSource image)
    {
        // SetImage supplies the standard WPF bitmap format consumed by Paint and other desktop apps.
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetImage(image); return; }
            catch (COMException) when (attempt < 4) { await Task.Delay(80 * (attempt + 1)); }
        }
    }
}
