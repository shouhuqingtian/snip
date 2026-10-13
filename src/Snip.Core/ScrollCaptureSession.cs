using System.Diagnostics;

namespace Snip.Core;

public enum BrowserCaptureMode { EntirePage, VisiblePage, SelectedRegion, SelectedRegionScroll }

/// <summary>Native drivers return settled frames; this loop needs no browser accessibility API.</summary>
public interface IScrollCaptureSource
{
    Task<PixelFrame> ReadAsync(CancellationToken token);
    // Positive units scroll down, negative units scroll up (120 = one wheel notch).
    Task<PixelFrame> ScrollAsync(int wheelUnits, CancellationToken token);
}

public sealed class ScrollCaptureSession
{
    public async Task<PixelFrame> CaptureAsync(IScrollCaptureSource source, bool fromTop, IProgress<string>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(90)) throw new InvalidOperationException("采集达到 90 秒上限，请分段截图。");
        }
        var first = await source.ReadAsync(token); Check();
        if (fromTop)
        {
            progress?.Report("正在自动滚动到网页顶部…");
            bool atTop = false;
            for (int i = 0; i < 15; i++)
            {
                Check(); var next = await source.ScrollAsync(-12000, token);
                if (ScrollMatching.Difference(first, next) < .2) { first = next; atTop = true; break; }
                first = next;
            }
            if (!atTop) throw new InvalidOperationException("未能在限制内滚动到网页顶部，请从当前位置分段采集。");
        }
        var frames = new List<(PixelFrame Frame, int Offset)> { (first, 0) };
        int footer = 0, total = first.Height, wheel = first.Height < 320 ? 30 : 120, unchanged = 0;
        for (int i = 0; i < 160; i++)
        {
            Check(); var next = await source.ScrollAsync(wheel, token); Check();
            if (ScrollMatching.Difference(frames[^1].Frame, next) < .2)
            {
                // Retry once to distinguish an accumulated high-resolution wheel delta from the end.
                if (++unchanged < 2) continue;
                if (frames.Count == 1) throw new InvalidOperationException("网页未响应滚轮。请选取可滚动的网页正文，避开地址栏、弹窗和嵌套滚动区域。");
                progress?.Report($"采集完成：{frames.Count} 帧，{total} 像素高。");
                return Join(frames, footer);
            }
            unchanged = 0;
            var match = ScrollMatching.Match(frames[^1].Frame, next);
            if (match is null) throw new InvalidOperationException("网页已滚动，但重叠内容无法可靠拼接；请扩大选区高度，并避开动态内容、弹窗或重复列表。");
            if (frames.Count == 1) footer = match.Footer;
            total += match.Offset;
            if (total > 24000 || (long)total * next.Width > 60_000_000) throw new InvalidOperationException("长图达到图像大小上限，请分段截图。");
            frames.Add((next, match.Offset)); progress?.Report($"正在自动滚动：已采集 {frames.Count} 帧，约 {total} 像素高；Esc 取消。");
            // Adapt to Windows wheel settings and page line height while retaining ample overlap.
            wheel = Math.Clamp((int)Math.Round(wheel * (first.Height * .35 / match.Offset)), 15, 600);
        }
        throw new InvalidOperationException("达到 160 帧上限，请分段截图。");
    }
    private static PixelFrame Join(List<(PixelFrame Frame, int Offset)> frames, int footer)
    {
        var first = frames[0].Frame; int height = first.Height + frames.Skip(1).Sum(f => f.Offset), stride = first.Width * 4;
        var output = new byte[checked(stride * height)];
        Buffer.BlockCopy(first.Bgra, 0, output, 0, (first.Height - footer) * stride);
        int row = first.Height - footer;
        foreach (var (frame, offset) in frames.Skip(1))
        { Buffer.BlockCopy(frame.Bgra, (frame.Height - footer - offset) * stride, output, row * stride, offset * stride); row += offset; }
        if (footer > 0) Buffer.BlockCopy(frames[^1].Frame.Bgra, (first.Height - footer) * stride, output, row * stride, footer * stride);
        return new(first.Width, height, output);
    }
}
