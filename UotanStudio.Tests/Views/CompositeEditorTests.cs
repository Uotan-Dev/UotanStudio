using Avalonia;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class CompositeEditorTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task EncapsulateSelection_UndoRedoAndExpand_RestoresOriginalGraphAndLocations()
    {
        await Dispatch(() =>
        {
            using var editor = new NodeEditorViewModel();
            var first = NodeTypeRegistry.Create("Data.Value.Constant")!;
            var second = NodeTypeRegistry.Create("Data.Value.Constant")!;
            first.NodeValue = "one";
            second.NodeValue = "two";
            editor.AddNode(first);
            editor.AddNode(second);
            editor.SetSelection([first, second]);
            var originalLocation = first.Location;

            editor.EncapsulateSelection("Pair", editor.AnalyzeSelection());
            Assert.IsType<CompositeNodeViewModel>(Assert.Single(editor.Nodes));
            editor.Undo();
            Assert.Equal(new[] { first, second }, editor.Nodes);
            Assert.Equal(originalLocation, first.Location);
            editor.Redo();
            Assert.IsType<CompositeNodeViewModel>(Assert.Single(editor.Nodes));
            editor.ExpandSelectedComposite();
            Assert.Equal(2, editor.Nodes.Count);
            Assert.Equal(new[] { "one", "two" }, editor.Nodes.Select(node => node.NodeValue));
            editor.Undo();
            Assert.IsType<CompositeNodeViewModel>(Assert.Single(editor.Nodes));
        });
    }

    [Fact]
    public async Task LoadFromAsync_EmbeddedComposite_RestoresWithoutInstallingRegistryEntry()
    {
        await Dispatch(async () =>
        {
            using var source = new NodeEditorViewModel();
            var constant = NodeTypeRegistry.Create("Data.Value.Constant")!;
            constant.NodeValue = "saved";
            source.AddNode(constant);
            source.SetSelection([constant]);
            source.EncapsulateSelection("Local", source.AnalyzeSelection());
            var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos(source.Nodes) };
            Assert.False(NodeTypeRegistry.Contains(snapshot.Nodes[0].TypeKey));

            using var restored = new NodeEditorViewModel();
            await restored.LoadFromAsync(snapshot);
            var composite = Assert.IsType<CompositeNodeViewModel>(Assert.Single(restored.Nodes));
            Assert.Equal("saved", Assert.Single(composite.Package.Graph.Nodes).NodeValue);
            restored.SetSelection([composite]);
            restored.ExpandSelectedComposite();
            Assert.Equal("saved", Assert.Single(restored.Nodes).NodeValue);
        });
    }

    [Fact]
    public async Task LoadFromAsync_UnsupportedVersion_PreservesExistingCanvas()
    {
        await Dispatch(async () =>
        {
            using var editor = new NodeEditorViewModel();
            var existing = NodeTypeRegistry.Create("Data.Value.Constant")!;
            editor.AddNode(existing);
            await Assert.ThrowsAsync<InvalidDataException>(() => editor.LoadFromAsync(new WorkflowSnapshot { FormatVersion = 1 }));
            Assert.Same(existing, Assert.Single(editor.Nodes));
        });
    }

    [Fact]
    public async Task AddNode_DefaultOriginAndExplicitDrop_UsesDistinctAndExactPositions()
    {
        await Dispatch(() =>
        {
            using var editor = new NodeEditorViewModel();
            var first = new FlowNodeViewModel("first", NodeCategory.Data) { Location = default };
            var second = new FlowNodeViewModel("second", NodeCategory.Data) { Location = default };
            editor.AddNode(first);
            editor.AddNode(second);
            Assert.NotEqual(first.Location, second.Location);
            var dropped = new FlowNodeViewModel("drop", NodeCategory.Data) { Location = default };
            editor.AddNode(dropped, preserveLocation: true);
            Assert.Equal(default(Point), dropped.Location);
        });
    }

    [Fact]
    public async Task Parameters_ConnectedInput_HidesIgnoredDefaultAndShowsUpstream()
    {
        await Dispatch(() =>
        {
            using var editor = new NodeEditorViewModel();
            var source = NodeTypeRegistry.Create("Data.Value.Constant")!;
            var target = new FlowNodeViewModel("target", NodeCategory.Data);
            target.Input.Add(new NodeConnectorViewModel { Title = "Input", IsInput = true, PortType = NodePortType.Text });
            editor.AddNode(source);
            editor.AddNode(target);
            editor.SetSelection([target]);
            var input = target.Input[0];
            Assert.True(input.UsesTextEditor);
            var edge = new NodeConnectionViewModel(source.Output[0], input);
            editor.Connections.Add(edge);
            Assert.False(input.UsesTextEditor);
            Assert.Contains(source.LocalizedTitle, input.SourceDescription, StringComparison.Ordinal);
            editor.Connections.Remove(edge);
            Assert.True(input.UsesTextEditor);
        });
    }

    private Task Dispatch(Action action) => Dispatch(() => { action(); return Task.CompletedTask; });

    private async Task Dispatch(Func<Task> action)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.Session.Dispatch(action, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}