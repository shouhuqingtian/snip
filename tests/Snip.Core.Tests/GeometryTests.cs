using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class GeometryTests
{
    private static readonly PixelRect Desktop = new(-1920, -1080, 4480, 2520);

    [Fact]
    public void CrossScreenDragPreservesNegativeCoordinatesAndPhysicalSize()
    {
        var result = SelectionGeometry.Drag(default, SelectionHandle.New, new(-300, -200), new(400, 250), Desktop);
        Assert.Equal(new PixelRect(-300, -200, 700, 450), result);
    }
    [Fact]
    public void DragOutsideDesktopClampsToCaptureBounds()
    {
        var result = SelectionGeometry.Drag(default, SelectionHandle.New, new(-100, 100), new(99999, 99999), Desktop);
        Assert.Equal(new PixelRect(-100, 100, 2660, 1340), result);
    }
    [Fact]
    public void MovingClampsOriginWithoutChangingSize()
    {
        var original = new PixelRect(-100, 50, 200, 100);
        var result = SelectionGeometry.Drag(original, SelectionHandle.Move, new(0, 100), new(9999, -9999), Desktop);
        Assert.Equal(new PixelRect(2360, -1080, 200, 100), result);
    }
    [Theory]
    [InlineData(SelectionHandle.TopLeft, -100, -50, -100, -50, 250, 200)]
    [InlineData(SelectionHandle.BottomRight, 250, 300, 50, 50, 200, 250)]
    [InlineData(SelectionHandle.Left, 200, 100, 150, 50, 50, 100)]
    [InlineData(SelectionHandle.Top, 100, 200, 50, 150, 100, 50)]
    [InlineData(SelectionHandle.Right, 30, 100, 30, 50, 20, 100)]
    [InlineData(SelectionHandle.Bottom, 100, 20, 50, 20, 100, 30)]
    public void ResizeAllowsCrossingOppositeEdge(SelectionHandle handle, double x, double y, double rx, double ry, double width, double height)
    {
        var result = SelectionGeometry.Drag(new(50, 50, 100, 100), handle, new(50, 50), new(x, y), Desktop);
        Assert.Equal(new PixelRect(rx, ry, width, height), result);
    }
    [Theory]
    [InlineData(50, 50, SelectionHandle.TopLeft)]
    [InlineData(150, 150, SelectionHandle.BottomRight)]
    [InlineData(100, 100, SelectionHandle.Move)]
    [InlineData(150, 100, SelectionHandle.Right)]
    [InlineData(100, 50, SelectionHandle.Top)]
    [InlineData(300, 300, SelectionHandle.New)]
    [InlineData(50, 300, SelectionHandle.New)]
    public void HitTestDistinguishesCornersEdgesInteriorAndOutside(double x, double y, SelectionHandle expected) =>
        Assert.Equal(expected, SelectionGeometry.HitTest(new(50, 50, 100, 100), new(x, y)));

    [Fact]
    public void RoundingIncludesEverySelectedPixel() => Assert.Equal(new PixelRect(-3, 4, 5, 7), new PixelRect(-2.4, 4.2, 3.7, 6.1).RoundOut());

    [Fact]
    public void IntersectionOfDisjointRegionsIsEmpty() => Assert.True(new PixelRect(-20, 0, 10, 10).Intersect(new(0, 0, 20, 20)).IsEmpty);

    [Fact]
    public void AnnotationTranslationMovesPenPointsAndPreservesStyle()
    {
        var pen = new Annotation(Guid.NewGuid(), AnnotationTool.Pen, new(1, 2), new(5, 6), "#FF0000", 4, 24, 12, "", [new(1, 2), new(5, 6)]);
        var moved = pen.Translate(new(-10, 20));
        Assert.Equal(new PixelPoint(-9, 22), moved.Start);
        Assert.Equal(new PixelPoint(-5, 26), moved.Points[1]);
        Assert.Equal(pen.Color, moved.Color);
        Assert.Equal(new PixelPoint(5, 6), pen.Points[1]);
    }
}
