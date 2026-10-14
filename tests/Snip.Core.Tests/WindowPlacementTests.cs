using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class WindowPlacementTests
{
    [Theory]
    [InlineData(3840,2120,1.75,0,0)]
    [InlineData(3840,2120,2,0,0)]
    [InlineData(1920,1160,1,-1920,0)]
    [InlineData(1920,1160,1.5,3840,-1200)]
    [InlineData(1920,1040,1.75,-1920,-1080)]
    [InlineData(1366,728,1.5,0,0)]
    public void OversizedResultFitsEachMonitorWithoutLosingCaption(int width,int height,double scale,int x,int y)
    {
        var area=new PixelRect(x,y,width,height);
        var result=WindowPlacement.Fit(new(x-300,y-400,1140*scale,760*scale),area,scale,true);
        Assert.Equal(result,result.Intersect(area));
        Assert.True(result.Y>=area.Y && result.Height<=area.Height);
        Assert.True(result.Width<=1140*scale && result.Height<=760*scale);
    }
    [Fact]
    public void MoveFrom4KLaptopTo1200pExternalClampsActualPixels()
    {
        var external=new PixelRect(-1920,0,1920,1160);
        var oversized=new PixelRect(-2100,-260,1995,1330);
        var result=WindowPlacement.Fit(oversized,external,1);
        Assert.Equal(new PixelRect(-1912,8,1904,1144),result);
    }
    [Fact]
    public void VisibleNormalSizeKeepsUserPosition()
    {
        var current=new PixelRect(-1700,80,1100,740);
        Assert.Equal(current,WindowPlacement.Fit(current,new(-1920,0,1920,1160),1));
    }
    [Fact]
    public void DisconnectedExternalWindowReturnsToRemainingMonitor()
    {
        var result=WindowPlacement.Fit(new(-1800,-900,1140,760),new(0,0,3840,2120),1.75);
        Assert.Equal(14,result.X);Assert.Equal(14,result.Y);
    }
}
