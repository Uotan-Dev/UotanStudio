using Avalonia;
using UotanStudio.Views.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public class ScreenMirrorCoordinatesTests
{
    [Theory]
    [InlineData(800, 600, 400, 300, 800, 450)]
    [InlineData(800, 600, 0, 75, 0, 0)]
    [InlineData(1200, 450, 200, 0, 0, 0)]
    [InlineData(1200, 450, 600, 225, 800, 450)]
    [InlineData(1000, 562.5, 500, 281.25, 800, 450)]
    public void UniformStretch_MapsImageLocalCoordinates(double bw, double bh, double px, double py, int x, int y)
    {
        Assert.True(ScreenMirrorCoordinates.TryMap(new Point(px, py), new Size(bw, bh), 1600, 900, false, out int actualX, out int actualY));
        Assert.Equal((x, y), (actualX, actualY));
    }

    [Theory]
    [InlineData(-0.1, 75)]
    [InlineData(0, 74.99)]
    [InlineData(800, 300)]
    [InlineData(400, 525)]
    public void BlackBarsAndExclusiveEdges_DoNotGenerateClicks(double x, double y)
        => Assert.False(ScreenMirrorCoordinates.TryMap(new Point(x, y), new Size(800, 600), 1600, 900, false, out _, out _));

    [Fact]
    public void CapturedDragOutsideImage_ClampsToValidPixel()
    {
        Assert.True(ScreenMirrorCoordinates.TryMap(new Point(-50, 1000), new Size(800, 600), 1600, 900, true, out int x, out int y));
        Assert.Equal((0, 899), (x, y));
    }

    [Fact]
    public void PortraitFrame_MapsCenterAndRejectsSideBars()
    {
        Assert.True(ScreenMirrorCoordinates.TryMap(new Point(400, 300), new Size(800, 600), 900, 1600, false, out int x, out int y));
        Assert.Equal((450, 800), (x, y));
        Assert.False(ScreenMirrorCoordinates.TryMap(new Point(20, 300), new Size(800, 600), 900, 1600, false, out _, out _));
    }
}
