namespace Snip.Core;

/// <summary>Physical viewport coordinates mapped into the document captured from the initial viewport.</summary>
public sealed class DragCaptureControl : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim input = new(0, 1);
    private PixelPoint start, pointer;
    private bool started, dragging, finished, disposed;
    private int width, height, offset;
    public void Configure(int viewportWidth, int viewportHeight)
    { lock (gate) { width = viewportWidth; height = viewportHeight; } }
    public bool Started { get { lock (gate) return started; } }
    public bool Dragging { get { lock (gate) return dragging; } }
    public bool FinishRequested { get { lock (gate) return finished; } }
    public bool AtLowerEdge { get { lock (gate) return dragging && pointer.Y >= height - 24; } }
    public int ScrollOffset { get { lock (gate) return offset; } }
    public PixelRect Selection { get { lock (gate) return started ? PixelRect.FromPoints(start, new(pointer.X, pointer.Y + offset)).RoundOut() : default; } }
    public PixelRect VisibleSelection { get { lock (gate) return started ? PixelRect.FromPoints(new(start.X, start.Y - offset), pointer).Intersect(new(0, 0, width, height)) : default; } }
    private PixelPoint Clamp(PixelPoint point) => new(Math.Clamp(point.X, 0, width), Math.Clamp(point.Y, 0, height));
    public bool Begin(PixelPoint point)
    {
        lock (gate)
        {
            if (started || finished || disposed || width <= 0 || height <= 0) return false;
            pointer = Clamp(point); start = new(pointer.X, pointer.Y + offset); started = dragging = true;
        }
        Wake(); return true;
    }
    public void Move(PixelPoint point) { lock (gate) { if (!dragging) return; pointer = Clamp(point); } Wake(); }
    public void Release(PixelPoint point) { lock (gate) { if (!dragging) return; pointer = Clamp(point); dragging = false; finished = true; } Wake(); }
    public void RequestFinish() { lock (gate) { if (!started) return; dragging = false; finished = true; } Wake(); }
    public void Advance(int pixels) { lock (gate) offset = checked(offset + pixels); }
    private void Wake()
    {
        try { if (!disposed && input.CurrentCount == 0) input.Release(); }
        catch (Exception e) when (e is ObjectDisposedException or SemaphoreFullException) { }
    }
    public Task<bool> WaitAsync(int milliseconds, CancellationToken token) => input.WaitAsync(milliseconds, token);
    public void Dispose() { lock (gate) disposed = true; input.Dispose(); }
}

/// <summary>Scroll only while dragging at the lower edge. Each injected step settles before it is stitched.</summary>
public sealed class DragScrollCaptureSession
{
    public async Task<PixelFrame> CaptureAsync(IScrollCaptureSource source, DragCaptureControl control,
        IProgress<ManualCaptureProgress>? progress, CancellationToken token, DiagnosticOperation? diagnostics = null)
    {
        var initial = await source.ReadAsync(token).ConfigureAwait(false);
        control.Configure(initial.Width, initial.Height);
        using var frames = new ScrollFrameAccumulator(initial);
        int viewportHeight = initial.Height;
        bool bottom = false, positionReliable = true;
        void Report() => progress?.Report(new(frames.FrameCount, frames.Height,
            control.Started ? $"已选 {control.Selection.Width:0}×{control.Selection.Height:0} 像素 · 松开鼠标完成" : "按住左键拖选 · 拖到网页下沿自动滚动 · Esc 取消", control.Started && !control.Selection.IsEmpty));
        Report(); diagnostics?.Stage("drag-ready", new { initial.Width, initial.Height });
        while (!control.FinishRequested)
        {
            token.ThrowIfCancellationRequested();
            if (!control.AtLowerEdge || bottom)
            { await control.WaitAsync(Timeout.Infinite, token).ConfigureAwait(false); Report(); continue; }
            // Small controlled steps avoid losing overlap or photographing smooth-scroll intermediates.
            var before = source.Position;
            diagnostics?.Stage("drag-step", new { before?.Offset, before?.AtBottom, control.ScrollOffset, control.Dragging, control.AtLowerEdge });
            var next = await source.ScrollAsync(viewportHeight < 320 ? 30 : 120, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var after = source.Position;
            int? measured = positionReliable && before is not null && after is not null ? (int)Math.Round(after.Offset - before.Offset) : null;
            bool unchanged = ScrollMatching.Unchanged(frames.Last, next);
            diagnostics?.Stage("drag-step-settled", new { Measured = measured, after?.AtBottom, Unchanged = unchanged });
            if (measured == 0 && !unchanged) { positionReliable = false; measured = null; }
            if (measured == 0 || measured is null && unchanged) { bottom = true; Report(); continue; }
            var match = ScrollMatching.Match(frames.Last, next);
            if (match is not null && measured is { } known && Math.Abs(match.Offset - known) > 2)
            { positionReliable = false; measured = null; }
            if (match is null && measured is > 0 && measured < viewportHeight * .8)
            {
                var bands = ScrollMatching.FixedBands(frames.Last, next);
                match = new(measured.Value, bands.Header, bands.Footer, 0);
            }
            if (match is null) throw new InvalidOperationException("网页滚动后无法确认内容位置，已停止选区截图；请避开动画或嵌套滚动区域后重试。");
            frames.Append(next, match); control.Advance(match.Offset);
            bottom = positionReliable && after?.AtBottom == true; Report();
            diagnostics?.Stage("drag-scroll-appended", new { frames.FrameCount, frames.Height, match.Offset, match.Error });
        }
        token.ThrowIfCancellationRequested();
        var selected = control.Selection.Intersect(new(0, 0, initial.Width, frames.Height)).RoundOut();
        if (selected.IsEmpty) throw new InvalidOperationException("选区太小，请按住左键拖出区域再松开。");
        var whole = frames.Finish();
        int width = (int)selected.Width, height = (int)selected.Height, stride = width * 4;
        var bytes = new byte[checked(stride * height)];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(whole.Bgra, ((int)selected.Y + row) * whole.Width * 4 + (int)selected.X * 4, bytes, row * stride, stride);
        diagnostics?.Stage("drag-selection-cropped", new { width, height });
        return new(width, height, bytes);
    }
}
