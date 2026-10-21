using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Four small, nonactivating composition surfaces instead of a viewport-sized layered bitmap.</summary>
public sealed class CaptureOutline : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    private readonly HwndSource[] edges = new HwndSource[4];
    private PixelRect[] bounds = new PixelRect[4];
    private bool visible, disposed;
    public int UpdateCount { get; private set; }
    public long SurfacePixels { get; private set; }

    public CaptureOutline(Color color)
    {
        try
        {
            for (int i = 0; i < edges.Length; i++)
            {
                var edge = edges[i] = new HwndSource(new HwndSourceParameters("SelfSnip.CaptureEdge")
                {
                    WindowStyle = unchecked((int)0x80000000), // POPUP, initially hidden
                    ExtendedWindowStyle = 0x08000000 | 0x80 | 0x20 | 0x8, // NOACTIVATE / TOOLWINDOW / TRANSPARENT / TOPMOST
                    Width = 1, Height = 1
                });
                edge.CompositionTarget.BackgroundColor = color;
                edge.RootVisual = new DrawingVisual();
                edge.AddHook(Hook);
            }
        }
        catch { Dispose(); throw; }
    }
    private static IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0084) { handled = true; return new(-1); } // HTTRANSPARENT
        if (message == 0x0021) { handled = true; return new(3); } // MA_NOACTIVATE
        return IntPtr.Zero;
    }
    public void Show(PixelRect selection, int thickness, PixelRect? outsideClip = null)
    {
        if (disposed) return;
        var r = selection.RoundOut();
        if (r.IsEmpty) { Hide(); return; }
        int line = Math.Max(1, Math.Min(thickness, (int)Math.Min(r.Width, r.Height)));
        var next = outsideClip is { } clip ? new[] {
            new PixelRect(r.X - line, r.Y - line, r.Width + 2 * line, line).Intersect(clip),
            new PixelRect(r.X - line, r.Bottom, r.Width + 2 * line, line).Intersect(clip),
            new PixelRect(r.X - line, r.Y, line, r.Height).Intersect(clip),
            new PixelRect(r.Right, r.Y, line, r.Height).Intersect(clip) } : new[] {
            new PixelRect(r.X, r.Y, r.Width, line),
            new PixelRect(r.X, r.Bottom - line, r.Width, line),
            new PixelRect(r.X, r.Y + line, line, Math.Max(0, r.Height - 2 * line)),
            new PixelRect(r.Right - line, r.Y + line, line, Math.Max(0, r.Height - 2 * line)) };
        bool changed = !visible;
        SurfacePixels = 0;
        for (int i = 0; i < edges.Length; i++)
        {
            var edge = next[i]; SurfacePixels += (long)(edge.Width * edge.Height);
            if (edge.IsEmpty) { ShowWindow(edges[i].Handle, 0); continue; }
            if (visible && edge == bounds[i]) continue;
            changed = true;
            if (!SetWindowPos(edges[i].Handle, new(-1), (int)edge.X, (int)edge.Y, (int)edge.Width, (int)edge.Height, 0x0050)) throw new Win32Exception();
        }
        bounds = next; visible = true;
        if (changed) UpdateCount++;
    }
    public void Hide()
    {
        if (disposed || !visible) return;
        foreach (var edge in edges) ShowWindow(edge.Handle, 0);
        visible = false;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var edge in edges) edge?.Dispose();
    }
}
