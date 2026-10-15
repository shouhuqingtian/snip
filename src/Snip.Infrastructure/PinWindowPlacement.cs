using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Snip.Core;
namespace Snip.Infrastructure;

public static class PinWindowPlacement
{
    public static PixelRect Bounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle,out var rect)) throw new Win32Exception();
        return new(rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top);
    }
    public static void Fit(Window window,PixelRect? preferred=null,bool nearCursor=false)
    {
        var hwnd=new WindowInteropHelper(window).Handle;
        var bounds=preferred ?? Bounds(window);
        var native=new NativeMethods.NativeRect {Left=(int)bounds.X,Top=(int)bounds.Y,Right=(int)bounds.Right,Bottom=(int)bounds.Bottom};
        var monitor=MonitorFromRect(ref native,2);
        if(nearCursor && NativeMethods.GetCursorPos(out var cursor))monitor=MonitorFromPoint(cursor,2);
        var info=new NativeMethods.MonitorInfo { Size=Marshal.SizeOf<NativeMethods.MonitorInfo>(),Device=string.Empty };
        if(!NativeMethods.GetMonitorInfo(monitor,ref info))throw new Win32Exception();
        var area=new PixelRect(info.Work.Left,info.Work.Top,info.Work.Right-info.Work.Left,info.Work.Bottom-info.Work.Top);
        var fit=WindowPlacement.Fit(bounds,area,Math.Max(96,GetDpiForWindow(hwnd))/96.0,center:nearCursor);
        if(!NativeMethods.SetWindowPos(hwnd,IntPtr.Zero,(int)fit.X,(int)fit.Y,(int)fit.Width,(int)fit.Height,0x0014))throw new Win32Exception();
    }
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromRect(ref NativeMethods.NativeRect rect,uint flags);
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromPoint(NativeMethods.NativePoint point,uint flags);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetWindowRect(IntPtr hwnd,out NativeMethods.NativeRect rect);
}
