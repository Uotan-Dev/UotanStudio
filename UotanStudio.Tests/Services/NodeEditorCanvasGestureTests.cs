using Avalonia;
using Avalonia.Input;
using UotanStudio.Views.Nodes;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Unit tests for mouse wheel scrolling and zooming interactions on the node editor canvas.
/// <para>节点编辑器画布上鼠标滚轮滚动与缩放交互的单元测试。</para>
/// </summary>
public sealed class NodeEditorCanvasGestureTests
{
    [Fact]
    public void CalculateWheelAction_VerticalWheel_ScrollsVerticallyWithoutZoom()
    {
        // Wheel up: viewport scrolls up (canvas shifts down, deltaOffsetY > 0)
        var resultUp = NodeEditorView.CalculateWheelAction(
            new Vector(0, 1),
            KeyModifiers.None,
            zoom: 1.0,
            out var isZoomUp,
            out var deltaXUp,
            out var deltaYUp);

        Assert.True(resultUp);
        Assert.False(isZoomUp);
        Assert.Equal(0, deltaXUp);
        Assert.Equal(60, deltaYUp);

        // Wheel down: viewport scrolls down (canvas shifts up, deltaOffsetY < 0)
        var resultDown = NodeEditorView.CalculateWheelAction(
            new Vector(0, -1),
            KeyModifiers.None,
            zoom: 1.0,
            out var isZoomDown,
            out var deltaXDown,
            out var deltaYDown);

        Assert.True(resultDown);
        Assert.False(isZoomDown);
        Assert.Equal(0, deltaXDown);
        Assert.Equal(-60, deltaYDown);
    }

    [Fact]
    public void CalculateWheelAction_ShiftVerticalWheel_ScrollsHorizontallyWithoutZoom()
    {
        // Shift + wheel up: viewport scrolls left (canvas shifts right, deltaOffsetX > 0)
        var resultUp = NodeEditorView.CalculateWheelAction(
            new Vector(0, 1),
            KeyModifiers.Shift,
            zoom: 1.0,
            out var isZoomUp,
            out var deltaXUp,
            out var deltaYUp);

        Assert.True(resultUp);
        Assert.False(isZoomUp);
        Assert.Equal(60, deltaXUp);
        Assert.Equal(0, deltaYUp);

        // Shift + wheel down: viewport scrolls right (canvas shifts left, deltaOffsetX < 0)
        var resultDown = NodeEditorView.CalculateWheelAction(
            new Vector(0, -1),
            KeyModifiers.Shift,
            zoom: 1.0,
            out var isZoomDown,
            out var deltaXDown,
            out var deltaYDown);

        Assert.True(resultDown);
        Assert.False(isZoomDown);
        Assert.Equal(-60, deltaXDown);
        Assert.Equal(0, deltaYDown);
    }

    [Fact]
    public void CalculateWheelAction_HorizontalWheel_ScrollsHorizontallyWithoutZoom()
    {
        // Horizontal tilt right / scroll right: Delta.X is negative, viewport scrolls right
        var resultRight = NodeEditorView.CalculateWheelAction(
            new Vector(-1, 0),
            KeyModifiers.None,
            zoom: 1.0,
            out var isZoomRight,
            out var deltaXRight,
            out var deltaYRight);

        Assert.True(resultRight);
        Assert.False(isZoomRight);
        Assert.Equal(-60, deltaXRight);
        Assert.Equal(0, deltaYRight);

        // Horizontal tilt left / scroll left: Delta.X is positive, viewport scrolls left
        var resultLeft = NodeEditorView.CalculateWheelAction(
            new Vector(1, 0),
            KeyModifiers.None,
            zoom: 1.0,
            out var isZoomLeft,
            out var deltaXLeft,
            out var deltaYLeft);

        Assert.True(resultLeft);
        Assert.False(isZoomLeft);
        Assert.Equal(60, deltaXLeft);
        Assert.Equal(0, deltaYLeft);
    }

    [Fact]
    public void CalculateWheelAction_ControlVerticalWheel_TreatsAsZoom()
    {
        // Ctrl + wheel up: zoom in
        var resultUp = NodeEditorView.CalculateWheelAction(
            new Vector(0, 1),
            KeyModifiers.Control,
            zoom: 1.0,
            out var isZoomUp,
            out var deltaXUp,
            out var deltaYUp);

        Assert.True(resultUp);
        Assert.True(isZoomUp);
        Assert.Equal(0, deltaXUp);
        Assert.Equal(0, deltaYUp);

        // Ctrl + wheel down: zoom out
        var resultDown = NodeEditorView.CalculateWheelAction(
            new Vector(0, -1),
            KeyModifiers.Control,
            zoom: 1.0,
            out var isZoomDown,
            out var deltaXDown,
            out var deltaYDown);

        Assert.True(resultDown);
        Assert.True(isZoomDown);
        Assert.Equal(0, deltaXDown);
        Assert.Equal(0, deltaYDown);
    }

    [Fact]
    public void CalculateWheelAction_ControlHorizontalWheel_DoesNotZoom()
    {
        var result = NodeEditorView.CalculateWheelAction(
            new Vector(1, 0),
            KeyModifiers.Control,
            zoom: 1.0,
            out var isZoom,
            out var deltaX,
            out var deltaY);

        Assert.False(result);
        Assert.False(isZoom);
        Assert.Equal(0, deltaX);
        Assert.Equal(0, deltaY);
    }

    [Fact]
    public void CalculateWheelAction_ZoomScaling_AdjustsCanvasDeltaInverselyToZoom()
    {
        // At zoom = 2.0 (zoomed in): 60 screen pixels = 30 canvas units
        NodeEditorView.CalculateWheelAction(
            new Vector(0, 1),
            KeyModifiers.None,
            zoom: 2.0,
            out _,
            out _,
            out var deltaZoomedIn);
        Assert.Equal(30.0, deltaZoomedIn);

        // At zoom = 0.5 (zoomed out): 60 screen pixels = 120 canvas units
        NodeEditorView.CalculateWheelAction(
            new Vector(0, 1),
            KeyModifiers.None,
            zoom: 0.5,
            out _,
            out _,
            out var deltaZoomedOut);
        Assert.Equal(120.0, deltaZoomedOut);
    }

    [Fact]
    public void CalculateWheelAction_ZeroDelta_ReturnsFalse()
    {
        var result = NodeEditorView.CalculateWheelAction(
            new Vector(0, 0),
            KeyModifiers.None,
            zoom: 1.0,
            out var isZoom,
            out var deltaX,
            out var deltaY);

        Assert.False(result);
        Assert.False(isZoom);
        Assert.Equal(0, deltaX);
        Assert.Equal(0, deltaY);
    }
}