using Avalonia;
using UotanStudio.Services.Ui.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WindowPlacementValidatorTests
{
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);
    private static readonly PixelRect Secondary = new(1920, 0, 1280, 1000);

    [Fact]
    public void HasVisibleArea_WindowInsideWorkingArea_ReturnsTrue()
        => Assert.True(WindowPlacementValidator.HasVisibleArea(100, 80, 1280, 800, [Primary], 1));

    [Fact]
    public void HasVisibleArea_WindowPartlyOffScreen_KeepsUsablePart()
        => Assert.True(WindowPlacementValidator.HasVisibleArea(-1200, 900, 1280, 800, [Primary], 1));

    [Fact]
    public void HasVisibleArea_WindowOnSecondaryScreen_ReturnsTrue()
        => Assert.True(WindowPlacementValidator.HasVisibleArea(2200, 100, 1280, 800, [Primary, Secondary], 1));

    [Fact]
    public void HasVisibleArea_WindowBeyondEveryScreen_ReturnsFalse()
        => Assert.False(WindowPlacementValidator.HasVisibleArea(2400, 1400, 1280, 800, [Primary, Secondary], 1));

    [Fact]
    public void HasVisibleArea_SliverBelowMinimumExtent_ReturnsFalse()
        => Assert.False(WindowPlacementValidator.HasVisibleArea(1890, 100, 1280, 800, [Primary], 1));

    [Fact]
    public void HasVisibleArea_NoScreens_ReportsTrue()
        => Assert.True(WindowPlacementValidator.HasVisibleArea(100000, 100000, 1280, 800, [], 1));

    [Fact]
    public void HasVisibleArea_UnmeasurableSize_ReportsTrue()
        => Assert.True(WindowPlacementValidator.HasVisibleArea(100, 100, double.NaN, 800, [Primary], 1));
}