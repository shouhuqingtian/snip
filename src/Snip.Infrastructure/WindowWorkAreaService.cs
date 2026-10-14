using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Normal windows use physical monitor work areas; full-screen overlays and pins opt out.</summary>
public sealed class WindowWorkAreaService : IDisposable
{
    private static readonly ConditionalWeakTable<Window, WindowWorkAreaService> Attached = new();
    private readonly Window window;
    private HwndSource? source;
    private IntPtr handle;
    private double originalMinWidth, originalMinHeight;
    private bool loaded, closed, applying, queued, moving;
    private WindowWorkAreaService(Window window)
    {
        this.window = window;
        window.SourceInitialized += Initialize;
        window.Loaded += Loaded;
        window.Closed += Closed;
    }
    public static void Attach(Window window) => Attached.GetValue(window, value => new(value));
    public static void Recover(Window window)
    {
        if (Attached.TryGetValue(window, out var service)) service.Fit(center: true, chooseCursor: true);
    }
    private void Initialize(object? sender, EventArgs args)
    {
        if (window.WindowStyle == WindowStyle.None) return;
        handle = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(handle); source?.AddHook(Hook);
    }
    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (loaded || handle == IntPtr.Zero) return;
        loaded = true; originalMinWidth = window.MinWidth; originalMinHeight = window.MinHeight;
        Fit(center: true, chooseCursor: true);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0231) moving = true; // WM_ENTERSIZEMOVE
        if (message == 0x0232) { moving = false; QueueFit(); } // WM_EXITSIZEMOVE
        // Let WPF handle WM_DPICHANGED's suggested rectangle, then repair against actual target DPI.
        if (message is 0x02E0 or 0x007E or 0x001A or 0x0047 && !moving && !applying) QueueFit();
        return IntPtr.Zero;
    }
    private void QueueFit()
    {
        if (!loaded || closed || queued) return;
        queued = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            queued = false; if (!closed && !moving) Fit(center: false, chooseCursor: false);
        }));
    }
    private void Fit(bool center, bool chooseCursor)
    {
        if (!loaded || closed || applying || window.WindowState != WindowState.Normal) return;
        applying = true;
        try
        {
            IntPtr monitor;
            if (chooseCursor && window.Owner is { } owner)
                monitor = MonitorFromWindow(new WindowInteropHelper(owner).Handle, 2);
            else if (chooseCursor && NativeMethods.GetCursorPos(out var cursor))
                monitor = MonitorFromPoint(cursor, 2);
            else monitor = MonitorFromWindow(handle, 2);
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(), Device = string.Empty };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;
            var area = new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right-info.Work.Left, info.Work.Bottom-info.Work.Top);
            if (area.IsEmpty) return;
            if (chooseCursor && MonitorFromWindow(handle, 2) != monitor)
            {
                // Move first. The destination HWND DPI is authoritative; don't reuse primary-screen DIP coordinates.
                if (!NativeMethods.SetWindowPos(handle,IntPtr.Zero,(int)area.X+8,(int)area.Y+8,0,0,0x0015)) throw new Win32Exception();
            }
            double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
            var available = WindowPlacement.InsetWorkArea(area, scale);
            window.MinWidth = Math.Min(originalMinWidth, available.Width/scale);
            window.MinHeight = Math.Min(originalMinHeight, available.Height/scale);
            if (!GetWindowRect(handle, out var rect)) throw new Win32Exception();
            var bounds = new PixelRect(rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top);
            bounds = bounds with { Width=Math.Max(bounds.Width,window.MinWidth*scale), Height=Math.Max(bounds.Height,window.MinHeight*scale) };
            var fitted = WindowPlacement.Fit(bounds, area, scale, center);
            if (rect.Left!=(int)fitted.X || rect.Top!=(int)fitted.Y || rect.Right-rect.Left!=(int)fitted.Width || rect.Bottom-rect.Top!=(int)fitted.Height)
                if (!NativeMethods.SetWindowPos(handle,IntPtr.Zero,(int)fitted.X,(int)fitted.Y,(int)fitted.Width,(int)fitted.Height,0x0014)) throw new Win32Exception();
        }
        finally { applying = false; }
    }
    private void Closed(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (closed) return; closed = true;
        source?.RemoveHook(Hook); source = null;
        window.SourceInitialized -= Initialize; window.Loaded -= Loaded; window.Closed -= Closed;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativeMethods.NativePoint point,uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool GetWindowRect(IntPtr window,out NativeMethods.NativeRect rect);
}
