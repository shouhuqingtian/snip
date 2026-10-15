using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class DragCapturePerformanceTests
{
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
