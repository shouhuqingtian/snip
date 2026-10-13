using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class ScrollMatchingTests
{
    private static PixelFrame Frame(int position, bool fixedBands = false)
    {
        const int width = 240, height = 400; var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int sourceY = y + position;
            bool fixedRow = fixedBands && (y < 30 || y >= height - 20);
            uint value = unchecked((uint)((sourceY + 1) * 747796405) ^ (uint)(x * 2891336453L));
            for (int c = 0; c < 3; c++) bytes[(y * width + x) * 4 + c] = fixedRow ? (byte)(y * 3 + c) : (byte)(value >> (c * 8));
            bytes[(y * width + x) * 4 + 3] = 255;
        }
        return new(width, height, bytes);
    }
    [Theory]
    [InlineData(20)] [InlineData(63)] [InlineData(110)] [InlineData(180)]
    public void FindsVerticalOffsetForOverlappingContent(int offset) => Assert.Equal(offset, ScrollMatching.Match(Frame(0), Frame(offset))!.Offset);
    [Fact]
    public void IdentifiesFixedHeaderAndFooter()
    {
        var match = ScrollMatching.Match(Frame(0, true), Frame(60, true));
        Assert.NotNull(match); Assert.Equal(30, match.Header); Assert.Equal(20, match.Footer); Assert.Equal(60, match.Offset);
    }
    [Fact]
    public void RejectsBlankAmbiguousContent()
    {
        var a = new PixelFrame(240, 400, new byte[240 * 400 * 4]);
        Assert.Null(ScrollMatching.Match(a, a));
    }
    [Fact]
    public void RejectsUnrelatedScreens()
    {
        var a = Frame(0); var b = Frame(1000);
        Assert.Null(ScrollMatching.Match(a, b));
    }
    [Fact]
    public void ResizeMapsPenPointsAndPreservesOriginalData()
    {
        var a = new Annotation(Guid.NewGuid(), AnnotationTool.Pen, new(10, 20), new(30, 60), "#FF0000", 4, 24, 12, "", [new(10, 20), new(30, 60)]);
        var resized = a.Resize(new(10, 20, 20, 40), new(40, 50, 40, 80));
        Assert.Equal(new PixelPoint(40, 50), resized.Points[0]); Assert.Equal(new PixelPoint(80, 130), resized.Points[1]);
        Assert.Equal(new PixelPoint(10, 20), a.Points[0]);
    }
}
