using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Nodes;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class NodeEditorOptimizationTests(NodeEditorUiFixture fixture)
{
    [Theory]
    [InlineData(NodeEditorAction.Undo)]
    [InlineData(NodeEditorAction.Delete)]
    [InlineData(NodeEditorAction.Cut)]
    [InlineData(NodeEditorAction.Paste)]
    [InlineData(NodeEditorAction.ZoomIn)]
    [InlineData(NodeEditorAction.ZoomOut)]
    [InlineData(NodeEditorAction.ResetZoom)]
    [InlineData(NodeEditorAction.ToggleMinimap)]
    public Task EditorAction_TargetedMessage_LeavesOtherEditorsUnchanged(NodeEditorAction action) => OnUiAsync(() =>
    {
        using var first = new NodeEditorViewModel();
        using var second = new NodeEditorViewModel();
        var a = NodeTypeRegistry.Create("Data.Value.Constant")!;
        var b = NodeTypeRegistry.Create("Data.Value.Constant")!;
        first.AddNode(a);
        second.AddNode(b);
        first.SetSelection([a]);
        second.SetSelection([b]);
        first.ExecuteAction(NodeEditorAction.Copy);
        first.Zoom = second.Zoom = 0.8;
        var minimap = second.ShowMinimap;

        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(action, first.EditorId));

        Assert.Same(b, Assert.Single(second.Nodes));
        Assert.Same(b, second.SelectedNode);
        Assert.Equal(0.8, second.Zoom);
        Assert.Equal(minimap, second.ShowMinimap);
        Assert.True(second.CanUndo);
        Assert.False(second.CanRedo);
        if (action is NodeEditorAction.Delete or NodeEditorAction.Cut or NodeEditorAction.Undo)
        {
            Assert.Empty(first.Nodes);
        }
        else if (action == NodeEditorAction.Paste)
        {
            Assert.Equal(2, first.Nodes.Count);
        }
        else if (action == NodeEditorAction.ToggleMinimap)
        {
            Assert.NotEqual(minimap, first.ShowMinimap);
        }
        else
        {
            Assert.NotEqual(0.8, first.Zoom);
        }
    });

    [Fact]
    public Task Clipboard_MissingNodeAndDuplicatePortTitles_PreservesIdentityAndUndo() => OnUiAsync(async () =>
    {
        using var editor = new NodeEditorViewModel();
        var snapshot = Snapshot();
        snapshot.Nodes[0].CustomName = "Source alias";
        snapshot.Nodes[1].CustomName = "Unavailable alias";
        snapshot.Nodes[1].InputPorts[0].PortType = "Any";
        snapshot.Nodes[1].InputPorts.Add(new() { Id = "second", Title = "Value", PortType = "Text" });
        snapshot.Nodes[1].InputInitialValuesByPortId = new() { ["in"] = "", ["second"] = "second value" };
        await editor.LoadFromAsync(snapshot);
        var original = editor.Nodes.ToArray();
        editor.SetSelection(original);
        editor.ExecuteAction(NodeEditorAction.Copy);
        original[1].Input[1].InitialValue = "changed after copy";

        editor.ExecuteAction(NodeEditorAction.Paste);

        var pasted = editor.SelectedNodes.ToArray();
        Assert.Equal(2, pasted.Length);
        Assert.All(pasted, node => Assert.True(node.IsUnavailable));
        Assert.DoesNotContain(pasted[0].InstanceId, original.Select(node => node.InstanceId));
        Assert.Equal("Unavailable alias", pasted[1].CustomName);
        Assert.Equal("", pasted[1].Input[0].InitialValue);
        Assert.Equal("second value", pasted[1].Input[1].InitialValue);
        Assert.Equal("second", pasted[1].Input[1].StableId);
        Assert.Equal(2, editor.Connections.Count);
        Assert.Same(pasted[1].Input[0], editor.Connections[1].Target);

        editor.ExecuteAction(NodeEditorAction.Paste);
        Assert.NotEqual(pasted[0].Location, editor.SelectedNodes[0].Location);
        Assert.Equal(6, editor.Nodes.Count);
        editor.Undo();
        Assert.Equal(4, editor.Nodes.Count);
        Assert.Equal(2, editor.Connections.Count);
        editor.Redo();
        Assert.Equal(6, editor.Nodes.Count);
        Assert.Equal(3, editor.Connections.Count);
    });

    [Fact]
    public Task Delete_RemovedNodeChanges_DoNotEnterCurrentUndoHistory() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var node = NodeTypeRegistry.Create("Data.Value.Constant")!;
        editor.AddNode(node);
        editor.SetSelection([node]);
        editor.DeleteSelectedCommand.Execute(null);

        node.CustomName = "detached change";
        editor.Undo();

        Assert.Same(node, Assert.Single(editor.Nodes));
        node.CustomName = "attached change";
        editor.Undo();
        Assert.Equal("detached change", node.CustomName);
    });

    [Theory]
    [InlineData("missing-node")]
    [InlineData("missing-port")]
    [InlineData("duplicate-edge")]
    [InlineData("incompatible")]
    [InlineData("duplicate-id")]
    [InlineData("ambiguous-port")]
    [InlineData("max-connections")]
    [InlineData("cycle")]
    public Task Load_InvalidGraph_PreservesCanvasSelectionAndHistory(string invalidity) => OnUiAsync(async () =>
    {
        using var editor = new NodeEditorViewModel();
        var existing = NodeTypeRegistry.Create("Data.Value.Constant")!;
        editor.AddNode(existing);
        editor.SetSelection([existing]);
        var snapshot = Snapshot();
        switch (invalidity)
        {
            case "missing-node": snapshot.Connections[0].SourceInstanceId = "absent"; break;
            case "missing-port": snapshot.Connections[0].SourcePortId = "absent"; break;
            case "duplicate-edge": snapshot.Connections.Add(snapshot.Connections[0]); break;
            case "incompatible": snapshot.Nodes[1].InputPorts[0].PortType = "Flow"; break;
            case "duplicate-id": snapshot.Nodes[1].InstanceId = "source"; break;
            case "ambiguous-port":
                snapshot.Nodes[0].OutputPorts.Add(new() { Id = "other", Title = "Value", PortType = "Text" });
                snapshot.Connections[0].SourcePortId = null;
                break;
            case "max-connections":
                snapshot.Nodes[1].InputPorts[0].MaxConnections = 1;
                snapshot.Nodes[0].OutputPorts.Add(new() { Id = "other", Title = "Other", PortType = "Text" });
                snapshot.Connections.Add(new()
                {
                    SourceInstanceId = "source",
                    SourcePortId = "other",
                    TargetInstanceId = "target",
                    TargetPortId = "in"
                });
                break;
            case "cycle":
                snapshot.Nodes[0].InputPorts.Add(new() { Id = "in", Title = "In", PortType = "Text" });
                snapshot.Nodes[1].OutputPorts.Add(new() { Id = "out", Title = "Out", PortType = "Text" });
                snapshot.Connections.Add(new()
                {
                    SourceInstanceId = "target",
                    SourcePortId = "out",
                    TargetInstanceId = "source",
                    TargetPortId = "in"
                });
                break;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => editor.LoadFromAsync(snapshot));

        Assert.Same(existing, Assert.Single(editor.Nodes));
        Assert.Same(existing, editor.SelectedNode);
        Assert.Empty(editor.Connections);
        Assert.True(editor.CanUndo);
        editor.Undo();
        Assert.Empty(editor.Nodes);
    });

    [Fact]
    public Task Clipboard_EmbeddedComposite_FreezesDefinitionAndKeepsIndependentCopies() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var constant = NodeTypeRegistry.Create("Data.Value.Constant")!;
        constant.NodeValue = "before";
        var package = UotanStudio.Services.VisualPlugins.VisualPluginPackageBuilder.Create(
            "Composite", "tests.clipboard", VisualPluginKind.CompositeNode, [constant], []);
        var composite = new CompositeNodeViewModel(package) { CustomName = "Composite alias" };
        editor.AddNode(composite);
        editor.SetSelection([composite]);
        editor.ExecuteAction(NodeEditorAction.Copy);
        package.Graph.Nodes[0].NodeValue = "changed original";
        editor.ExecuteAction(NodeEditorAction.Paste);
        Assert.True(editor.Nodes.Count == 2, editor.ExecutionStatus);
        var first = Assert.IsType<CompositeNodeViewModel>(editor.SelectedNode);
        Assert.Equal("before", first.Package.Graph.Nodes[0].NodeValue);
        Assert.Equal("Composite alias", first.CustomName);
        first.Package.Graph.Nodes[0].NodeValue = "changed paste";

        editor.ExecuteAction(NodeEditorAction.Paste);

        var second = Assert.IsType<CompositeNodeViewModel>(editor.SelectedNode);
        Assert.Equal("before", second.Package.Graph.Nodes[0].NodeValue);
        Assert.NotSame(first.Package, second.Package);
    });

    [Fact]
    public Task Import_InvalidConnection_DoesNotAppendPartialTemplate() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var node = NodeTypeRegistry.Create("Data.Value.Constant")!;
        editor.AddNode(node);
        var graph = Snapshot();
        graph.Connections[0].SourcePortId = "invalid";
        var template = new VisualPluginPackage
        {
            Kind = VisualPluginKind.WorkflowTemplate,
            Manifest = new() { Id = "tests.invalid-template", Name = "Invalid" },
            Graph = graph
        };

        Assert.Throws<InvalidDataException>(() => editor.ImportTemplate(template));

        Assert.Same(node, Assert.Single(editor.Nodes));
        Assert.Empty(editor.Connections);
        editor.Undo();
        Assert.Empty(editor.Nodes);
    });

    [Fact]
    public Task Run_WhileWaiting_LocksEditsAndRestoresCommandsAfterCancellation() => OnUiAsync(async () =>
    {
        using var editor = new NodeEditorViewModel();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = new DelegateNode(async context =>
        {
            context.ReportProgress(0.5);
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        });
        node.Input.Add(new() { Title = "Value", InitialValue = "before" });
        node.CustomName = "before";
        editor.AddNode(node);
        var location = node.Location;
        editor.SetSelection([node]);
        var view = new NodeEditorView { DataContext = editor };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        var run = editor.RunCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(editor.IsExecuting);
            Assert.False(editor.CanEditGraph);
            Assert.False(editor.RunCommand.CanExecute(null));
            Assert.False(editor.CanUndo);
            Assert.True(editor.StopCommand.CanExecute(null));
            Assert.False(view.FindControl<NodifyM.Avalonia.Controls.NodifyEditor>("Editor")!.IsEffectivelyEnabled);
            Assert.True(view.FindControl<ProgressBar>("WorkflowProgress")!.IsVisible);
            Assert.InRange(editor.ExecutionProgress, 0.49, 0.51);

            editor.AddNode(NodeTypeRegistry.Create("Data.Value.Constant")!);
            editor.DeleteSelectedCommand.Execute(null);
            editor.Undo();
            editor.Clear();
            editor.ExecuteAction(NodeEditorAction.Paste);
            node.CustomName = "changed";
            node.Location = new(900, 900);
            node.Input[0].InitialValue = "changed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => editor.LoadFromAsync(Snapshot()));
            Assert.Throws<InvalidOperationException>(() => editor.ImportTemplate(new()));

            Assert.Same(node, Assert.Single(editor.Nodes));
            Assert.Equal("before", node.CustomName);
            Assert.Equal("before", node.Input[0].InitialValue);
            Assert.Equal(location, node.Location);
        }
        finally
        {
            editor.StopCommand.Execute(null);
            await run;
            window.Close();
        }
        Assert.False(editor.IsExecuting);
        Assert.True(editor.CanUndo);
        Assert.True(editor.RunCommand.CanExecute(null));
        node.CustomName = "after";
        Assert.Equal("after", node.CustomName);
    });

    [Fact]
    public Task Validation_FailedNode_CanFocusAndClearsStaleErrors() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var node = new DelegateNode(_ => Task.CompletedTask);
        node.Input.Add(new() { Title = "Required", IsRequired = true, PortType = NodePortType.Text });
        editor.AddNode(node);

        editor.ValidateGraphCommand.Execute(null);

        Assert.Same(node, Assert.Single(editor.FailedNodes));
        Assert.NotEmpty(node.ExecutionError);
        Assert.True(editor.IsExecutionLogExpanded);
        editor.FocusFailureCommand.Execute(node);
        Assert.Same(node, editor.SelectedNode);
        node.Input[0].InitialValue = "configured";
        editor.ValidateGraphCommand.Execute(null);
        Assert.Empty(editor.FailedNodes);
        Assert.Empty(node.ExecutionError);
        editor.Clear();
        Assert.False(editor.HasFailures);
    });

    [Fact]
    public Task CompactNodes_ConnectedAdvancedInput_KeepsAnchorAndDefaultValue() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var source = new FlowNodeViewModel("Source", NodeCategory.Data);
        source.Output.Add(new() { Title = "Value", PortType = NodePortType.Text });
        var target = new FlowNodeViewModel("Target", NodeCategory.Data);
        var advanced = new NodeConnectorViewModel
        {
            Title = "Advanced",
            PortType = NodePortType.Text,
            InitialValue = "retained",
            IsAdvanced = true
        };
        target.Input.Add(advanced);
        editor.AddNode(source);
        editor.AddNode(target);
        editor.CompactNodes = true;
        editor.SetSelection([target]);
        Assert.Empty(target.CanvasInputs);
        Assert.Empty(editor.SelectedParameterInputs);

        editor.CreateConnection(source.Output[0], advanced);

        Assert.Same(advanced, Assert.Single(target.CanvasInputs));
        Assert.Same(advanced, Assert.Single(editor.SelectedParameterInputs));
        Assert.Equal("retained", advanced.InitialValue);
        editor.Undo();
        Assert.Empty(target.CanvasInputs);
        Assert.Equal("retained", advanced.InitialValue);
        editor.CompactNodes = false;
        Assert.Same(advanced, Assert.Single(target.CanvasInputs));
    });

    [Theory]
    [InlineData(480)]
    [InlineData(640)]
    [InlineData(800)]
    [InlineData(900)]
    [InlineData(1024)]
    [InlineData(1200)]
    [InlineData(1600)]
    public Task Layout_ViewportWidths_KeepToolbarAndCanvasWithinWindow(int width) => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        editor.AddNode(NodeTypeRegistry.Create("Data.Value.Constant")!);
        var view = new NodeEditorView { DataContext = editor };
        var window = new Window { Content = view, Width = width, Height = 800 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var surface = view.FindControl<Grid>("EditorSurface")!;
            Assert.True(surface.Bounds.Width > 150);
            Assert.True(surface.Bounds.Height > 150);
            var tools = view.FindControl<StackPanel>("NarrowPaneControls")!;
            Assert.Equal(width < 900, tools.IsVisible);
            foreach (var button in view.GetVisualDescendants().OfType<Button>().Where(button =>
                         button.IsEffectivelyVisible && button.FindAncestorOfType<WrapPanel>() is not null))
            {
                var position = button.TranslatePoint(default, view)!.Value;
                Assert.InRange(position.X, 0, width);
                Assert.True(position.X + button.Bounds.Width <= width + 1,
                    $"{button.Name}: {position.X} + {button.Bounds.Width} > {width}");
            }
            if (width < 900)
            {
                var toggle = view.FindControl<ToggleButton>("PropertiesPaneToggle")!;
                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.True(view.FindControl<Border>("PropertiesPane")!.IsVisible);
                Assert.False(view.FindControl<Border>("ToolboxPane")!.IsVisible);
                var pane = view.FindControl<Border>("PropertiesPane")!;
                var paneTop = pane.TranslatePoint(default, view)!.Value.Y;
                var canvasBottom = surface.TranslatePoint(new(0, surface.Bounds.Height), view)!.Value.Y;
                Assert.True(paneTop >= canvasBottom - 1);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(1200)]
    [InlineData(1600)]
    public Task Layout_WideToolbar_SplitsControlsIntoThreeFunctionalColumns(int width) => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var view = new NodeEditorView { DataContext = editor };
        var window = new Window { Content = view, Width = width, Height = 800 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var toolbar = view.FindControl<Grid>("EditorToolbar")!;
            var editGroup = view.FindControl<WrapPanel>("ToolbarEditGroup")!;
            var runGroup = view.FindControl<WrapPanel>("ToolbarRunGroup")!;
            var viewGroup = view.FindControl<WrapPanel>("ToolbarViewGroup")!;
            var toolbarLeft = toolbar.TranslatePoint(default, view)!.Value.X;
            var toolbarRight = toolbarLeft + toolbar.Bounds.Width;
            var editLeft = editGroup.TranslatePoint(default, view)!.Value.X;
            var editRight = editLeft + editGroup.Bounds.Width;
            var runLeft = runGroup.TranslatePoint(default, view)!.Value.X;
            var runRight = runLeft + runGroup.Bounds.Width;
            var viewLeft = viewGroup.TranslatePoint(default, view)!.Value.X;
            var viewRight = viewLeft + viewGroup.Bounds.Width;
            Assert.InRange(editLeft - toolbarLeft, -1, 9);
            Assert.True(editRight <= runLeft + 1, $"edit group ends at {editRight}, run group starts at {runLeft}");
            Assert.True(runRight <= viewLeft + 1, $"run group ends at {runRight}, view group starts at {viewLeft}");
            Assert.InRange((runLeft + runRight) / 2 - (toolbarLeft + toolbarRight) / 2, -2, 2);
            Assert.InRange(toolbarRight - viewRight, -1, 9);
            var deleteButton = view.FindControl<Button>("DeleteSelectedButton")!;
            var deleteLeft = deleteButton.TranslatePoint(default, view)!.Value.X;
            Assert.InRange(toolbarRight - (deleteLeft + deleteButton.Bounds.Width), -1, 9);
            Assert.True(deleteLeft >= viewLeft, $"delete button at {deleteLeft} is left of the view group at {viewLeft}");
            Assert.True(viewLeft > editRight + width * 0.1, $"view group starts at {viewLeft}, edit ends at {editRight}");
            Assert.Equal(1, CountToolbarGroupRows(editGroup));
            Assert.Equal(1, CountToolbarGroupRows(runGroup));
            Assert.Equal(1, CountToolbarGroupRows(viewGroup));
        }
        finally
        {
            window.Close();
        }
    });

    private static int CountToolbarGroupRows(WrapPanel group)
    {
        var rows = new SortedSet<int>();
        foreach (var child in group.GetVisualChildren().OfType<Control>().Where(child => child.IsEffectivelyVisible))
        {
            rows.Add((int)Math.Round(child.TranslatePoint(default, group)!.Value.Y));
        }
        return rows.Count;
    }

    [Fact]
    public Task Navigation_DistantNodes_FitsFocusesAndInsertsInsideViewport() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var a = NodeTypeRegistry.Create("Data.Value.Constant")!;
        var b = NodeTypeRegistry.Create("Data.Value.Constant")!;
        a.Location = new(-1200, -500);
        b.Location = new(1600, 800);
        editor.AddNode(a);
        editor.AddNode(b);
        var view = new NodeEditorView { DataContext = editor };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        try
        {
            window.Show();
            window.UpdateLayout();
            view.NavigateCanvas(NodeViewportAction.FitAll);
            window.UpdateLayout();
            var surface = view.FindControl<Grid>("EditorSurface")!;
            var canvas = view.FindControl<NodifyM.Avalonia.Controls.NodifyEditor>("Editor")!;
            Assert.InRange(canvas.Zoom, 0.05, 0.5);
            foreach (var visual in canvas.GetVisualDescendants().OfType<NodifyM.Avalonia.Controls.Node>())
            {
                var origin = visual.TranslatePoint(default, surface)!.Value;
                Assert.InRange(origin.X, -1, surface.Bounds.Width);
                Assert.InRange(origin.Y, -1, surface.Bounds.Height);
                Assert.True(origin.X + visual.Bounds.Width * canvas.Zoom <= surface.Bounds.Width + 1);
            }
            editor.SetSelection([b]);
            view.NavigateCanvas(NodeViewportAction.FocusSelection);
            Assert.Equal(1, canvas.Zoom);
            var added = NodeTypeRegistry.Create("Data.Value.Constant")!;
            editor.AddNode(added);
            Assert.InRange((added.Location.X + canvas.OffsetX) * canvas.Zoom, 0, surface.Bounds.Width);
            Assert.InRange((added.Location.Y + canvas.OffsetY) * canvas.Zoom, 0, surface.Bounds.Height);
            view.NavigateCanvas(NodeViewportAction.Reset);
            Assert.Equal(0, canvas.OffsetX);
            Assert.Equal(0, canvas.ViewTranslateTransform.X);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Keyboard_CompositeEditorShortcut_OnlyChangesFocusedEditor() => OnUiAsync(() =>
    {
        using var main = new NodeEditorViewModel();
        using var composite = new NodeEditorViewModel(false);
        main.AddNode(NodeTypeRegistry.Create("Data.Value.Constant")!);
        composite.AddNode(NodeTypeRegistry.Create("Data.Value.Constant")!);
        var view = new NodeEditorView { DataContext = composite };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Z,
                KeyModifiers = KeyModifiers.Control
            });
            Assert.Empty(composite.Nodes);
            Assert.Single(main.Nodes);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Properties_LanguageAndTypeChanges_PreserveStableValuesAndBoundedCoordinates() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var node = NodeTypeRegistry.Create("Data.Value.Constant")!;
        node.Location = new(218.285714285714, -244.285714285714);
        editor.AddNode(node);
        editor.SetSelection([node]);
        var view = new NodeEditorView { DataContext = editor };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        var language = LanguageService.Instance.CurrentLanguage;
        try
        {
            window.Show();
            window.UpdateLayout();
            var selector = view.FindControl<ComboBox>("ConstantValueTypeSelector")!;
            Assert.Equal("Text", selector.SelectedValue);
            selector.SelectedItem = editor.ValueTypeChoices.Single(choice => choice.Value == "Number");
            Assert.Equal("Number", node.NodeValueType);
            Assert.Equal(NodePortType.Number, node.Output[0].PortType);
            foreach (var locale in new[] { "zh-CN", "en-US" })
            {
                LanguageService.Instance.ApplyLanguage(locale);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var expected = LanguageService.Instance.GetString("Node_PortType_Number");
                Assert.Contains(selector.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == expected);
                Assert.Equal("Number", selector.SelectedValue);
                Assert.Equal("Number", node.NodeValueType);
            }
            var location = view.FindControl<TextBlock>("NodeLocationText")!;
            Assert.DoesNotContain("285714", location.Text ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            LanguageService.Instance.ApplyLanguage(language);
            window.Close();
        }
    });

    private Task OnUiAsync(Action action) => OnUiAsync(() => { action(); return Task.CompletedTask; });

    private async Task OnUiAsync(Func<Task> action)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(action, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);
    }

    private static WorkflowSnapshot Snapshot() => new()
    {
        Nodes =
        [
            new()
            {
                InstanceId = "source", TypeKey = "Tests.Unavailable.Source",
                OutputPorts = [new() { Id = "out", Title = "Value", PortType = "Text" }]
            },
            new()
            {
                InstanceId = "target", TypeKey = "Tests.Unavailable.Target",
                InputPorts = [new() { Id = "in", Title = "Value", PortType = "Text" }]
            }
        ],
        Connections =
        [
            new()
            {
                SourceInstanceId = "source", SourcePortId = "out", SourceConnectorTitle = "Value",
                TargetInstanceId = "target", TargetPortId = "in", TargetConnectorTitle = "Value"
            }
        ]
    };

    private sealed class DelegateNode(Func<INodeExecutionContext, Task> action) : FlowNodeViewModel("Test", NodeCategory.Data)
    {
        public override Task ExecuteAsync(INodeExecutionContext context) => action(context);
    }

    [Fact]
    public Task OpenDocuments_SwitchingActiveCanvas_KeepsEachNodeSetSeparate() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        var alpha = new NodeProjectDocument("alpha");
        editor.OpenDocument(alpha);
        var node = NodeTypeRegistry.Create("Data.Value.Constant")!;
        editor.AddNode(node);

        editor.OpenDocument(new NodeProjectDocument("beta"));

        Assert.Empty(editor.Nodes);
        Assert.Same(node, Assert.Single(alpha.Nodes));
        Assert.Equal(2, editor.OpenDocuments.Count);
        Assert.Equal("beta", editor.ActiveDocumentName);
        Assert.True(editor.HasOpenNodeProjects);

        Assert.True(editor.ActivateDocument("alpha"));
        Assert.Same(node, Assert.Single(editor.Nodes));
        Assert.False(editor.ActivateDocument("missing"));
        Assert.Equal("alpha", editor.ActiveDocumentName);

        Assert.True(editor.CloseDocument("alpha"));
        Assert.Equal("beta", editor.ActiveDocumentName);
        Assert.Single(editor.OpenDocuments);
        Assert.Same(node, Assert.Single(alpha.Nodes));
    });

    [Fact]
    public Task OpenDocuments_EditorCommandHistoryFollowsEachDocument() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        editor.OpenDocument(new NodeProjectDocument("alpha"));
        editor.AddNode(NodeTypeRegistry.Create("Data.Value.Constant")!);
        Assert.True(editor.CanUndo);

        editor.OpenDocument(new NodeProjectDocument("beta"));
        Assert.False(editor.CanUndo);

        editor.ActivateDocument("alpha");
        Assert.True(editor.CanUndo);
        Assert.Single(editor.Nodes);
        Assert.Empty(editor.FindDocument("beta")!.Nodes);

        editor.Undo();
        Assert.Empty(editor.Nodes);
        Assert.True(editor.CanRedo);
        editor.ActivateDocument("beta");
        Assert.False(editor.CanRedo);
    });

    [Fact]
    public Task CloseAllDocuments_ResetsCanvasToBlankDraft() => OnUiAsync(() =>
    {
        using var editor = new NodeEditorViewModel();
        editor.OpenDocument(new NodeProjectDocument("alpha"));
        editor.AddNode(NodeTypeRegistry.Create("Data.Value.Constant")!);
        editor.OpenDocument(new NodeProjectDocument("beta"));

        editor.CloseAllDocuments();

        Assert.Empty(editor.OpenDocuments);
        Assert.Empty(editor.Nodes);
        Assert.Empty(editor.Connections);
        Assert.False(editor.HasOpenNodeProjects);
        Assert.Equal(string.Empty, editor.ActiveDocumentName);
        Assert.False(editor.CanUndo);
        Assert.Null(editor.FindDocument("alpha"));
    });
}