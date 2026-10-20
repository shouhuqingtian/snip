using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows.Automation;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Reads only geometry/control types. A stuck accessibility provider cannot hold the app UI.</summary>
public static class HoverRegionWorker
{
    public static int Run(Stream input, Stream output)
    {
        using var watchdog = new Timer(_ => Environment.Exit(8), null, Timeout.Infinite, Timeout.Infinite);
        byte[] request = new byte[64], response = new byte[32];
        try
        {
            while (true)
            {
                try { input.ReadExactly(request); } catch (EndOfStreamException) { return 0; }
                watchdog.Change(3000, Timeout.Infinite);
                var handle = new IntPtr(BinaryPrimitives.ReadInt64LittleEndian(request));
                int pid = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(8));
                double Value(int i) => BinaryPrimitives.ReadDoubleLittleEndian(request.AsSpan(12 + i * 8));
                var point = new PixelPoint(Value(0), Value(1));
                var bounds = new PixelRect(Value(2), Value(3), Value(4), Value(5));
                PixelRect result = default;
                if (double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom) && !bounds.IsEmpty && bounds.Contains(point))
                {
                    using var pixels = new PhysicalDesktopScope();
                    GetWindowThreadProcessId(handle, out int actualPid);
                    if (pid == actualPid && IsWindowVisible(handle)) result = Find(handle, bounds, point);
                }
                double[] values = [result.X, result.Y, result.Width, result.Height];
                for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(response.AsSpan(i * 8), values[i]);
                output.Write(response); output.Flush(); watchdog.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { return 0; }
    }

    private static PixelRect Find(IntPtr handle, PixelRect bounds, PixelPoint point)
    {
        var clock = Stopwatch.StartNew();
        var best = bounds;
        bool chromium = BrowserHoverHitTest.IsChromium(handle);
        PixelRect page = default;
        double scale = Math.Max(96, DesktopWindows.GetDpiForWindow(handle)) / 96.0;
        double Area(PixelRect r) => r.Width * r.Height;
        void Consider(PixelRect rect)
        {
            rect = rect.Intersect(bounds).RoundOut();
            if (IsUsefulRegion(rect, scale) && rect.Contains(point) && Area(rect) < Area(best)) best = rect;
        }
        int nativeCount = 0;
        ChildCallback callback = (child, _) =>
        {
            // Native class/rectangle reads have no cross-process provider RPC. Bound their
            // count, not elapsed time since cold-start JIT: that could skip the renderer
            // before the first callback and incorrectly fall back to the whole browser.
            // Accessibility below keeps its own time budget; the helper has a watchdog.
            if (++nativeCount > 512) return false;
            if (IsWindowVisible(child) && GetWindowRect(child, out var rect))
            {
                var region = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top).Intersect(bounds).RoundOut();
                Consider(region);
                if (chromium && region.Contains(point) && IsUsefulRegion(region, scale))
                {
                    var name = new StringBuilder(64); GetClassName(child, name, name.Capacity);
                    // The outer renderer is the complete visible page, including sidebars.
                    // Nested renderer/iframe widgets must not shrink the ordinary selection.
                    if (name.ToString() == "Chrome_RenderWidgetHostHWND" && Area(region) > Area(page)) page = region;
                }
            }
            return true;
        };
        EnumChildWindows(handle, callback, IntPtr.Zero);
        // Area alone cannot establish visibility: CSS dialogs and embedded pages leave
        // smaller background controls in the accessibility tree. Fail safely to the native
        // viewport/window if Chromium cannot expose a hit, rather than choose a hidden row.
        if (chromium)
        {
            var pageBounds = page.IsEmpty ? bounds : page;
            return BrowserHoverHitTest.Find(handle, pageBounds, point, scale) ?? pageBounds;
        }
        try
        {
            var request = new CacheRequest { TreeScope = TreeScope.Element };
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            var root = AutomationElement.FromHandle(handle).GetUpdatedCache(request);
            clock.Restart(); // Allow the first accessibility connection to initialize separately.
            var walker = TreeWalker.ControlViewWalker;
            int visited = 0;
            void Visit(AutomationElement parent, int depth)
            {
                if (depth >= 20 || visited >= 384 || clock.ElapsedMilliseconds > 300) return;
                for (var child = walker.GetFirstChild(parent, request); child is not null; child = walker.GetNextSibling(child, request))
                {
                    if (++visited > 384 || clock.ElapsedMilliseconds > 300) return;
                    var data = child.Cached;
                    if (data.IsOffscreen) continue;
                    var r = data.BoundingRectangle;
                    bool valid = !r.IsEmpty && double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom);
                    bool atomic = data.ControlType == ControlType.Button || data.ControlType == ControlType.Edit || data.ControlType == ControlType.CheckBox ||
                        data.ControlType == ControlType.RadioButton || data.ControlType == ControlType.ComboBox || data.ControlType == ControlType.ListItem ||
                        data.ControlType == ControlType.DataItem || data.ControlType == ControlType.TreeItem || data.ControlType == ControlType.Image || data.ControlType == ControlType.Text;
                    if (valid)
                    {
                        var rect = new PixelRect(r.X, r.Y, r.Width, r.Height).Intersect(bounds);
                        if (!rect.Contains(point)) continue;
                        // Prefer complete panels/lists, not individual fields, labels or rows.
                        if (!atomic && data.ControlType != ControlType.ToolTip) Consider(rect);
                    }
                    if (atomic) continue;
                    Visit(child, depth + 1);
                }
            }
            Visit(root, 0);
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException or System.UnauthorizedAccessException) { }
        return best;
    }
    internal static bool IsUsefulRegion(PixelRect rect, double scale) => rect.Width >= 160 * scale && rect.Height >= 96 * scale;

    private delegate bool ChildCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, ChildCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
}
