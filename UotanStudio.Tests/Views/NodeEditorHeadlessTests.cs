using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Nodes;
using Xunit;

namespace UotanStudio.Tests.Views;

public sealed class HeadlessTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class NodeEditorHeadlessTests
{
    [Fact]
    public async Task NodeEditor_RendersToolboxAndCanvasInHeadlessAvalonia()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var view = new NodeEditorView { DataContext = editor };
                view.Measure(new Size(1200, 700));
                view.Arrange(new Rect(0, 0, 1200, 700));

                Assert.NotEmpty(editor.ToolboxCategories);
                Assert.NotEmpty(editor.ToolboxCategories.SelectMany(category => category.Items));
                Assert.True(view.Bounds.Width > 0);
                Assert.True(view.Bounds.Height > 0);
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NodeEditor_SelectionTools_ExposeBatchDeleteAndSelectionBox()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var view = new NodeEditorView { DataContext = editor };
                view.Measure(new Size(1200, 700));
                view.Arrange(new Rect(0, 0, 1200, 700));

                var editorSurface = view.FindControl<Grid>("EditorSurface");
                var selectionBox = view.FindControl<Border>("SelectionBox");
                var deleteButton = view.FindControl<Button>("DeleteSelectedButton");
                var deleteLabel = view.FindControl<TextBlock>("DeleteSelectedLabel");
                Assert.NotNull(editorSurface);
                Assert.NotNull(selectionBox);
                Assert.NotNull(deleteButton);
                Assert.NotNull(deleteLabel);
                Assert.False(selectionBox.IsVisible);
                Assert.False(deleteButton.IsEnabled);

                var first = NodeTypeRegistry.Create("Data.Constant")!;
                var second = NodeTypeRegistry.Create("Data.Constant")!;
                editor.AddNode(first);
                editor.AddNode(second);
                editor.SetSelection([first, second]);

                Assert.True(deleteButton.IsEnabled);
                Assert.Equal(editor.DeleteSelectionText, deleteLabel.Text);
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(640, 360, 1, 1, 639, 359)]
    [InlineData(500, 250, -120, 80, 620, 170)]
    public void ToNodeLocation_RemovesViewportOffsetWithoutApplyingZoomAgain(
        double editorX,
        double editorY,
        double offsetX,
        double offsetY,
        double expectedX,
        double expectedY)
    {
        var location = NodeEditorView.ToNodeLocation(new Point(editorX, editorY), offsetX, offsetY);

        Assert.Equal(new Point(expectedX, expectedY), location);
    }

    [Theory]
    [InlineData(10, 20, 50, 80, 10, 20, 40, 60)]
    [InlineData(50, 20, 10, 80, 10, 20, 40, 60)]
    [InlineData(10, 80, 50, 20, 10, 20, 40, 60)]
    [InlineData(50, 80, 10, 20, 10, 20, 40, 60)]
    public void CreateSelectionRectangle_AnyDragDirection_ReturnsNonNegativeBounds(
        double firstX,
        double firstY,
        double secondX,
        double secondY,
        double expectedX,
        double expectedY,
        double expectedWidth,
        double expectedHeight)
    {
        var rectangle = NodeEditorView.CreateSelectionRectangle(
            new Point(firstX, firstY),
            new Point(secondX, secondY));

        Assert.Equal(new Rect(expectedX, expectedY, expectedWidth, expectedHeight), rectangle);
    }

    [Fact]
    public async Task NodeDropPreview_ShowMoveAndHide_TracksCanvasPosition()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var view = new NodeEditorView { DataContext = editor };
                view.Measure(new Size(1200, 700));
                view.Arrange(new Rect(0, 0, 1200, 700));

                var preview = view.FindControl<Border>("NodeDropPreview");
                var title = view.FindControl<TextBlock>("NodeDropPreviewTitle");
                Assert.NotNull(preview);
                Assert.NotNull(title);
                Assert.False(preview.IsVisible);

                view.ShowNodeDropPreview(new Point(320, 240), "Unpack firmware");

                Assert.True(preview.IsVisible);
                Assert.Equal(320, Canvas.GetLeft(preview));
                Assert.Equal(240, Canvas.GetTop(preview));
                Assert.Equal("Unpack firmware", title.Text);

                view.ShowNodeDropPreview(new Point(480, 360), "Pack firmware");

                Assert.Equal(480, Canvas.GetLeft(preview));
                Assert.Equal(360, Canvas.GetTop(preview));
                Assert.Equal("Pack firmware", title.Text);

                view.HideNodeDropPreview();

                Assert.False(preview.IsVisible);
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}