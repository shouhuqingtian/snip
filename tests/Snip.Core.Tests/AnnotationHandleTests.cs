using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class AnnotationHandleTests
{
    [Theory]
    [InlineData(10, 20, SelectionHandle.TopLeft)]
    [InlineData(110, 20, SelectionHandle.Top)]
    [InlineData(210, 20, SelectionHandle.TopRight)]
    [InlineData(10, 120, SelectionHandle.Left)]
    [InlineData(210, 120, SelectionHandle.Right)]
    [InlineData(10, 220, SelectionHandle.BottomLeft)]
    [InlineData(110, 220, SelectionHandle.Bottom)]
    [InlineData(210, 220, SelectionHandle.BottomRight)]
    [InlineData(10, 70, SelectionHandle.New)]
    [InlineData(60, 20, SelectionHandle.New)]
    [InlineData(110, 120, SelectionHandle.New)]
    public void OnlyVisiblePointsResize(double x, double y, SelectionHandle expected) =>
        Assert.Equal(expected, SelectionGeometry.ControlPointHitTest(new(10, 20, 200, 200), new(x, y), 6));

    [Theory]
    [InlineData(.1)]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void HandleToleranceFollowsDisplayScale(double scale)
    {
        var bounds = new PixelRect(100, 100, 1000, 1000);
        Assert.Equal(SelectionHandle.TopLeft, SelectionGeometry.ControlPointHitTest(bounds, new(100 - 5 / scale, 100), 6 / scale));
        Assert.Equal(SelectionHandle.New, SelectionGeometry.ControlPointHitTest(bounds, new(100 - 7 / scale, 100), 6 / scale));
        var tiny = new PixelRect(10, 10, 8, 8);
        Assert.Equal(SelectionHandle.BottomRight, SelectionGeometry.ControlPointHitTest(tiny, new(18, 18), 6));
    }
}
