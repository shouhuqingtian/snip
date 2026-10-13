using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class ScrollCaptureSessionTests
{
    [Fact]
    public async Task EntirePageMovesToTopAndStitchesAllPixelsWithoutScrollPattern()
    {
        var source = new PageSource(803, 300);
        var output = await new ScrollCaptureSession().CaptureAsync(source, true, null, CancellationToken.None);
        Assert.Equal(803, output.Height); Assert.Equal(source.Page, output.Bgra);
        Assert.Contains(source.Inputs, amount => amount < 0); Assert.Contains(source.Inputs, amount => amount > 0);
    }
    [Fact]
    public async Task RegionStartsAtCurrentPosition()
    {
        var source = new PageSource(803, 180);
        var output = await new ScrollCaptureSession().CaptureAsync(source, false, null, CancellationToken.None);
        Assert.Equal(623, output.Height); Assert.Equal(source.Page.Skip(180 * 120 * 4), output.Bgra);
        Assert.DoesNotContain(source.Inputs, amount => amount < 0);
    }
    [Fact]
    public async Task NoMovementReportsFailureInsteadOfReturningOriginalScreenshot()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ScrollCaptureSession().CaptureAsync(new PageSource(200, 0), false, null, CancellationToken.None));
        Assert.Contains("未响应滚轮", error.Message);
    }
    [Fact]
    public async Task CancellationStopsBeforeNextWheelInput()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new PageSource(803, 0) { AfterScroll = () => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScrollCaptureSession().CaptureAsync(source, false, null, cancellation.Token));
        Assert.Single(source.Inputs);
    }
    [Fact]
    public async Task ChangedContentIsNotSilentlyJoined()
    {
        var source = new PageSource(803, 0) { Corrupt = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ScrollCaptureSession().CaptureAsync(source, false, null, CancellationToken.None));
    }
    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(7)]
    public async Task FinalSmallScrollStillMatches(int shift)
    {
        var source = new PageSource(803, 0); var first = await source.ReadAsync(CancellationToken.None);
        var next = await source.ScrollAsync(shift * 3, CancellationToken.None);
        Assert.Equal(shift, ScrollMatching.Match(first, next)!.Offset);
    }
    private sealed class PageSource : IScrollCaptureSource
    {
        public byte[] Page { get; }
        private int position;
        private readonly int height;
        public List<int> Inputs { get; } = [];
        public Action? AfterScroll { get; init; }
        public bool Corrupt { get; init; }
        public PageSource(int height, int position)
        {
            this.height = height; this.position = position; Page = new byte[120 * height * 4];
            new Random(8).NextBytes(Page);
            for (int i = 3; i < Page.Length; i += 4) Page[i] = 255;
        }
        public Task<PixelFrame> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new PixelFrame(120, 200, Page.AsSpan(position * 120 * 4, 200 * 120 * 4).ToArray()));
        }
        public Task<PixelFrame> ScrollAsync(int wheelUnits, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Inputs.Add(wheelUnits);
            position = Math.Clamp(position + wheelUnits / 3, 0, height - 200);
            var frame = ReadAsync(token);
            if (Corrupt) Array.Fill(frame.Result.Bgra, (byte)12);
            AfterScroll?.Invoke(); return frame;
        }
    }
}
