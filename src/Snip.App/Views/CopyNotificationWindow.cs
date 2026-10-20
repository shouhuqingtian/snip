using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Snip.Infrastructure;

namespace Snip.App.Views;

/// <summary>A small, input-transparent acknowledgment; owns no screenshot or clipboard data.</summary>
public sealed class CopyNotificationWindow : Window
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Device; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    private readonly DispatcherTimer lifetime = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private HwndSource? source;
    public CopyNotificationWindow()
    {
        Style = null; Title = "自用截图 · 已添加到剪贴板";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        Width = 180; Height = 132;
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new UiIcon("check") { Width = 46, Height = 46, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,16) });
        content.Children.Add(new TextBlock { Text = "已添加到剪贴板", FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        Content = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(235,53,57,59)), Child = content };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle,-20,new(GetWindowLongPtr(handle,-20).ToInt64() | 0x08000000L | 0x80 | 0x20)); // NOACTIVATE / TOOLWINDOW / TRANSPARENT
            source = HwndSource.FromHwnd(handle); source?.AddHook(Hook);
        };
        lifetime.Tick += Expired;
        Loaded += (_, _) => { PositionOnCursorMonitor(); lifetime.Start(); };
        Closed += (_, _) => { lifetime.Stop(); lifetime.Tick -= Expired; source?.RemoveHook(Hook); source = null; };
    }
    private void Expired(object? sender, EventArgs args) => Close();
    private static IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0084) { handled=true; return new(-1); } // HTTRANSPARENT
        if (message == 0x0021) { handled=true; return new(3); } // MA_NOACTIVATE
        return IntPtr.Zero;
    }
    private void PositionOnCursorMonitor()
    {
        var point = new DesktopCaptureService().CursorPosition;
        var monitor = MonitorFromPoint(new() { X=(int)point.X, Y=(int)point.Y },2);
        var info = new MonitorInfo { Size=Marshal.SizeOf<MonitorInfo>(), Device="" };
        if (!GetMonitorInfo(monitor,ref info)) throw new Win32Exception();
        var handle = new WindowInteropHelper(this).Handle;
        // Move first so this HWND reports the destination monitor's DPI.
        if (!SetWindowPos(handle,new(-1),info.Work.Left+8,info.Work.Top+8,0,0,0x0011)) throw new Win32Exception();
        double scale = Math.Max(96,DesktopWindows.GetDpiForWindow(handle))/96.0;
        int width=Math.Min((int)Math.Ceiling(Width*scale),info.Work.Right-info.Work.Left);
        int height=Math.Min((int)Math.Ceiling(Height*scale),info.Work.Bottom-info.Work.Top);
        int x=info.Work.Left+(info.Work.Right-info.Work.Left-width)/2, y=info.Work.Top+(info.Work.Bottom-info.Work.Top-height)/2;
        if (!SetWindowPos(handle,new(-1),x,y,width,height,0x0010)) throw new Win32Exception();
    }
}
