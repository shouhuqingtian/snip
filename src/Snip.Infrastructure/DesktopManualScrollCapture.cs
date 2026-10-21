using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Observe a fixed physical desktop rectangle. No browser API, scroll injection or cursor movement.</summary>
public sealed class DesktopManualScrollCapture(IDesktopCaptureService capture)
{
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeMethods.NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeMethods.NativeRect rect, int size);

    public async Task<BitmapSource> CaptureAsync(PixelRect region, ManualCaptureControl control, IProgress<ManualCaptureProgress> progress,
        CancellationToken token, DiagnosticOperation? diagnostics = null, Func<PixelRect, IntPtr, Task>? ready = null, Func<bool, Task>? previewVisibility = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            // Await the worker itself on cancellation; do not leave GDI reads or callbacks running after disposal.
            return await Task.Run(async () =>
            {
                if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height))
                    throw new InvalidOperationException("截图区域无效，请重新框选。");
                region = region.RoundOut();
                if (region.Width < 80 || region.Height < 160) throw new InvalidOperationException("请框选至少 80×160 像素的可滚动内容区。");
                var layout = capture.GetDisplays().ToArray();
                if (!layout.Any(d => region.Intersect(d.Bounds) == region)) throw new InvalidOperationException("桌面长截图请在同一块显示器内框选内容区。");
                var center = new PixelPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
                var target = DesktopWindows.AtPhysicalPoint(center) ?? throw new InvalidOperationException("未找到目标窗口，请重新框选软件内容区。");
                if (region.Intersect(target.Bounds) != region) throw new InvalidOperationException("请把选区框在同一个软件窗口内，避开标题栏、固定侧栏和滚动条。");
                DesktopWindows.Activate(target.Handle);
                await Task.Delay(160, lifetime.Token).ConfigureAwait(false);
                Check();
                diagnostics?.Stage("desktop-target-ready", new { target.ProcessId, region.Width, region.Height, Dpi = DesktopWindows.GetDpiForWindow(target.Handle) });
                if (ready is not null) await ready(region, target.Handle).ConfigureAwait(false);
                var source = new Source(async cancellation =>
                {
                    Check();
                    if (previewVisibility is not null) await previewVisibility(false).ConfigureAwait(false);
                    try
                    {
                        Check();
                        if (DesktopWindows.AtPhysicalPoint(center)?.Handle != target.Handle)
                            throw new InvalidOperationException("选区被其他窗口遮挡，已停止采集；请重新框选。");
                        var pixels = new FormatConvertedBitmap(capture.CaptureRegion(region), PixelFormats.Bgra32, null, 0); pixels.Freeze();
                        var bytes = new byte[checked(pixels.PixelWidth * pixels.PixelHeight * 4)];
                        pixels.CopyPixels(bytes, checked(pixels.PixelWidth * 4), 0);
                        cancellation.ThrowIfCancellationRequested(); Check();
                        return new PixelFrame(pixels.PixelWidth, pixels.PixelHeight, bytes);
                    }
                    finally { if (previewVisibility is not null) await previewVisibility(true).ConfigureAwait(false); }
                });
                var frame = await new ManualScrollCaptureSession().CaptureAsync(source, control, progress, lifetime.Token, diagnostics, desktopMode: true).ConfigureAwait(false);
                Check();
                var result = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, checked(frame.Width * 4)); result.Freeze();
                return result;

                void Check()
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (!IsWindow(target.Handle) || IsIconic(target.Handle) || DesktopWindows.Foreground != target.Handle)
                        throw new InvalidOperationException("目标窗口已关闭、最小化或切换，已停止采集。");
                    using var scope = new PhysicalDesktopScope();
                    if (DwmGetWindowAttribute(target.Handle, 9, out var bounds, Marshal.SizeOf<NativeMethods.NativeRect>()) != 0 && !GetWindowRect(target.Handle, out bounds))
                        throw new Win32Exception();
                    if (new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top) != target.Bounds || !capture.GetDisplays().SequenceEqual(layout))
                        throw new InvalidOperationException("窗口位置、尺寸或显示器布局已变化，请重新框选。");
                }
            }, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("桌面长截图已到 5 分钟时限，请分段采集。"); }
    }

    private sealed class Source(Func<CancellationToken, Task<PixelFrame>> read) : IScrollCaptureSource
    {
        public Task<PixelFrame> ReadAsync(CancellationToken token) => read(token);
        public Task<PixelFrame> ScrollAsync(int wheelUnits, CancellationToken token) => throw new NotSupportedException("桌面手动采集不会发送滚轮输入。");
    }
}
