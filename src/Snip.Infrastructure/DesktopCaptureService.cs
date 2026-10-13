using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed record DisplayInfo(string Name, PixelRect Bounds, bool Primary);
public sealed record DesktopFrame(BitmapSource Image, PixelRect Bounds, IReadOnlyList<DisplayInfo> Displays);
public interface IDesktopCaptureService
{
    IReadOnlyList<DisplayInfo> GetDisplays();
    DesktopFrame Capture();
    BitmapSource CaptureRegion(PixelRect region)
    {
        var desktop = Capture();
        if (region.Intersect(desktop.Bounds) != region) throw new InvalidOperationException("选区超出桌面。");
        var image = new CroppedBitmap(desktop.Image, new Int32Rect((int)(region.X - desktop.Bounds.X), (int)(region.Y - desktop.Bounds.Y), (int)region.Width, (int)region.Height));
        image.Freeze(); return image;
    }
    PixelPoint CursorPosition { get; }
    void PlaceOverlay(Window window, PixelRect bounds);
}

public sealed class DesktopCaptureService : IDesktopCaptureService
{
    public PixelPoint CursorPosition
    {
        get { if (!NativeMethods.GetCursorPos(out var p)) throw new Win32Exception(); return new(p.X, p.Y); }
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var result = new List<DisplayInfo>();
        NativeMethods.MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref NativeMethods.NativeRect rect, IntPtr data) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(), Device = string.Empty };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
            result.Add(new(info.Device, new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), (info.Flags & 1) != 0));
            return true;
        };
        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || result.Count == 0)
            throw new InvalidOperationException("无法获取显示器布局。");
        return result.OrderBy(d => d.Name, StringComparer.Ordinal).ToArray();
    }

    public DesktopFrame Capture()
    {
        NativeMethods.DwmFlush();
        var displays = GetDisplays();
        double x = displays.Min(d => d.Bounds.X), y = displays.Min(d => d.Bounds.Y);
        var bounds = new PixelRect(x, y, displays.Max(d => d.Bounds.Right) - x, displays.Max(d => d.Bounds.Bottom) - y);
        return new(CapturePixels(bounds, displays), bounds, displays);
    }

    public BitmapSource CaptureRegion(PixelRect region)
    {
        var displays = GetDisplays();
        var bounds = new PixelRect(displays.Min(d => d.Bounds.X), displays.Min(d => d.Bounds.Y),
            displays.Max(d => d.Bounds.Right) - displays.Min(d => d.Bounds.X), displays.Max(d => d.Bounds.Bottom) - displays.Min(d => d.Bounds.Y));
        if (region.Intersect(bounds) != region) throw new InvalidOperationException("选区超出桌面。");
        return CapturePixels(region, displays);
    }

    private static BitmapSource CapturePixels(PixelRect bounds, IReadOnlyList<DisplayInfo> displays)
    {
        NativeMethods.DwmFlush();
        using var bitmap = new Bitmap((int)bounds.Width, (int)bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Black);
            var target = graphics.GetHdc();
            var source = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                if (source == IntPtr.Zero) throw new Win32Exception();
                // Copy each monitor separately, leaving desktop gaps opaque black.
                foreach (var display in displays)
                {
                    var r = display.Bounds.Intersect(bounds);
                    if (r.IsEmpty) continue;
                    if (!NativeMethods.BitBlt(target, (int)(r.X - bounds.X), (int)(r.Y - bounds.Y), (int)r.Width, (int)r.Height, source, (int)r.X, (int)r.Y, 0x40CC0020))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally { if (source != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, source); graphics.ReleaseHdc(target); }
        }
        var handle = bitmap.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally { NativeMethods.DeleteObject(handle); }
    }

    public void PlaceOverlay(Window window, PixelRect bounds)
    {
        if (!NativeMethods.SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1),
            (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0x0040))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
