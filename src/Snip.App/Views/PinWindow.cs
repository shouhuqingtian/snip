using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.Services;
using Snip.Infrastructure;

namespace Snip.App.Views;

public sealed class PinWindow : Window
{
    private readonly BitmapSource bitmap;
    private bool clickThrough;
    private double scale = 1;
    private readonly Image image;
    public string Description => $"贴图 {bitmap.PixelWidth}×{bitmap.PixelHeight}";
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr hwnd, int index, IntPtr style);
    public PinWindow(BitmapSource bitmap, IImageExportService export)
    {
        Ui.ApplyWindow(this);
        this.bitmap = bitmap; Title = "自用截图 · 贴图"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; AllowsTransparency = true; Background = Brushes.Transparent;
        scale = Math.Min(1, Math.Min(650.0 / bitmap.PixelWidth, 500.0 / bitmap.PixelHeight));
        Width = Math.Max(120, bitmap.PixelWidth * scale + 4); Height = Math.Max(80, bitmap.PixelHeight * scale + 4);
        image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        Content = new Border { Child = image, Background = Brushes.White, BorderBrush = Ui.Brush("AccentBrush"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6) };
        var menu = new ContextMenu(); ContextMenu = menu;
        void Add(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("复制图片", async () => { try { await export.CopyAsync(bitmap); } catch (Exception e) { MessageBox.Show(e.Message); } });
        Add("保存 PNG…", () => { var path = new EditorDialogs(() => this).ChoosePngPath(); if (path is null) return; try { export.SavePng(bitmap, path); } catch (Exception e) { MessageBox.Show(e.Message); } });
        Add("透明度 100%", () => image.Opacity = 1); Add("透明度 70%", () => image.Opacity = .7); Add("透明度 40%", () => image.Opacity = .4);
        Add("开启鼠标穿透（从托盘贴图管理恢复）", () => SetClickThrough(true));
        Add("关闭贴图", Close);
        MouseLeftButtonDown += (_, e) => { if (!clickThrough) { DragMove(); e.Handled = true; } };
        MouseWheel += (_, e) => { scale = Math.Clamp(scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), .05, 4); Width = Math.Max(60, bitmap.PixelWidth * scale + 4); Height = Math.Max(50, bitmap.PixelHeight * scale + 4); e.Handled = true; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    public void SetClickThrough(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle; if (hwnd == IntPtr.Zero) return;
        long style = GetStyle(hwnd, -20).ToInt64();
        SetStyle(hwnd, -20, new IntPtr(enabled ? style | 0x20 | 0x80000 : style & ~0x20)); clickThrough = enabled;
        if (!enabled) { Show(); Activate(); }
    }
}
