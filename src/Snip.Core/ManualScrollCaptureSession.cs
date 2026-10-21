using System.Diagnostics;

namespace Snip.Core;

public sealed record ManualCaptureProgress(int Frames, int Height, string Status, bool CanFinish, bool Partial = false);

/// <summary>Only a wake-up signal is retained; wheel activity never accumulates an unbounded queue.</summary>
public sealed class ManualCaptureControl : IDisposable
{
    private readonly SemaphoreSlim input = new(0, 1);
    private long lastInput;
    private int finish, disposed;
    public bool FinishRequested => Volatile.Read(ref finish) != 0;
    public bool InputRecent => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastInput)) < TimeSpan.FromMilliseconds(350);
    public void RequestFrame()
    {
        Interlocked.Exchange(ref lastInput, Stopwatch.GetTimestamp()); Wake();
    }
    public void RequestFinish() { Interlocked.Exchange(ref finish, 1); Wake(); }
    private void Wake()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { if (input.CurrentCount == 0) input.Release(); }
        catch (Exception error) when (error is SemaphoreFullException or ObjectDisposedException) { }
    }
    public Task WaitAsync(CancellationToken token) => input.WaitAsync(token);
    public void Dispose() { Interlocked.Exchange(ref disposed, 1); input.Dispose(); }
}

/// <summary>Observes user-controlled scrolling. Never sends wheel input or changes the cursor.</summary>
public sealed class ManualScrollCaptureSession(TimeSpan? sampleDelay = null)
{
    public async Task<PixelFrame> CaptureAsync(IScrollCaptureSource source, ManualCaptureControl control, IProgress<ManualCaptureProgress>? progress,
        CancellationToken token, DiagnosticOperation? diagnostics = null, bool desktopMode = false)
    {
        var first = await source.ReadAsync(token).ConfigureAwait(false);
        using var frames = new ScrollFrameAccumulator(first);
        var position = source.Position; bool positionReliable = true, gap = false;
        void Report(string? warning = null) => progress?.Report(new(frames.FrameCount, frames.Height,
            warning ?? $"已采集 {frames.FrameCount} 帧，{frames.Height} 像素 · 滚轮向下滚动，完成 Ctrl+Enter", !gap || desktopMode, gap && desktopMode));
        Report(); diagnostics?.Stage("manual-ready", new { first.Width, first.Height });
        async Task<PixelFrame> Observe(PixelFrame? observed = null, bool moving = false)
        {
            token.ThrowIfCancellationRequested();
            var frame = observed ?? await source.ReadAsync(token).ConfigureAwait(false);
            var current = source.Position;
            if (ScrollMatching.Unchanged(frames.Last, frame)) { gap = false; Report(); return frame; }
            int? offset = positionReliable && position is not null && current is not null ? (int)Math.Round(current.Offset - position.Offset) : null;
            var match = desktopMode ? DesktopScrollMatching.Match(frames.Last, frame, token, diagnostics) : null;
            // A rendered frame can be safely joined during scrolling when its moving pixels
            // align confidently. Transient animation failures must not publish a pause warning.
            if (desktopMode && moving && (match is null || match.Offset < 0 || match.Error > 4)) return frame;
            if (offset < 0 || desktopMode && match?.Offset < 0 || !desktopMode && offset is null && ScrollMatching.Match(frame, frames.Last) is not null)
            { gap = true; Report(desktopMode ? "已暂停拼接：请回到已采集末尾；也可保存已采集部分。" : "已向上滚动；请回到已采集末尾再向下滚动，避免重复内容。"); return frame; }
            if (offset == 0) { positionReliable = false; offset = null; }
            if (!desktopMode) match = ScrollMatching.Match(frames.Last, frame);
            if (match is not null && offset is { } measured && Math.Abs(match.Offset - measured) > 2)
            { positionReliable = false; offset = null; }
            if (match is null && offset is > 0 && offset < first.Height * .8)
            {
                var bands = ScrollMatching.FixedBands(frames.Last, frame);
                match = new(offset.Value, bands.Header, bands.Footer, 0);
            }
            if (match is null)
            {
                gap = true; Report(desktopMode ? "对齐暂停：向上回到已采集末尾再缓慢下滚，或保存已采集部分。" : "滚动过快或内容难以对齐；请向上回到已采集末尾，再慢慢向下滚动。");
                diagnostics?.Stage("manual-overlap-rejected", new { Frames = frames.FrameCount, KnownOffset = offset }); return frame;
            }
            int minimumStep = desktopMode ? Math.Max(8, Math.Min(32, (first.Height - match.Header - match.Footer) / 6)) : Math.Max(24, first.Height / 4);
            if (!control.FinishRequested && current?.AtBottom != true && match.Offset < minimumStep)
            { gap = false; Report(); return frame; } // Coalesce smooth-scroll microsteps; include remainder when finishing.
            if (desktopMode && (frames.FrameCount >= 160 || frames.Height + match.Offset > 24000 || (long)(frames.Height + match.Offset) * first.Width > 60_000_000))
            {
                gap = true; Report("已到采集大小上限，正在保留已采集部分；其余内容请分段截图。");
                control.RequestFinish(); return frame;
            }
            frames.Append(frame, match); position = current; gap = false; Report();
            diagnostics?.Stage("manual-frame-appended", new { Frames = frames.FrameCount, frames.Height, match.Offset, match.Header, match.Footer });
            return frame;
        }
        while (!control.FinishRequested)
        {
            await control.WaitAsync(token).ConfigureAwait(false);
            if (control.FinishRequested) break;
            var previous = frames.Last; int settled = 0;
            for (int i = 0; i < 30 && !control.FinishRequested; i++)
            {
                await Task.Delay(sampleDelay ?? TimeSpan.FromMilliseconds(desktopMode && control.InputRecent ? 40 : 80), token).ConfigureAwait(false);
                var observed = desktopMode ? await source.ReadAsync(token).ConfigureAwait(false) : await Observe().ConfigureAwait(false);
                settled = ScrollMatching.Difference(previous, observed) < .2 ? settled + 1 : 0; previous = observed;
                if (desktopMode) await Observe(observed, moving: settled < 2).ConfigureAwait(false);
                if (settled >= 2 && !control.InputRecent) break;
            }
        }
        // Finish includes the final viewport after the last wheel animation settles.
        PixelFrame? final = null; int stable = 0;
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(sampleDelay ?? TimeSpan.FromMilliseconds(80), token).ConfigureAwait(false);
            var observed = desktopMode ? await source.ReadAsync(token).ConfigureAwait(false) : await Observe().ConfigureAwait(false);
            stable = final is not null && ScrollMatching.Difference(final, observed) < .2 ? stable + 1 : 0; final = observed;
            if (stable >= 2) { if (desktopMode) await Observe(observed).ConfigureAwait(false); break; }
        }
        token.ThrowIfCancellationRequested();
        if (desktopMode && (stable < 2 || gap))
        {
            gap = true; Report("已保存可靠采集的部分；最后一段未能对齐，没有加入长图。");
            diagnostics?.Stage("manual-partial-completed", new { Frames = frames.FrameCount, frames.Height, Stable = stable >= 2 });
            return frames.Finish();
        }
        if (stable < 2) throw new InvalidOperationException("页面仍在滚动或内容变化，请停止滚动后重新完成截图。");
        if (gap) throw new InvalidOperationException("最后一段未可靠采集；请减慢滚动，保留更多重叠区域后重新截图。");
        return frames.Finish();
    }
}
