using System.Windows.Media.Imaging;
using Snip.Infrastructure;

namespace Snip.App.Services;

/// <summary>Only successful image clipboard writes produce feedback; saving and failures do not.</summary>
public sealed class CopyFeedbackExport(IImageExportService inner, CopyNotificationService notifications) : IImageExportService
{
    public void SavePng(BitmapSource image,string path) => inner.SavePng(image,path);
    public Task CopyAsync(BitmapSource image) => CopyAsync(image, CancellationToken.None);
    public async Task CopyAsync(BitmapSource image, CancellationToken token, IProgress<ImageCopyProgress>? progress = null)
    {
        await inner.CopyAsync(image, token, progress);
        token.ThrowIfCancellationRequested();
        notifications.NotifyCopied();
    }
}
