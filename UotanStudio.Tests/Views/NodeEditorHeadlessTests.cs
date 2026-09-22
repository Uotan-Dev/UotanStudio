using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Nodes;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://UotanStudio.Tests/"))
        {
            Source = new Uri("avares://NodifyM.Avalonia/Styles/ControlStyles.axaml")
        });
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

[Collection("Node editor UI")]
public sealed class NodeEditorHeadlessTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task NodeEditor_ConnectionClickAndDelete_RemovesOnlyConnectionAndSupportsUndoRedo()
    {
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = fixture.Session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var node = NodeTypeRegistry.Create("Data.Constant")!;
                editor.AddNode(node);
                var connection = new NodeConnectionViewModel(
                    new NodeConnectorViewModel { Anchor = new Point(50, 50) },
                    new NodeConnectorViewModel { Anchor = new Point(200, 50) });
                editor.Connections.Add(connection);
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1200, Height = 700 };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    editor.SetSelection([node]);
                    var line = Assert.Single(view.GetVisualDescendants()
                        .OfType<NodifyM.Avalonia.Controls.Connection>(), item => item.DataContext == connection);
                    var canvas = view.FindControl<NodifyM.Avalonia.Controls.NodifyEditor>("Editor")!;
                    var hitPoint = new Point(125, 50);
                    Assert.True(NodeEditorView.IsConnectionHit(line, hitPoint, 5));
                    Assert.False(NodeEditorView.IsConnectionHit(line, hitPoint + new Vector(0, 40), 5));
                    var boundingBoxBlank = line.TranslatePoint(hitPoint + new Vector(0, 40), window)!.Value;
                    window.MouseDown(boundingBoxBlank, MouseButton.Left);
                    window.MouseUp(boundingBoxBlank, MouseButton.Left);
                    Assert.Null(editor.SelectedConnection);

                    var click = line.TranslatePoint(hitPoint, window)!.Value;
                    window.MouseDown(click, MouseButton.Left);
                    window.MouseUp(click, MouseButton.Left);

                    Assert.Same(connection, editor.SelectedConnection);
                    Assert.True(connection.IsSelected);
                    Assert.Contains("selected", line.Classes);
                    Assert.Equal(4, line.StrokeThickness);
                    Assert.False(node.IsSelected);
                    var deleteButton = view.FindControl<Button>("DeleteSelectedButton")!;
                    Assert.True(deleteButton.IsEnabled);

                    canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });

                    Assert.Empty(editor.Connections);
                    Assert.Single(editor.Nodes);
                    Assert.Null(editor.SelectedConnection);
                    Assert.False(deleteButton.IsEnabled);
                    editor.Undo();
                    Assert.Same(connection, Assert.Single(editor.Connections));
                    Assert.False(connection.IsSelected);
                    editor.SelectConnection(connection);
                    deleteButton.Command!.Execute(deleteButton.CommandParameter);
                    Assert.Empty(editor.Connections);
                    editor.Undo();
                    Assert.Same(connection, Assert.Single(editor.Connections));
                    editor.Redo();
                    Assert.Empty(editor.Connections);
                    Assert.Single(editor.Nodes);
                    editor.Undo();
                    editor.SelectConnection(connection);
                    canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                    Assert.Null(editor.SelectedConnection);
                    Assert.Single(editor.Connections);
                    editor.SelectConnection(connection);
                    var blank = canvas.TranslatePoint(new Point(300, 250), window)!.Value;
                    window.MouseDown(blank, MouseButton.Left);
                    window.MouseUp(blank, MouseButton.Left);
                    Assert.Null(editor.SelectedConnection);
                    Assert.False(connection.IsSelected);
                }
                finally
                {
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NodeEditor_ConnectionSelection_ClearsOnNodeSelectionAndExternalRemoval()
    {
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = fixture.Session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var node = NodeTypeRegistry.Create("Data.Constant")!;
                editor.AddNode(node);
                var connection = new NodeConnectionViewModel(new NodeConnectorViewModel(), new NodeConnectorViewModel());
                editor.Connections.Add(connection);
                editor.SelectConnection(connection);

                editor.SetSelection([node]);
                Assert.Null(editor.SelectedConnection);
                Assert.False(connection.IsSelected);
                Assert.True(node.IsSelected);

                editor.SelectConnection(connection);
                editor.Connections.Clear();
                Assert.Null(editor.SelectedConnection);
                Assert.False(connection.IsSelected);
                Assert.False(editor.DeleteSelectedCommand.CanExecute(null));
            }, TestContext.Current.CancellationToken);
        }
        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VisualPluginManager_RendersOutsideSettings()
    {
        var session = fixture.Session;
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = session.Dispatch(() =>
            {
                var settings = new SettingsView();
                settings.Measure(new Size(1000, 900));
                settings.Arrange(new Rect(0, 0, 1000, 900));
                Assert.Null(settings.FindControl<Button>("VisualPluginImportButton"));
                Assert.Null(settings.FindControl<ItemsControl>("VisualPluginList"));

                var manager = new VisualPluginManagerWindow();
                manager.Measure(new Size(900, 620));
                manager.Arrange(new Rect(0, 0, 900, 620));
                Assert.NotNull(manager.FindControl<TextBox>("PluginSearchBox"));
                Assert.NotNull(manager.FindControl<ListBox>("VisualPluginList"));
                manager.Close();
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NodeEditor_RendersToolboxAndCanvasInHeadlessAvalonia()
    {
        var session = fixture.Session;
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
                Assert.NotNull(view.FindControl<Border>("VisualPluginWorkspace"));
                Assert.NotNull(view.FindControl<ComboBox>("VisualPluginSelector"));
                Assert.NotNull(view.FindControl<Button>("OpenVisualPluginEditorButton"));
                Assert.True(view.Bounds.Width > 0);
                Assert.True(view.Bounds.Height > 0);
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NodeEditor_CanvasResizesAfterHostLayoutChanges()
    {
        var session = fixture.Session;
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 900, Height = 600 };
                try
                {
                    window.Show();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var canvas = view.FindControl<Control>("Editor");
                    Assert.NotNull(canvas);
                    var initialSize = canvas.Bounds.Size;

                    window.Width = 1400;
                    window.Height = 900;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();

                    Assert.True(canvas.Bounds.Width > initialSize.Width, $"Canvas width: {initialSize.Width} -> {canvas.Bounds.Width}");
                    Assert.True(canvas.Bounds.Height > initialSize.Height, $"Canvas height: {initialSize.Height} -> {canvas.Bounds.Height}");
                }
                finally
                {
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NodeEditor_SelectionTools_ExposeBatchDeleteAndSelectionBox()
    {
        var session = fixture.Session;
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
    [InlineData("Output Path", true)]
    [InlineData("Archive Path", false)]
    public void IsOutputFilePath_PortTitle_ReturnsExpectedPickerKind(string title, bool expected)
    {
        var connector = new NodeConnectorViewModel { Title = title, PortType = NodePortType.Path };

        Assert.Equal(expected, NodeEditorView.IsOutputFilePath(connector));
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
        var session = fixture.Session;
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
                Assert.NotNull(preview);
                Assert.False(preview.IsVisible);

                view.ShowNodeDropPreview(new Point(320, 240));

                Assert.True(preview.IsVisible);
                Assert.Equal(320, Canvas.GetLeft(preview));
                Assert.Equal(240, Canvas.GetTop(preview));
                Assert.Empty(preview.GetVisualDescendants().OfType<TextBlock>());

                view.ShowNodeDropPreview(new Point(480, 360));

                Assert.Equal(480, Canvas.GetLeft(preview));
                Assert.Equal(360, Canvas.GetTop(preview));

                view.HideNodeDropPreview();

                Assert.False(preview.IsVisible);
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}