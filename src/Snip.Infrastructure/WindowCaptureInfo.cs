using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed record WindowCaptureInfo(IntPtr Handle, PixelRect Bounds, int ProcessId);

public static class DesktopWindows
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    public static IntPtr Foreground => GetForegroundWindow();
    public static void Activate(IntPtr hwnd) { if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd); }
    private delegate bool Callback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int process);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(NativeMethods.NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.NativeRect rect, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetCloaked(IntPtr hwnd, int attribute, out int cloaked, int size);
    public static IReadOnlyList<WindowCaptureInfo> Snapshot()
    {
        using var pixels = new PhysicalDesktopScope();
        var result = new List<WindowCaptureInfo>();
        Callback callback = (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (DwmGetCloaked(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out int pid); if (pid == Environment.ProcessId) return true;
            if (DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<NativeMethods.NativeRect>()) != 0 && !GetWindowRect(hwnd, out rect)) return true;
            if (rect.Right > rect.Left && rect.Bottom > rect.Top) result.Add(new(hwnd, new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), pid));
            return true;
        };
        EnumWindows(callback, IntPtr.Zero); return result;
    }

    /// <summary>Resolve the actual window at a desktop pixel, rather than a covering rectangle.
    /// Transparent overlays and desktop host windows can have bounds covering a browser.</summary>
    public static WindowCaptureInfo? AtPhysicalPoint(PixelPoint point)
    {
        using var pixels = new PhysicalDesktopScope();
        var hit = WindowFromPhysicalPoint(new() { X = (int)point.X, Y = (int)point.Y });
        var hwnd = GetAncestor(hit, 2); // GA_ROOT: renderer children belong to the browser top-level window.
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return null;
        if (DwmGetCloaked(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        if (DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<NativeMethods.NativeRect>()) != 0 && !GetWindowRect(hwnd, out rect)) return null;
        GetWindowThreadProcessId(hwnd, out int pid);
        return new(hwnd, new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), pid);
    }
}
