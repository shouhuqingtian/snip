using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class DragCapturePerformanceTests
{
    [Theory]
    [InlineData(200, .5)]
    [InlineData(600, .5)]
    [InlineData(1200, 1)]
    [InlineData(600, 2)]
    public async Task SustainedEdgeDragCapturesSeveralViewportsWithFewerInputsAndExactPixels(int height, double pixelsPerUnit)
    {
        using var control = new DragCaptureControl();
        var source = new LongPage(control, height, pixelsPerUnit);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new DragScrollCaptureSession().CaptureAsync(source, control, null, cancel.Token);
        Assert.InRange(source.Inputs, 2, 9);
        Assert.Equal(110, result.Width);
        Assert.Equal(source.Offset + height / 2 - 20, result.Height);
        for (int y = 0; y < result.Height; y++)
            Assert.True(result.Bgra.AsSpan(y * result.Width * 4, result.Width * 4)
                .SequenceEqual(source.Page.AsSpan(((y + 20) * LongPage.Width + 10) * 4, result.Width * 4)));
    }

    private sealed class LongPage : IScrollCaptureSource
    {
        public const int Width = 160;
        private readonly DragCaptureControl control;
        private readonly int height;
        private readonly double pixelsPerUnit;
        public byte[] Page { get; }
        public int Inputs { get; private set; }
        public int Offset { get; private set; }
        public LongPage(DragCaptureControl control, int height, double pixelsPerUnit)
        {
            this.control = control; this.height = height; this.pixelsPerUnit = pixelsPerUnit;
            Page = new byte[Width * height * 8 * 4]; new Random(173).NextBytes(Page);
        }
        public Task<PixelFrame> ReadAsync(CancellationToken token)
        {
            if (!control.Started)
            { control.Configure(Width, height); control.Begin(new(10, 20)); control.Move(new(120, height - 1)); }
            return Task.FromResult(new PixelFrame(Width, height, Page.AsSpan(Offset * Width * 4, height * Width * 4).ToArray()));
        }
        public Task<PixelFrame> ScrollAsync(int wheelUnits, CancellationToken token)
        {
            Inputs++; Offset += (int)Math.Round(wheelUnits * pixelsPerUnit);
            if (Offset >= height * 3) control.Release(new(120, height / 2));
            return ReadAsync(token);
        }
    }

    [Fact]
    public async Task OrdinaryPointerMotionDoesNotWakeTheCaptureLoopButEdgesAndReleaseDo()
    {
        using var control = new DragCaptureControl(); control.Configure(1000, 600);
        Assert.True(control.Begin(new(10, 10))); Assert.True(await control.WaitAsync(0, CancellationToken.None));
        for (int i = 0; i < 1000; i++) control.Move(new(30 + i % 300, 50 + i % 200));
        Assert.False(await control.WaitAsync(0, CancellationToken.None));
        Assert.Equal(new PixelRect(10, 10, 129 - 10, 249 - 10), control.Selection);
        control.Move(new(350, 590)); Assert.True(await control.WaitAsync(0, CancellationToken.None));
        control.Move(new(360, 595)); Assert.False(await control.WaitAsync(0, CancellationToken.None));
        control.Move(new(360, 300)); Assert.True(await control.WaitAsync(0, CancellationToken.None));
        control.Release(new(360, 310)); Assert.True(await control.WaitAsync(0, CancellationToken.None));
        Assert.True(control.FinishRequested);
    }

    [Fact]
    public void DirectSelectionExportMatchesWholeImageAcrossRepairedBodyAndFixedFooter()
    {
        const int width = 180, height = 200;
        PixelFrame Frame(int seed) { var bytes = new byte[width * height * 4]; new Random(seed).NextBytes(bytes); return new(width, height, bytes); }
        using var frames = new ScrollFrameAccumulator(Frame(1));
        frames.Append(Frame(2), new(60, 12, 15, 0)); frames.Append(Frame(3), new(50, 12, 15, 0));
        var whole = frames.Finish();
        foreach (var region in new[] { new PixelRect(7, 5, 90, 302), new(10, 292, 70, 18), new(0, 0, 180, 310) })
        {
            var cropped = frames.Finish(region);
            for (int row = 0; row < cropped.Height; row++)
                Assert.True(whole.Bgra.AsSpan(((int)region.Y + row) * width * 4 + (int)region.X * 4, cropped.Width * 4).SequenceEqual(cropped.Bgra.AsSpan(row * cropped.Width * 4, cropped.Width * 4)));
        }
    }

    [Fact]
    public void Narrow4KSelectionDoesNotAllocateAnotherWholeViewport()
    {
        using var frames = new ScrollFrameAccumulator(new(3840, 2160, new byte[3840 * 2160 * 4]));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var selected = frames.Finish(new(1000, 400, 320, 1700));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(320 * 1700 * 4, selected.Bgra.Length);
        Assert.InRange(allocated, selected.Bgra.Length, selected.Bgra.Length + 65536L);
    }
}
