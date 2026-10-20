using System;
using System.Runtime.InteropServices;
using System.Text;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Read the visible renderer before our frozen overlay occludes Chromium.</summary>
public static class BrowserPageGeometry
{
    private delegate bool ChildCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, ChildCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeMethods.NativeRect bounds);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);

    public static WindowCaptureInfo Snapshot(WindowCaptureInfo target)
    {
        using var pixels = new PhysicalDesktopScope();
        if (!BrowserHoverHitTest.IsChromium(target.Handle)) return target;
        PixelRect page = default;
        int count = 0;
        double scale = Math.Max(96, DesktopWindows.GetDpiForWindow(target.Handle)) / 96.0;
        ChildCallback callback = (child, _) =>
        {
            // Native geometry only: no UI Automation, provider calls, names or page text.
            if (++count > 512) return false;
            if (!IsWindowVisible(child)) return true;
            var name = new StringBuilder(64); GetClassName(child, name, name.Capacity);
            if (name.ToString() != "Chrome_RenderWidgetHostHWND" || !GetWindowRect(child, out var rect)) return true;
            var bounds = new PixelRect(rect.Left, rect.Top, rect.Right-rect.Left, rect.Bottom-rect.Top).Intersect(target.Bounds).RoundOut();
            if (HoverRegionWorker.IsUsefulRegion(bounds, scale) && bounds.Width*bounds.Height > page.Width*page.Height) page = bounds;
            return true;
        };
        EnumChildWindows(target.Handle, callback, IntPtr.Zero);
        return target with { PageBounds = page.IsEmpty ? null : page };
    }
}
