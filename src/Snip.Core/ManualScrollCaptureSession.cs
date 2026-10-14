using System.Diagnostics;

namespace Snip.Core;

public sealed record ManualCaptureProgress(int Frames, int Height, string Status, bool CanFinish);

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
        CancellationToken token, DiagnosticOperation? diagnostics = null)
    {
        var first = await source.ReadAsync(token).ConfigureAwait(false);
        using var frames = new ScrollFrameAccumulator(first);
        var position = source.Position; bool positionReliable = true, gap = false;
        void Report(string? warning = null) => progress?.Report(new(frames.FrameCount, frames.Height,
            warning ?? $"已采集 {frames.FrameCount} 帧，{frames.Height} 像素 · 滚轮向下滚动，完成 Ctrl+Enter", !gap));
        Report(); diagnostics?.Stage("manual-ready", new { first.Width, first.Height });
        async Task<PixelFrame> Observe()
        {
            token.ThrowIfCancellationRequested();
            var frame = await source.ReadAsync(token).ConfigureAwait(false);
            var current = source.Position;
            if (ScrollMatching.Unchanged(frames.Last, frame)) { gap = false; Report(); return frame; }
            int? offset = positionReliable && position is not null && current is not null ? (int)Math.Round(current.Offset - position.Offset) : null;
            if (offset < 0 || offset is null && ScrollMatching.Match(frame, frames.Last) is not null)
            { gap = true; Report("已向上滚动；请回到已采集末尾再向下滚动，避免重复内容。"); return frame; }
            if (offset == 0) { positionReliable = false; offset = null; }
            var match = ScrollMatching.Match(frames.Last, frame);
            if (match is not null && offset is { } measured && Math.Abs(match.Offset - measured) > 2)
            { positionReliable = false; offset = null; }
            if (match is null && offset is > 0 && offset < first.Height * .8)
            {
                var bands = ScrollMatching.FixedBands(frames.Last, frame);
                match = new(offset.Value, bands.Header, bands.Footer, 0);
            }
            if (match is null)
            {
                gap = true; Report("滚动过快或内容难以对齐；请向上回到已采集末尾，再慢慢向下滚动。");
                diagnostics?.Stage("manual-overlap-rejected", new { Frames = frames.FrameCount, KnownOffset = offset }); return frame;
            }
            if (!control.FinishRequested && current?.AtBottom != true && match.Offset < Math.Max(24, first.Height / 4))
            { gap = false; Report(); return frame; } // Coalesce smooth-scroll microsteps; include remainder when finishing.
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
                await Task.Delay(sampleDelay ?? TimeSpan.FromMilliseconds(80), token).ConfigureAwait(false);
                var observed = await Observe().ConfigureAwait(false);
                settled = ScrollMatching.Difference(previous, observed) < .2 ? settled + 1 : 0; previous = observed;
                if (settled >= 2 && !control.InputRecent) break;
            }
        }
        // Finish includes the final viewport after the last wheel animation settles.
        PixelFrame? final = null; int stable = 0;
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(sampleDelay ?? TimeSpan.FromMilliseconds(80), token).ConfigureAwait(false);
            var observed = await Observe().ConfigureAwait(false);
            stable = final is not null && ScrollMatching.Difference(final, observed) < .2 ? stable + 1 : 0; final = observed;
            if (stable >= 2) break;
        }
        token.ThrowIfCancellationRequested();
        if (stable < 2) throw new InvalidOperationException("网页仍在滚动或内容变化，请停止滚动后重新完成截图。");
        if (gap) throw new InvalidOperationException("最后一段未可靠采集；请减慢滚动，保留更多重叠区域后重新截图。");
        return frames.Finish();
    }
}
