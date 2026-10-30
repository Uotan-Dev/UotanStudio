using Avalonia;
using Avalonia.Input;
using UotanStudio.Views.Nodes;
using UotanStudio.Workflow.Nodes;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Unit tests for mouse wheel scrolling and zooming interactions on the node editor canvas.
/// <para>节点编辑器画布上鼠标滚轮滚动与缩放交互的单元测试。</para>
/// </summary>
public sealed class NodeEditorCanvasGestureTests
{
    [Theory]
    [InlineData(0, 40, 1, 1)]
    [InlineData(99, 40, 1, 1)]
    [InlineData(40, 0, 1, 1)]
    [InlineData(40, 99, 1, 1)]
    public void IntersectsSelection_AnyPartialNodeOverlap_IsSelected(
        double selectionX, double selectionY, double selectionWidth, double selectionHeight)
    {
        var selection = new Rect(selectionX, selectionY, selectionWidth, selectionHeight);
        var nodeBounds = new Rect(0, 0, 100, 100);

        Assert.True(NodeEditorView.IntersectsSelection(selection, nodeBounds));
    }

    [Fact]
    public void IntersectsSelection_OutsideNodeBounds_IsNotSelected()
    {
        var selection = new Rect(102, 40, 10, 10);
        var nodeBounds = new Rect(0, 0, 100, 100);

        Assert.False(NodeEditorView.IntersectsSelection(selection, nodeBounds));
    }

    [Fact]
    public void ToggleNodeSelection_ShiftClickSelectedNode_RemovesIt()
    {
        var first = new FlowNodeViewModel("first", NodeCategory.Data);
        var second = new FlowNodeViewModel("second", NodeCategory.Data);

        var selected = NodeEditorView.ToggleNodeSelection([first, second], first);

        Assert.DoesNotContain(first, selected);
        Assert.Contains(second, selected);
    }

    [Fact]
    public void ToggleNodeSelection_ShiftClickUnselectedNode_AddsIt()
    {
        var first = new FlowNodeViewModel("first", NodeCategory.Data);
        var second = new FlowNodeViewModel("second", NodeCategory.Data);

        var selected = NodeEditorView.ToggleNodeSelection([first], second);

        Assert.Contains(first, selected);
        Assert.Contains(second, selected);
    }

    [Fact]
    public void CalculateWheelAction_VerticalWheel_ScrollsVerticallyWithoutZoom()
    {
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
        NodeEditorView.CalculateWheelAction(
            new Vector(0, 1),
            KeyModifiers.None,
            zoom: 2.0,
            out _,
            out _,
            out var deltaZoomedIn);
        Assert.Equal(30.0, deltaZoomedIn);

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