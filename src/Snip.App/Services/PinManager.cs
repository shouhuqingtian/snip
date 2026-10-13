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
        var window = new Window { Title = "贴图管理", Width = 420, Height = 330, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = pins.Count == 0 ? "当前没有贴图" : "恢复鼠标交互后可移动或右键设置贴图。", Margin = new Thickness(0, 0, 0, 10) });
        foreach (var pin in pins.ToArray())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = pin.Description, Width = 170, VerticalAlignment = VerticalAlignment.Center });
            var recover = new Button { Content = "恢复交互" }; recover.Click += (_, _) => pin.SetClickThrough(false); row.Children.Add(recover);
            var close = new Button { Content = "关闭" }; close.Click += (_, _) => { pin.Close(); panel.Children.Remove(row); }; row.Children.Add(close); panel.Children.Add(row);
        }
        window.Content = new ScrollViewer { Content = panel }; window.Show();
    }
    public void Dispose() { foreach (var pin in pins.ToArray()) pin.Close(); }
}
