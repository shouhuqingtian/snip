using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class ManualScrollCaptureTests
{
    [Fact]
    public async Task ManualScrollingKeepsSelectedWidthAndStopsAtUserChosenPosition()
    {
        using var control = new ManualCaptureControl();
        var source = new ManualSource(control, [0, 50, 100, 150, 150]);
        var result = await new ManualScrollCaptureSession(TimeSpan.Zero).CaptureAsync(source, control, null, CancellationToken.None);
        Assert.Equal(120, result.Width); Assert.Equal(350, result.Height);
        Assert.Equal(source.Page.AsSpan(0, 350 * 120 * 4).ToArray(), result.Bgra);
        Assert.Equal(0, source.AutomaticInputs);
    }
    [Fact]
    public async Task SmoothScrollingCoalescesSmallStepsAndIncludesTheFinalPartialStep()
    {
        using var control = new ManualCaptureControl();
        var source = new ManualSource(control, [0, 10, 20, 30, 40, 50, 60, 70, 70]);
        var progress = new InlineProgress();
        var result = await new ManualScrollCaptureSession(TimeSpan.Zero).CaptureAsync(source, control, progress, CancellationToken.None);
        Assert.Equal(source.Page.AsSpan(0, 270 * 120 * 4).ToArray(), result.Bgra);
        Assert.Equal(3, progress.Last!.Frames); Assert.Equal(0, source.AutomaticInputs);
    }
    [Fact]
    public async Task FinishWithoutScrollingReturnsOnlyTheSelectedViewport()
    {
        using var control = new ManualCaptureControl(); control.RequestFinish();
        var source = new ManualSource(control, [0, 0]);
        var result = await new ManualScrollCaptureSession(TimeSpan.Zero).CaptureAsync(source, control, null, CancellationToken.None);
        Assert.Equal(200, result.Height); Assert.Equal(0, source.AutomaticInputs);
    }
    [Fact]
    public async Task LargeJumpCannotSilentlyOmitContent()
    {
        using var control = new ManualCaptureControl();
        var source = new ManualSource(control, [0, 400, 400]);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new ManualScrollCaptureSession(TimeSpan.Zero).CaptureAsync(source, control, null, CancellationToken.None));
        Assert.Contains("未可靠采集", failure.Message); Assert.Equal(0, source.AutomaticInputs);
    }
    [Fact]
    public async Task ReturningToCapturedEndRecoversAfterScrollingTooFar()
    {
        using var control = new ManualCaptureControl();
        var source = new ManualSource(control, [0, 400, 0, 50, 100, 100]);
        var result = await new ManualScrollCaptureSession(TimeSpan.Zero).CaptureAsync(source, control, null, CancellationToken.None);
        Assert.Equal(source.Page.AsSpan(0, 300 * 120 * 4).ToArray(), result.Bgra);
    }
    [Fact]
    public async Task DesktopGapPreservesOnlyAcceptedFramesAndExplicitlyReportsPartialResult()
    {
        using var control = new ManualCaptureControl();
        var source = new ManualSource(control, [0, 50, 50, 50, 50, 400, 400, 400]);
        var progress = new InlineProgress();
        var result = await new ManualScrollCaptureSession(TimeSpan.Zero).CaptureAsync(source, control, progress, CancellationToken.None, desktopMode: true);
        Assert.Equal(source.Page.AsSpan(0, 250 * 120 * 4).ToArray(), result.Bgra);
        Assert.True(progress.Last!.Partial); Assert.True(progress.Last.CanFinish);
        Assert.Contains("最后一段", progress.Last.Status); Assert.Equal(0, source.AutomaticInputs);
    }
    [Fact]
    public async Task DesktopModeWaitsForStableViewportRatherThanAppendingAnimationFrames()
    {
        using var control = new ManualCaptureControl();
        var source = new ManualSource(control, [0, 15, 30, 45, 60, 70, 70, 70, 70]);
        var progress = new InlineProgress();
        var result = await new ManualScrollCaptureSession(TimeSpan.Zero).CaptureAsync(source, control, progress, CancellationToken.None, desktopMode: true);
        Assert.Equal(source.Page.AsSpan(0, 270 * 120 * 4).ToArray(), result.Bgra);
        Assert.Equal(2, progress.Last!.Frames); Assert.False(progress.Last.Partial); Assert.Equal(0, source.AutomaticInputs);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleSessionCapturesNoAdditionalFramesAndCancellationStopsWaiting(bool desktopMode)
    {
        using var control = new ManualCaptureControl(); using var cancellation = new CancellationTokenSource();
        var source = new IdleSource();
        var task = new ManualScrollCaptureSession().CaptureAsync(source, control, null, cancellation.Token, desktopMode: desktopMode);
        await Task.Delay(150); Assert.Equal(1, source.Reads);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
    [Fact]
    public void IncrementalStitchingRetainsBodyAndOneViewportRatherThanEveryFullFrame()
    {
        byte[] page = new byte[120 * 1400 * 4]; new Random(8).NextBytes(page);
        PixelFrame Frame(int y) => new(120, 200, page.AsSpan(y * 120 * 4, 200 * 120 * 4).ToArray());
        using var frames = new ScrollFrameAccumulator(Frame(0));
        for (int y = 10; y <= 1000; y += 10) frames.Append(Frame(y), new(10, 0, 0, 0));
        Assert.Equal(101, frames.FrameCount);
        Assert.Equal(page.AsSpan(0, 1200 * 120 * 4).ToArray(), frames.Finish().Bgra);
        Assert.True(frames.RetainedBytes <= 2L * 1200 * 120 * 4 + 200 * 120 * 4);
    }
    private sealed class ManualSource(ManualCaptureControl control, int[] positions) : IScrollCaptureSource
    {
        public byte[] Page { get; } = CreatePage();
        private int reads;
        public int AutomaticInputs { get; private set; }
        public Task<PixelFrame> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int y = positions[Math.Min(reads, positions.Length - 1)]; reads++;
            if (reads == 1) control.RequestFrame();
            if (reads >= positions.Length - 1) control.RequestFinish();
            return Task.FromResult(new PixelFrame(120, 200, Page.AsSpan(y * 120 * 4, 200 * 120 * 4).ToArray()));
        }
        public Task<PixelFrame> ScrollAsync(int units, CancellationToken token)
        { AutomaticInputs++; throw new InvalidOperationException("Manual capture must not inject input."); }
        private static byte[] CreatePage() { var page = new byte[120 * 800 * 4]; new Random(8).NextBytes(page); return page; }
    }
    private sealed class IdleSource : IScrollCaptureSource
    {
        public int Reads { get; private set; }
        public Task<PixelFrame> ReadAsync(CancellationToken token)
        { Reads++; return Task.FromResult(new PixelFrame(120, 200, new byte[120 * 200 * 4])); }
        public Task<PixelFrame> ScrollAsync(int units, CancellationToken token) => throw new InvalidOperationException("Unexpected automatic wheel input.");
    }
    private sealed class InlineProgress : IProgress<ManualCaptureProgress>
    {
        public ManualCaptureProgress? Last { get; private set; }
        public void Report(ManualCaptureProgress value) => Last = value;
    }
}
