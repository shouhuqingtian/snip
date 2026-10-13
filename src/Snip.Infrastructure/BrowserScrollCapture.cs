using System;
using System.Collections.Generic;
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
    public async Task<BitmapSource> CaptureAsync(PixelRect region, IProgress<string> progress, CancellationToken token)
    {
        if (region.Width < 80 || region.Height < 160) throw new InvalidOperationException("长截图选区至少需要 80×160 像素，请选择网页正文区域。");
        var target = DesktopWindows.Snapshot().FirstOrDefault(w => w.Bounds.Contains(new(region.X + region.Width / 2, region.Y + region.Height / 2)))
            ?? throw new InvalidOperationException("未找到浏览器窗口。");
        var process = Process.GetProcessById(target.ProcessId);
        if (process.ProcessName is not "chrome" and not "msedge") throw new InvalidOperationException("当前只支持 Chrome/Edge 普通网页，请重新选择浏览器正文区域。");
        if (!SetForegroundWindow(target.Handle)) throw new InvalidOperationException("无法激活目标浏览器，请切换到浏览器后重试。");
        await Task.Delay(200, token);
        var state = await Task.Run(() => FindScroll(region), token);
        if (state is null) throw new InvalidOperationException("网页没有可用的自动滚动接口。请使用普通可滚动网页，避开地址栏、嵌套列表或开启浏览器辅助功能后重试。");
        var (pattern, originalPercent) = state.Value;
        var layout = capture.GetDisplays();
        var frames = new List<(PixelFrame Frame, int Offset)>();
        PixelFrame Grab()
        {
            if (!capture.GetDisplays().SequenceEqual(layout) || GetForegroundWindow() != target.Handle) throw new InvalidOperationException("浏览器焦点或显示器布局发生变化，长截图已停止。");
            var desktop = capture.Capture(); var r = region.Intersect(desktop.Bounds);
            if (r != region) throw new InvalidOperationException("选区超出桌面。");
            var image = new FormatConvertedBitmap(desktop.Image, PixelFormats.Bgra32, null, 0);
            var bytes = new byte[(int)r.Width * (int)r.Height * 4];
            image.CopyPixels(new System.Windows.Int32Rect((int)(r.X - desktop.Bounds.X), (int)(r.Y - desktop.Bounds.Y), (int)r.Width, (int)r.Height), bytes, (int)r.Width * 4, 0);
            return new((int)r.Width, (int)r.Height, bytes);
        }
        try
        {
            frames.Add((Grab(), 0)); int header = 0, footer = 0, total = (int)region.Height;
            var watch = Stopwatch.StartNew(); bool reachedBottom = false;
            for (int i = 0; i < 60; i++)
            {
                token.ThrowIfCancellationRequested();
                if (GetAsyncKeyState(0x1B) < 0) throw new OperationCanceledException();
                if (watch.Elapsed > TimeSpan.FromSeconds(90)) throw new InvalidOperationException("采集达到 90 秒上限，请缩小范围。");
                double before = await Task.Run(() => pattern.Current.VerticalScrollPercent, token);
                if (before >= 99.99) { reachedBottom = true; break; }
                // Small increments retain sufficient overlap and handle short selections.
                await Task.Run(() => pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement), token);
                await Task.Delay(350, token);
                var next = Grab(); double after = await Task.Run(() => pattern.Current.VerticalScrollPercent, token);
                if (after <= before + .0001)
                {
                    if (after >= 99.9) { reachedBottom = true; break; }
                    throw new InvalidOperationException("网页未继续滚动，无法可靠完成长截图。");
                }
                var match = ScrollMatching.Match(frames[^1].Frame, next);
                if (match is null) throw new InvalidOperationException("页面重叠内容不明确，无法可靠拼接；请避开动态内容、重复列表或复杂固定区域。");
                if (i == 0) { header = match.Header; footer = match.Footer; }
                total += match.Offset; if (total > 24000 || (long)total * next.Width > 60_000_000) throw new InvalidOperationException("长图达到图像大小上限，请分段截图。");
                frames.Add((next, match.Offset)); progress.Report($"已采集 {frames.Count} 帧，约 {total} 像素高；Esc 取消");
                if (after >= 99.99) { reachedBottom = true; break; }
            }
            if (!reachedBottom) throw new InvalidOperationException("达到 60 帧上限，请分段截图。");
            return Join(frames, footer);
        }
        finally
        {
            try { await Task.Run(() => pattern.SetScrollPercent(ScrollPattern.NoScroll, originalPercent)); } catch (Exception) { /* Target may have closed; do not mask the capture result. */ }
        }
    }
    private static (ScrollPattern Pattern, double Position)? FindScroll(PixelRect region)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(region.X + region.Width / 2, region.Y + region.Height / 2));
        while (element is not null)
        {
            if (element.Current.ControlType == ControlType.Document && element.TryGetCurrentPattern(ScrollPattern.Pattern, out var value))
            {
                var p = (ScrollPattern)value; var bounds = element.Current.BoundingRectangle;
                if (p.Current.VerticallyScrollable && bounds.Contains(new System.Windows.Rect(region.X, region.Y, region.Width, region.Height))) return (p, p.Current.VerticalScrollPercent);
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return null;
    }
    private static BitmapSource Join(List<(PixelFrame Frame, int Offset)> frames, int footer)
    {
        var first = frames[0].Frame; int height = first.Height + frames.Skip(1).Sum(f => f.Offset), stride = first.Width * 4;
        var output = new byte[checked(stride * height)];
        Buffer.BlockCopy(first.Bgra, 0, output, 0, (first.Height - footer) * stride);
        int row = first.Height - footer;
        foreach (var (frame, offset) in frames.Skip(1))
        { Buffer.BlockCopy(frame.Bgra, (frame.Height - footer - offset) * stride, output, row * stride, offset * stride); row += offset; }
        if (footer > 0) Buffer.BlockCopy(frames[^1].Frame.Bgra, (first.Height - footer) * stride, output, row * stride, footer * stride);
        var result = BitmapSource.Create(first.Width, height, 96, 96, PixelFormats.Bgra32, null, output, stride); result.Freeze(); return result;
    }
}
