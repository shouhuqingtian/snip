using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Snip.App.ViewModels;
using Snip.Core;

namespace Snip.App.Views;

/// <summary>Nonactivating controls remain outside the captured rectangle; browser keeps wheel focus.</summary>
public sealed class ManualScrollCaptureWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr hwnd, int index, IntPtr style);
    public PixelRect Viewport { get; internal set; }
    public ManualScrollCaptureWindow(ManualScrollCaptureViewModel vm, bool desktop = false)
    {
        Ui.ApplyWindow(this); Title = desktop ? "自用截图 · 桌面手动长截图" : "自用截图 · 网页选区拖选";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        ShowActivated = false; Topmost = true; AllowsTransparency = true; Background = Brushes.Transparent;
        Width = 410; Height = 58; DataContext = vm;
        var row = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var done = new Button { Name = "CompleteManualButton", MinWidth = 58, Margin = new(4, 0, 0, 0) };
        done.SetBinding(ContentControl.ContentProperty, new Binding(nameof(vm.CompleteText)));
        Ui.Style(done, "PrimaryButton"); done.SetBinding(Button.CommandProperty, new Binding(nameof(vm.CompleteCommand)));
        var cancel = new Button { Name = "CancelManualButton", Content = "取消", MinWidth = 58, Margin = new(4, 0, 0, 0) };
        Ui.Style(cancel, "QuietButton"); cancel.SetBinding(Button.CommandProperty, new Binding(nameof(vm.CancelCommand)));
        buttons.Children.Add(done); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
        var text = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(vm.Status))); row.Children.Add(text);
        Content = new Border { Background = Ui.Brush("SurfaceBrush"), BorderBrush = Ui.Brush("StrokeBrush"), BorderThickness = new(1), CornerRadius = new(8), Padding = new(10, 6, 10, 6), Child = row };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetStyle(hwnd, -20, new(GetStyle(hwnd, -20).ToInt64() | 0x08000000 | 0x80)); // NOACTIVATE / TOOLWINDOW
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _, int message, IntPtr _, IntPtr _, ref bool handled) =>
            { if (message == 0x0021) { handled = true; return new IntPtr(3); } return IntPtr.Zero; });
        };
    }
}
