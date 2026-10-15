using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.Services;

/// <summary>Hooks exist only during this capture, pass wheel input to Chrome, and never log keystrokes.</summary>
public sealed class ManualScrollCaptureController : IDisposable
{
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private readonly Hook mouse, keyboard;
    private readonly Action cancel;
    private readonly DispatcherTimer focus = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
    private IntPtr mouseHook, keyHook, browser;
    private ManualScrollCaptureWindow? window;
    private CaptureOutline? outline;
    private int borderThickness = 2;
    private bool previewVisible = true;
    private bool disposed;
    public int PreviewUpdates => outline?.UpdateCount ?? 0;
    public long PreviewSurfacePixels => outline?.SurfacePixels ?? 0;
    public DragCaptureControl Control { get; } = new();
    public ManualScrollCaptureViewModel ViewModel { get; }
    public PixelRect ToolbarBounds { get; private set; }
    public PixelRect Region { get; private set; }
    public string? CancellationReason { get; private set; }
    public ManualScrollCaptureController(Action cancel)
    {
        this.cancel = cancel; ViewModel = new(Control, cancel);
        mouse = Mouse; keyboard = Keyboard;
        refresh.Tick += (_, _) => { refresh.Stop(); DrawSelection(); };
        focus.Tick += (_, _) =>
        {
            if (!disposed && browser != IntPtr.Zero && DesktopWindows.Foreground != browser)
            { CancellationReason = "浏览器焦点发生变化，手动长截图已取消。"; cancel(); }
        };
    }
    public void Bind(PixelRect region, IntPtr target)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ManualScrollCaptureController));
        Region = region; browser = target;
        Control.Configure((int)region.Width, (int)region.Height);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        var monitor = MonitorFromPoint(new() { X = (int)(region.X + region.Width / 2), Y = (int)(region.Y + region.Height / 2) }, 2);
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        var area = new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        window = new(ViewModel) { Viewport = region }; window.Show();
        // Move onto the destination monitor before consulting that HWND's authoritative DPI.
        Move(window, new(region.X, Math.Max(area.Y, region.Y - 90), 410, 58));
        double scale = Math.Max(96, DesktopWindows.GetDpiForWindow(new WindowInteropHelper(window).Handle)) / 96.0;
        double width = 410 * scale, height = 58 * scale, gap = 8;
        var choices = new[]
        {
            new PixelRect(Math.Clamp(region.Right - width, area.X, Math.Max(area.X, area.Right - width)), region.Y - height - gap, width, height),
            new PixelRect(Math.Clamp(region.Right - width, area.X, Math.Max(area.X, area.Right - width)), region.Bottom + gap, width, height),
            new PixelRect(region.Right + gap, Math.Clamp(region.Y, area.Y, Math.Max(area.Y, area.Bottom - height)), width, height),
            new PixelRect(region.X - width - gap, Math.Clamp(region.Y, area.Y, Math.Max(area.Y, area.Bottom - height)), width, height)
        };
        var fitted = choices.FirstOrDefault(candidate => candidate.Intersect(area) == candidate && candidate.Intersect(region).IsEmpty);
        if (fitted.IsEmpty) throw new InvalidOperationException("请稍微缩小手动截图选区，为选区外的完成按钮留出空间；浏览器全屏时可先退出全屏。");
        ToolbarBounds = fitted.RoundOut(); Move(window, ToolbarBounds); window.UpdateLayout();
        borderThickness = Math.Max(1, (int)Math.Round(2 * scale));
        outline = new CaptureOutline(((SolidColorBrush)Ui.Brush("AccentBrush")).Color);
        mouseHook = SetWindowsHookEx(14, mouse, GetModuleHandle(null), 0);
        keyHook = SetWindowsHookEx(13, keyboard, GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero) throw new Win32Exception();
        focus.Start();
    }
    private static void Move(Window window, PixelRect bounds)
    {
        if (!SetWindowPos(new WindowInteropHelper(window).Handle, new(-1), (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0x0050)) throw new Win32Exception();
    }
    public void RefreshSelection()
    {
        if (disposed || outline is null) return;
        if (!refresh.IsEnabled) refresh.Start();
    }
    private void DrawSelection()
    {
        if (disposed || outline is null) return;
        if (!previewVisible) { outline.Hide(); return; }
        ViewModel.RefreshSelection();
        var r = Control.VisibleSelection;
        if (r.IsEmpty) outline.Hide();
        else outline.Show(new(Region.X + r.X, Region.Y + r.Y, r.Width, r.Height), borderThickness);
    }
    // The outline is inside the browser viewport. Hide it explicitly for every native read;
    // GDI capture cannot be assumed to honor display-affinity exclusion.
    public void SetPreviewVisible(bool visible)
    {
        if (disposed || outline is null) return;
        previewVisible = visible;
        if (visible) DrawSelection(); else outline.Hide();
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (!disposed && code >= 0 && ViewModel.Ready && DesktopWindows.Foreground == browser)
        {
            int kind = message.ToInt32();
            var point = Marshal.PtrToStructure<Point>(data);
            var physical = new PixelPoint(point.X, point.Y);
            var local = new PixelPoint(point.X - Region.X, point.Y - Region.Y);
            if (kind == 0x0201 && Region.Contains(physical) && Control.Begin(local))
            { RefreshSelection(); return new(1); }
            if (kind == 0x0200 && Control.Dragging && Marshal.ReadIntPtr(data, 24).ToInt64() != 0x534E4950) { Control.Move(local); RefreshSelection(); }
            if (kind == 0x0202 && Control.Dragging)
            { Control.Release(local); RefreshSelection(); return new(1); }
            // The program owns scrolling until release; don't allow an uncontrolled wheel jump.
            if (kind == 0x020A && Region.Contains(physical) && Marshal.ReadIntPtr(data, 24).ToInt64() != 0x534E4950) return new(1);
        }
        return CallNextHookEx(mouseHook, code, message, data);
    }
    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (!disposed && code >= 0 && message.ToInt32() is 0x0100 or 0x0104)
        {
            int key = Marshal.ReadInt32(data);
            if (key == 0x1B) { cancel(); return new(1); }
            if (key == 0x0D && GetAsyncKeyState(0x11) < 0 && ViewModel.CanFinish)
            { Control.RequestFinish(); return new(1); }
        }
        return CallNextHookEx(keyHook, code, message, data);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; focus.Stop(); refresh.Stop();
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
        outline?.Dispose(); window?.Close(); Control.Dispose();
    }
}
