using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Snip.App.Views;
using Snip.Infrastructure;

namespace Snip.App.Services;

public sealed class PinManager(IImageExportService export) : IDisposable
{
    private readonly List<PinWindow> pins = [];
    public void Add(BitmapSource image)
    {
        var window = new PinWindow(image, export); pins.Add(window);
        window.Closed += (_, _) => pins.Remove(window); window.Show(); window.Activate();
    }
    public void ShowManager()
    {
        new PinManagerWindow(pins).Show();
    }
    public void Dispose() { foreach (var pin in pins.ToArray()) pin.Close(); }
}
