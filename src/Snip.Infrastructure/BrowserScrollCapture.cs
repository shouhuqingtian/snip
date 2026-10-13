using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class BrowserScrollCapture(IDesktopCaptureService capture)
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativeMethods.NativePoint point);
    private delegate bool ChildCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, ChildCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    // Include the full native union so INPUT has the correct size on both architectures.
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Wheel, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    public async Task<BitmapSource> CaptureAsync(PixelRect region, IProgress<string> progress, CancellationToken token, BrowserCaptureMode mode = BrowserCaptureMode.EntirePage)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try { return await Task.Run(() => CaptureCoreAsync(region, progress, deadline.Token, mode), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("网页采集超时，已停止；请分段截图。"); }
    }
    private async Task<BitmapSource> CaptureCoreAsync(PixelRect region, IProgress<string> progress, CancellationToken token, BrowserCaptureMode mode)
    {
        if (region.Width < 80 || region.Height < 160) throw new InvalidOperationException("长截图选区至少需要 80×160 像素，请选择网页正文区域。");
        var target = DesktopWindows.Snapshot().FirstOrDefault(w => w.Bounds.Contains(new(region.X + region.Width / 2, region.Y + region.Height / 2)))
            ?? throw new InvalidOperationException("未找到浏览器窗口。");
        using var process = Process.GetProcessById(target.ProcessId);
        if (process.ProcessName is not "chrome" and not "msedge") throw new InvalidOperationException("当前只支持 Chrome/Edge 普通网页，请重新选择浏览器正文区域。");
        SetForegroundWindow(target.Handle);
        await Task.Delay(200, token).ConfigureAwait(false);
        if (GetForegroundWindow() != target.Handle) throw new InvalidOperationException("无法激活目标浏览器，请切换到浏览器后重试。");
        if (mode is BrowserCaptureMode.EntirePage or BrowserCaptureMode.VisiblePage)
        {
            var viewport = FindNativeViewport(target, region);
            if (viewport is null)
            {
                try { viewport = await Task.Run(() => FindDocumentBounds(region), token).WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { }
            }
            region = viewport ?? throw new InvalidOperationException("无法确定浏览器正文边界。请选取正文区域，再使用‘选定区域长截图’；不要求开启浏览器辅助功能。");
        }
        if (mode is BrowserCaptureMode.VisiblePage or BrowserCaptureMode.SelectedRegion)
        {
            var visible = capture.CaptureRegion(region); visible.Freeze(); return visible;
        }
        // ScrollPattern is optional and used only for exact position restoration.
        // Default Chrome commonly exposes no ScrollPattern; wheel capture must still work.
        (ScrollPattern Pattern, double Position, double ViewportHeight)? state = null;
        try { state = await Task.Run(() => FindScroll(region), token).WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { }
        if (state is { } measured)
        {
            // Accessibility document bounds may describe the full document, not its visible
            // viewport. Use physical renderer bounds when converting percentages to pixels.
            double height = mode == BrowserCaptureMode.EntirePage ? region.Height : FindNativeViewport(target, region)?.Height ?? 0;
            state = (measured.Pattern, measured.Position, height);
        }
        var source = new WheelSource(capture, region, target.Handle, state);
        GetCursorPos(out var originalCursor);
        bool completed = false;
        try
        {
            progress.Report("正在使用浏览器滚轮自动采集…");
            var frame = await new ScrollCaptureSession().CaptureAsync(source, mode == BrowserCaptureMode.EntirePage, progress, token).ConfigureAwait(false);
            var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Width * 4); image.Freeze();
            completed = true; return image;
        }
        finally
        {
            // Never send input after the user has switched away from the browser.
            if (GetForegroundWindow() == target.Handle && state is { } saved)
            {
                try { await Task.Run(() => saved.Pattern.SetScrollPercent(ScrollPattern.NoScroll, saved.Position)).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception) { progress.Report("滚动位置未恢复，网页保留在当前采集位置。"); }
            }
            else if (completed && state is null) progress.Report("采集完成；浏览器未提供位置恢复接口，网页保留在页尾。");
            if (GetCursorPos(out var cursor) && cursor.X == source.PointerX && cursor.Y == source.PointerY) SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }
    private static PixelRect? FindNativeViewport(WindowCaptureInfo target, PixelRect selection)
    {
        PixelRect? found = null;
        ChildCallback callback = (hwnd, _) =>
        {
            var name = new System.Text.StringBuilder(128); GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "Chrome_RenderWidgetHostHWND" || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r)) return true;
            var bounds = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top).Intersect(target.Bounds).RoundOut();
            if (bounds.Width >= 80 && bounds.Height >= 160 && bounds.Contains(new(selection.X + selection.Width / 2, selection.Y + selection.Height / 2)) &&
                (found is null || bounds.Width * bounds.Height > found.Value.Width * found.Value.Height)) found = bounds;
            return true;
        };
        EnumChildWindows(target.Handle, callback, IntPtr.Zero); return found;
    }
    private static PixelRect? FindDocumentBounds(PixelRect selection)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(selection.X + selection.Width / 2, selection.Y + selection.Height / 2));
        for (int i = 0; element is not null && i < 20; i++)
        {
            if (element.Current.ControlType == ControlType.Document)
            {
                var r = element.Current.BoundingRectangle;
                if (!r.IsEmpty && r.Width >= 80 && r.Height >= 160 && double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height))
                    return new PixelRect(r.X, r.Y, r.Width, r.Height).RoundOut();
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return null;
    }
    private static (ScrollPattern Pattern, double Position, double ViewportHeight)? FindScroll(PixelRect region)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(region.X + region.Width / 2, region.Y + region.Height / 2));
        for (int i = 0; element is not null && i < 20; i++)
        {
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var value))
            {
                var p = (ScrollPattern)value;
                var bounds = element.Current.BoundingRectangle;
                if (bounds.Contains(new System.Windows.Rect(region.X, region.Y, region.Width, region.Height)) && bounds.Height > 0)
                    return (p, p.Current.VerticalScrollPercent, bounds.Height);
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return null;
    }
    private sealed class WheelSource : IScrollCaptureSource
    {
        private readonly IDesktopCaptureService capture;
        private readonly PixelRect region;
        private readonly IntPtr target;
        private readonly DisplayInfo[] layout;
        private (ScrollPattern Pattern, double Position, double ViewportHeight)? scrollState;
        public ScrollCapturePosition? Position { get; private set; }
        public int PointerX { get; }
        public int PointerY { get; }
        public WheelSource(IDesktopCaptureService capture, PixelRect region, IntPtr target, (ScrollPattern Pattern, double Position, double ViewportHeight)? scrollState)
        {
            this.scrollState = scrollState;
            this.capture = capture; this.region = region; this.target = target; layout = capture.GetDisplays().ToArray();
            PointerX = (int)(region.Right - 32); PointerY = (int)(region.Y + region.Height / 2);
        }
        private void Check(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (GetAsyncKeyState(0x1B) < 0) throw new OperationCanceledException(token);
            if (GetForegroundWindow() != target || !capture.GetDisplays().SequenceEqual(layout)) throw new InvalidOperationException("浏览器焦点或显示器布局发生变化，长截图已停止。");
        }
        public async Task<PixelFrame> ReadAsync(CancellationToken token)
        {
            Check(token);
            var image = new FormatConvertedBitmap(capture.CaptureRegion(region), PixelFormats.Bgra32, null, 0);
            var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0);
            Position = null;
            if (scrollState is { } saved)
            {
                try
                {
                    Position = await Task.Run(() =>
                    {
                        var current = saved.Pattern.Current;
                        double percent = current.VerticalScrollPercent, view = current.VerticalViewSize;
                        if (view >= 100 && !current.VerticallyScrollable) return new ScrollCapturePosition(0, true, true);
                        if (!double.IsFinite(percent) || percent < 0 || percent > 100 || !double.IsFinite(view) || view <= 0 || view > 100 || saved.ViewportHeight <= 0) return null;
                        double extent = saved.ViewportHeight * (100 / view - 1);
                        return new ScrollCapturePosition(extent * percent / 100, percent <= .001, percent >= 99.999 || extent < 1);
                    }, token).WaitAsync(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException) { scrollState = null; }
            }
            return new PixelFrame(image.PixelWidth, image.PixelHeight, bytes);
        }
        public async Task<PixelFrame> ScrollAsync(int wheelUnits, CancellationToken token)
        {
            Check(token);
            if (!SetCursorPos(PointerX, PointerY)) throw new InvalidOperationException("无法将鼠标定位到网页正文。");
            var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Wheel = unchecked((uint)-wheelUnits), Flags = 0x0800 } } };
            if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new InvalidOperationException("浏览器拒绝滚轮输入。请让浏览器和截图软件都以普通用户权限运行。");
            // Wait for smooth scrolling to settle, rather than photographing an intermediate animation.
            await Task.Delay(300, token).ConfigureAwait(false);
            var previous = await ReadAsync(token).ConfigureAwait(false);
            int stable = 0;
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                var next = await ReadAsync(token).ConfigureAwait(false);
                stable = ScrollMatching.Difference(previous, next) < .2 ? stable + 1 : 0;
                if (stable >= 2) return next;
                previous = next;
            }
            throw new InvalidOperationException("网页滚动后一直在变化，请避开视频、动画或动态内容再试。");
        }
    }
}
