using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class SparseScrollTests
{
    [Theory]
    [InlineData(73)] [InlineData(161)] [InlineData(257)]
    public void FindsSparseArticleWithLargeWhiteMarginsAndFixedToolbar(int offset)
    {
        var first = Article(0); var next = Article(offset);
        Assert.Equal(offset, ScrollMatching.Match(first, next)!.Offset);
    }
    [Fact]
    public void SparseTextMovementIsNotReportedAsUnchanged()
    {
        var first = Article(0, false); var next = Article(73, false);
        Assert.False(ScrollMatching.Unchanged(first, next));
    }
    [Fact]
    public async Task MeasuredPositionPreservesACompletelyWhiteGap()
    {
        var source = new MeasuredBlankPage(2031);
        var result = await new ScrollCaptureSession().CaptureAsync(source, true, null, CancellationToken.None);
        Assert.Equal(2031, result.Height); Assert.All(result.Bgra, b => Assert.Equal((byte)255, b));
    }
    [Fact]
    public async Task ShortWholePageReturnsOneFrame()
    {
        var source = new MeasuredBlankPage(200);
        var result = await new ScrollCaptureSession().CaptureAsync(source, true, null, CancellationToken.None);
        Assert.Equal(200, result.Height);
    }
    public static PixelFrame Article(int position, bool toolbar = true)
    {
        const int width = 1200, height = 600;
        var data = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (int y = 0; y < height; y++)
        {
            int documentY = y + position;
            // Thin, unique "glyphs" surrounded by large white image/article areas.
            if (documentY % 160 is < 24)
            {
                var random = new Random(documentY / 160 * 31 + documentY % 160);
                for (int x = 160; x < 440; x++)
                    if (random.Next(3) == 0)
                        for (int c = 0; c < 3; c++) data[(y * width + x) * 4 + c] = (byte)(32 + c * 4);
            }
        }
        if (toolbar)
        {
            for (int y = 520; y < 565; y++) for (int x = 440; x < 800; x++)
                for (int c = 0; c < 3; c++) data[(y * width + x) * 4 + c] = (byte)((x % 27 < 5 && y % 18 < 7) ? 230 : 75);
        }
        return new(width, height, data);
    }
    private sealed class MeasuredBlankPage(int height) : IScrollCaptureSource
    {
        private int position;
        public ScrollCapturePosition? Position => new(position, position == 0, position == height - 200);
        public Task<PixelFrame> ReadAsync(CancellationToken token) => Task.FromResult(new PixelFrame(120, 200, Enumerable.Repeat((byte)255, 120 * 200 * 4).ToArray()));
        public Task<PixelFrame> ScrollAsync(int amount, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); position = Math.Clamp(position + amount / 3, 0, height - 200); return ReadAsync(token);
        }
    }
}
