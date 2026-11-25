using System.Windows;
using PeakModeTray.Controls;
using Xunit;

namespace PeakModeTray.Tests;

public sealed class CutCornersTests
{
    [Fact]
    public void TopRightCutRemovesOnlyThatCorner()
    {
        var g = CutCorners.Geometry(200, 100, topRight: 14, bottomLeft: 0);
        Assert.False(g.FillContains(new Point(199, 1)));    // inside the cut
        Assert.True(g.FillContains(new Point(185, 1)));     // just left of it
        Assert.True(g.FillContains(new Point(1, 99)));      // bottom left untouched
        Assert.True(g.FillContains(new Point(199, 99)));
        Assert.Equal(new Rect(0, 0, 200, 100), g.Bounds);
    }

    [Fact]
    public void BothCuts()
    {
        var g = CutCorners.Geometry(300, 200, topRight: 28, bottomLeft: 28);
        Assert.False(g.FillContains(new Point(299, 1)));
        Assert.False(g.FillContains(new Point(1, 199)));
        Assert.True(g.FillContains(new Point(150, 100)));
    }

    [Fact]
    public void CutNeverExceedsHalfTheShortSide()
    {
        var g = CutCorners.Geometry(20, 10, topRight: 50, bottomLeft: 0);
        Assert.True(g.FillContains(new Point(10, 9)));      // cut clamped to 5
        Assert.False(g.FillContains(new Point(19.5, 0.5)));
    }
}
