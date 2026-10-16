using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class DragScrollCaptureTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(600)]
    [InlineData(1200)]
    [InlineData(2160)]
    public async Task DragJustBelowMiddleStartsScrollingAndUpwardMovementPausesWithoutJitter(int height)
    {
        using var control = new DragCaptureControl(); control.Configure(1000, height);
        control.Begin(new(10, height * .1)); await control.WaitAsync(0, CancellationToken.None);
        control.Move(new(300, height * .5)); Assert.False(control.ScrollZoneActive);
        Assert.False(await control.WaitAsync(0, CancellationToken.None));
        control.Move(new(300, height * .62)); Assert.True(control.ScrollZoneActive);
        Assert.True(await control.WaitAsync(0, CancellationToken.None));
        control.Move(new(300, height * .58)); Assert.True(control.ScrollZoneActive);
        Assert.False(await control.WaitAsync(0, CancellationToken.None));
        control.Move(new(300, height * .54)); Assert.False(control.ScrollZoneActive);
        Assert.True(await control.WaitAsync(0, CancellationToken.None));
        control.Move(new(300, height * .58)); Assert.False(control.ScrollZoneActive);
        control.Move(new(300, height * .62)); Assert.True(control.ScrollZoneActive);
        control.Release(new(300, height * .62)); Assert.False(control.ScrollZoneActive);
    }

    [Fact]
    public void PressingInLowerHalfDoesNotScrollUntilDownwardDragAndResumeWorksAboveOriginalAnchor()
    {
        using var control = new DragCaptureControl(); control.Configure(1000, 1000);
        control.Begin(new(10, 700)); Assert.False(control.ScrollZoneActive);
        control.Move(new(300, 700)); Assert.False(control.ScrollZoneActive);
        control.Move(new(300, 710)); Assert.True(control.ScrollZoneActive);
        control.Advance(200); control.Move(new(300, 500)); Assert.False(control.ScrollZoneActive);
        control.Move(new(300, 620)); Assert.True(control.ScrollZoneActive);
    }

    [Fact]
    public async Task ReleaseCropsOnlyDraggedDocumentCoordinatesIncludingFinalInFlightScroll()
    {
        using var control = new DragCaptureControl();
        var source = new PageSource(control, true);
        var output = await new DragScrollCaptureSession().CaptureAsync(source, control, null, CancellationToken.None);
        Assert.Equal(75, output.Width); Assert.Equal(170, output.Height); Assert.Equal(1, source.Inputs);
        Assert.Equal(Crop(source.Page, 15, 50, 75, 170), output.Bgra);
    }
    [Fact]
    public async Task OneIgnoredWheelStepDoesNotPrematurelyStopEdgeScrolling()
    {
        using var control = new DragCaptureControl(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var source = new PageSource(control, true) { IgnoreFirstWheel = true };
        var output = await new DragScrollCaptureSession().CaptureAsync(source, control, null, cancel.Token);
        Assert.Equal(2, source.Inputs); Assert.Equal(Crop(source.Page, 15, 50, 75, 170), output.Bgra);
    }
    [Fact]
    public async Task StaticDragDoesNotScrollOrIncludeUnselectedMargins()
    {
        using var control = new DragCaptureControl();
        var source = new PageSource(control, false);
        var output = await new DragScrollCaptureSession().CaptureAsync(source, control, null, CancellationToken.None);
        Assert.Equal(75, output.Width); Assert.Equal(70, output.Height); Assert.Equal(0, source.Inputs);
        Assert.Equal(Crop(source.Page, 15, 50, 75, 70), output.Bgra);
    }
    [Fact]
    public void DocumentAnchorAndVisibleSelectionStayCorrectWhileScrolling()
    {
        using var control = new DragCaptureControl(); control.Configure(120, 200);
        Assert.True(control.Begin(new(15, 50))); control.Move(new(90, 199));
        control.Advance(100); Assert.Equal(new PixelRect(15, 50, 75, 249), control.Selection);
        Assert.Equal(new PixelRect(15, 0, 75, 199), control.VisibleSelection);
        control.Release(new(90, 120)); Assert.False(control.ScrollZoneActive); Assert.True(control.FinishRequested);
        Assert.Equal(new PixelRect(15, 50, 75, 170), control.Selection);
    }
    [Fact]
    public async Task WaitingForDragDoesNotPollScreensAndCanBeCancelled()
    {
        using var control = new DragCaptureControl(); using var cancel = new CancellationTokenSource();
        var source = new PageSource(control, null);
        var task = new DragScrollCaptureSession().CaptureAsync(source, control, null, cancel.Token);
        await Task.Delay(100); Assert.Equal(1, source.Reads); Assert.Equal(0, source.Inputs);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
    [Fact]
    public async Task AmbiguousMovementIsNotSilentlyStitched()
    {
        using var control = new DragCaptureControl();
        var source = new PageSource(control, true) { Corrupt = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DragScrollCaptureSession().CaptureAsync(source, control, null, CancellationToken.None));
    }
    private static byte[] Crop(byte[] page, int x, int y, int width, int height)
    {
        var bytes = new byte[width * height * 4];
        for (int row = 0; row < height; row++) Buffer.BlockCopy(page, ((row + y) * 120 + x) * 4, bytes, row * width * 4, width * 4);
        return bytes;
    }
    private sealed class PageSource(DragCaptureControl control, bool? scroll) : IScrollCaptureSource
    {
        public byte[] Page { get; } = Data();
        public bool Corrupt { get; init; }
        public bool IgnoreFirstWheel { get; init; }
        private int position;
        public int Reads { get; private set; }
        public int Inputs { get; private set; }
        public Task<PixelFrame> ReadAsync(CancellationToken token)
        {
            Reads++; control.Configure(120, 200);
            if (Reads == 1 && scroll is not null)
            {
                control.Begin(new(15, 50)); control.Move(new(90, scroll.Value ? 199 : 120));
                if (!scroll.Value) control.Release(new(90, 120));
            }
            return Task.FromResult(new PixelFrame(120, 200, Page.AsSpan(position * 120 * 4, 200 * 120 * 4).ToArray()));
        }
        public Task<PixelFrame> ScrollAsync(int units, CancellationToken token)
        {
            Inputs++;
            if (IgnoreFirstWheel && Inputs == 1) return ReadAsync(token);
            position += 100;
            control.Release(new(90, 120));
            var frame = ReadAsync(token).Result;
            if (Corrupt) Array.Fill(frame.Bgra, (byte)11);
            return Task.FromResult(frame);
        }
        private static byte[] Data() { var bytes = new byte[120 * 600 * 4]; new Random(15).NextBytes(bytes); return bytes; }
    }
}
