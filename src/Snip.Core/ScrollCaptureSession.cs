using System.Diagnostics;

namespace Snip.Core;

public enum BrowserCaptureMode { EntirePage, FromCurrentPage, ManualScroll }

/// <summary>Native drivers return settled frames; this loop needs no browser accessibility API.</summary>
public interface IScrollCaptureSource
{
    ScrollCapturePosition? Position => null;
    Task<PixelFrame> ReadAsync(CancellationToken token);
    // Positive units scroll down, negative units scroll up (120 = one wheel notch).
    Task<PixelFrame> ScrollAsync(int wheelUnits, CancellationToken token);
}
public sealed record ScrollCapturePosition(double Offset, bool AtTop, bool AtBottom);

public sealed class ScrollCaptureSession
{
    public async Task<PixelFrame> CaptureAsync(IScrollCaptureSource source, bool fromTop, IProgress<string>? progress, CancellationToken token, DiagnosticOperation? diagnostics = null, bool allowSingleFrame = false)
    {
        var watch = Stopwatch.StartNew();
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(90)) throw new InvalidOperationException("采集达到 90 秒上限，请分段截图。");
        }
        var first = await source.ReadAsync(token); Check();
        diagnostics?.Stage("first-frame", new { first.Width, first.Height, FromTop = fromTop });
        if (fromTop)
        {
            progress?.Report("正在自动滚动到网页顶部…");
            bool atTop = false;
            for (int i = 0; i < 15; i++)
            {
                Check(); var next = await source.ScrollAsync(-12000, token);
                if (source.Position?.AtTop == true || ScrollMatching.Unchanged(first, next)) { first = next; atTop = true; break; }
                first = next;
            }
            diagnostics?.Stage("scroll-to-top", new { AtTop = atTop });
            if (!atTop) throw new InvalidOperationException("未能在限制内滚动到网页顶部，请从当前位置分段采集。");
        }
        using var frames = new ScrollFrameAccumulator(first);
        int wheel = first.Height < 320 ? 30 : 120, unchanged = 0;
        bool positionReliable = true;
        for (int i = 0; i < 160; i++)
        {
            Check(); var before = source.Position; var next = await source.ScrollAsync(wheel, token); Check();
            var after = source.Position;
            int? knownOffset = positionReliable && before is not null && after is not null ? (int)Math.Round(after.Offset - before.Offset) : null;
            diagnostics?.Stage("wheel-settled", new { Frame = i, WheelUnits = wheel, KnownOffset = knownOffset, after?.AtBottom });
            if (knownOffset == 0 && !ScrollMatching.Unchanged(frames.Last, next)) { positionReliable = false; knownOffset = null; }
            if (knownOffset == 0 || knownOffset is null && ScrollMatching.Unchanged(frames.Last, next))
            {
                // Retry once to distinguish an accumulated high-resolution wheel delta from the end.
                if (++unchanged < 2) continue;
                if (frames.FrameCount == 1 && !fromTop && !allowSingleFrame && after?.AtBottom != true) throw new InvalidOperationException("网页未响应滚轮。请选取可滚动的网页正文，避开地址栏、弹窗和嵌套滚动区域。");
                progress?.Report($"采集完成：{frames.FrameCount} 帧，{frames.Height} 像素高。");
                return frames.Finish();
            }
            unchanged = 0;
            var match = ScrollMatching.Match(frames.Last, next);
            diagnostics?.Stage("overlap-match", new { Frame = i, Matched = match is not null, match?.Offset, match?.Error, match?.Header, match?.Footer, KnownOffset = knownOffset });
            if (match is not null && knownOffset is { } measured && Math.Abs(match.Offset - measured) > 2) { positionReliable = false; knownOffset = null; }
            if (match is null && knownOffset is > 0 && knownOffset < first.Height * .8)
            {
                var bands = ScrollMatching.FixedBands(frames.Last, next);
                match = new(knownOffset.Value, bands.Header, bands.Footer, 0);
                diagnostics?.Stage("position-fallback", new { match.Offset, match.Header, match.Footer });
            }
            if (match is null) throw new InvalidOperationException("网页已滚动，但重叠内容无法可靠拼接；请扩大选区高度，并避开动态内容、弹窗或重复列表。");
            frames.Append(next, match); progress?.Report($"正在自动滚动：已采集 {frames.FrameCount} 帧，约 {frames.Height} 像素高；Esc 取消。");
            if (positionReliable && after?.AtBottom == true) return frames.Finish();
            // Adapt to Windows wheel settings and page line height while retaining ample overlap.
            wheel = Math.Clamp((int)Math.Round(wheel * (first.Height * .35 / match.Offset)), 15, 600);
        }
        throw new InvalidOperationException("达到 160 帧上限，请分段截图。");
    }
}
