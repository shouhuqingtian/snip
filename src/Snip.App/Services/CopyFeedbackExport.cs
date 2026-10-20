using System.Windows.Media.Imaging;
using Snip.Infrastructure;

namespace Snip.App.Services;

/// <summary>Only successful image clipboard writes produce feedback; saving and failures do not.</summary>
public sealed class CopyFeedbackExport(IImageExportService inner, CopyNotificationService notifications) : IImageExportService
{
    public void SavePng(BitmapSource image,string path) => inner.SavePng(image,path);
    public async Task CopyAsync(BitmapSource image)
    {
        await inner.CopyAsync(image);
        notifications.NotifyCopied();
    }
}
