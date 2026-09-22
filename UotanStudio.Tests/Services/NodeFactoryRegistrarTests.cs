using System.Collections.Concurrent;
using Avalonia;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NodeFactoryRegistrarTests
{
    [Fact]
    public async Task Register_ConcurrentFactories_PreservesCreationAndSnapshotAfterRemoval()
    {
        var prefix = "tests.concurrent." + Guid.NewGuid().ToString("N") + ".";
        var token = TestContext.Current.CancellationToken;
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var index = 0; index < 64; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var key = prefix + worker + "." + index;
                    NodeTypeRegistry.Register(key, () => PatchNodeViewModel.Create("Patch.CheckFormat"));
                    Assert.True(NodeTypeRegistry.Contains(key));
                    Assert.IsType<PatchNodeViewModel>(NodeTypeRegistry.Create(key));
                    var snapshot = NodeTypeRegistry.Keys;
                    Assert.True(NodeTypeRegistry.Unregister(key));
                    Assert.Contains(key, snapshot);
                    Assert.False(NodeTypeRegistry.Contains(key));
                }
            }, token)));
        }
        finally
        {
            foreach (var key in NodeTypeRegistry.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                NodeTypeRegistry.Unregister(key);
            }
        }
    }

    [Fact]
    public void Descriptors_HaveUniqueKeysAndReconstructableFactories()
    {
        NodeFactoryRegistrar.RegisterAll();

        var descriptors = NodeFactoryRegistrar.Descriptors;
        Assert.Equal(descriptors.Count, descriptors.Select(d => d.TypeKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var descriptor in descriptors)
        {
            Assert.True(NodeTypeRegistry.Contains(descriptor.TypeKey));
            var node = NodeTypeRegistry.Create(descriptor.TypeKey);
            Assert.NotNull(node);
            Assert.Equal(descriptor.TypeKey, node.TypeKey);
        }
    }

    [Fact]
    public void Descriptors_DoNotContainRemovedHdcOrDexNodes()
    {
        var keys = NodeFactoryRegistrar.Descriptors.Select(d => d.TypeKey).ToArray();

        Assert.DoesNotContain(keys, key => key.Contains("Hdc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, key => key.Contains("Dex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Descriptors_ExposeExecutionCapabilities()
    {
        var flash = Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "Flash.Flash");
        Assert.True(flash.IsDestructive);
        Assert.True(flash.Capabilities.HasFlag(NodeCapability.RequiresDevice));
        Assert.True(flash.Capabilities.HasFlag(NodeCapability.MutatesDevice));

        var branch = Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "Flow.Branch");
        Assert.True(branch.Capabilities.HasFlag(NodeCapability.ControlFlow));

        var download = Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "Network.Download");
        Assert.True(download.Capabilities.HasFlag(NodeCapability.RequiresNetwork));
        Assert.True(download.Capabilities.HasFlag(NodeCapability.ProducesArtifact));
    }

    [Fact]
    public void Toolbox_IsBuiltFromTheSameDescriptorSet()
    {
        using var editor = new NodeEditorViewModel();

        var toolboxItems = editor.ToolboxCategories.SelectMany(category => category.Items).ToArray();
        Assert.Equal(NodeFactoryRegistrar.Descriptors.Count, toolboxItems.Length);
        Assert.Equal(
            NodeFactoryRegistrar.Descriptors.Select(descriptor => descriptor.TypeKey).OrderBy(key => key),
            toolboxItems.Select(item => item.TypeKey).OrderBy(key => key));
    }

    [Fact]
    public async Task LoadFromAsync_UnavailableNodes_ArePreservedAsPlaceholders()
    {
        var entries = new ConcurrentQueue<LogEntry>();
        var unsubscribe = LogService.Instance.Subscribe((_, entry) => entries.Enqueue(entry));
        try
        {
            using var editor = new NodeEditorViewModel();
            var snapshot = new WorkflowSnapshot
            {
                Nodes =
                [
                    new NodeDto { InstanceId = "hdc", TypeKey = "HDC.Remount", Category = "Flash" },
                    new NodeDto { InstanceId = "dex", TypeKey = "Dex.Parse", Category = "Dex" }
                ],
                Connections =
                [
                    new ConnectionDto
                    {
                        SourceInstanceId = "hdc",
                        SourceConnectorTitle = "Flow",
                        TargetInstanceId = "dex",
                        TargetConnectorTitle = "Flow"
                    }
                ]
            };

            await editor.LoadFromAsync(snapshot);

            Assert.Equal(2, editor.Nodes.Count);
            Assert.All(editor.Nodes, node => Assert.True(node.IsUnavailable));
            Assert.Empty(editor.Connections);
            var roundTrip = WorkflowPersistenceService.BuildNodeDtos(editor.Nodes);
            Assert.Contains(roundTrip, node => node.TypeKey == "HDC.Remount");
            Assert.Contains(roundTrip, node => node.TypeKey == "Dex.Parse");
        }
        finally
        {
            unsubscribe();
        }
    }

    [Fact]
    public async Task WorkflowSnapshot_PreservesConfigurableNodeState()
    {
        using var editor = new NodeEditorViewModel();
        var constant = NodeTypeRegistry.Create("Data.Constant")!;
        constant.NodeValueType = "Number";
        constant.NodeValue = "42";
        constant.Location = new Point(12, 34);
        editor.AddNode(constant);

        var image = NodeTypeRegistry.Create("Data.ProjectImage")!;
        image.SelectedImage = "images/boot.img";
        editor.AddNode(image);

        var snapshot = new WorkflowSnapshot
        {
            Nodes = WorkflowPersistenceService.BuildNodeDtos(editor.Nodes)
        };
        using var restored = new NodeEditorViewModel();
        await restored.LoadFromAsync(snapshot);

        var restoredConstant = Assert.Single(restored.Nodes, node => node.TypeKey == "Data.Constant");
        Assert.Equal("Number", restoredConstant.NodeValueType);
        Assert.Equal("42", restoredConstant.NodeValue);
        Assert.Equal(new Point(12, 34), restoredConstant.Location);
        var restoredImage = Assert.Single(restored.Nodes, node => node.TypeKey == "Data.ProjectImage");
        Assert.Equal("images/boot.img", restoredImage.SelectedImage);
    }

    [Fact]
    public void UndoDepth_DropsOldestCommand()
    {
        using var editor = new NodeEditorViewModel();
        for (var index = 1; index <= 101; index++)
        {
            var node = NodeTypeRegistry.Create("Flow.Start")!;
            node.Title = $"N{index}";
            editor.AddNode(node);
        }

        for (var index = 0; index < 100; index++) editor.Undo();

        var remaining = Assert.Single(editor.Nodes);
        Assert.Equal("N1", remaining.Title);
    }

    [Fact]
    public void CopyPaste_RecreatesConcreteNodeAndConfiguration()
    {
        using var editor = new NodeEditorViewModel();
        var constant = NodeTypeRegistry.Create("Data.Constant")!;
        constant.NodeValueType = "Boolean";
        constant.NodeValue = "true";
        constant.IsSelected = true;
        editor.AddNode(constant);

        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Copy));
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Paste));

        Assert.Equal(2, editor.Nodes.Count);
        var copy = Assert.Single(editor.Nodes, node => node.Title.EndsWith("(copy)", StringComparison.Ordinal));
        Assert.Equal("Data.Constant", copy.TypeKey);
        Assert.Equal("Boolean", copy.NodeValueType);
        Assert.Equal("true", copy.NodeValue);
        Assert.NotEmpty(copy.Output);
    }

    [Fact]
    public void PropertyEdits_AreUndoableAndRedoable()
    {
        using var editor = new NodeEditorViewModel();
        var constant = NodeTypeRegistry.Create("Data.Constant")!;
        editor.AddNode(constant);

        constant.NodeValueType = "Number";
        constant.NodeValue = "42";
        editor.Undo();
        Assert.Equal(string.Empty, constant.NodeValue);
        editor.Undo();
        Assert.Equal("Text", constant.NodeValueType);
        editor.Redo();
        Assert.Equal("Number", constant.NodeValueType);
        editor.Redo();
        Assert.Equal("42", constant.NodeValue);

        var reader = NodeTypeRegistry.Create("Data.ReadFile")!;
        editor.AddNode(reader);
        var input = Assert.Single(reader.Input);
        input.InitialValue = "boot.img";
        editor.Undo();
        Assert.Null(input.InitialValue);
        editor.Redo();
        Assert.Equal("boot.img", input.InitialValue);
    }

    [Fact]
    public void SetSelection_MultipleNodes_SynchronizesSelectionAndBatchState()
    {
        using var editor = new NodeEditorViewModel();
        var first = NodeTypeRegistry.Create("Data.Constant")!;
        var second = NodeTypeRegistry.Create("Data.Constant")!;
        editor.AddNode(first);
        editor.AddNode(second);

        editor.SetSelection([first, second], second);

        Assert.Equal(2, editor.SelectedNodeCount);
        Assert.True(editor.HasSelectedNodes);
        Assert.True(editor.IsMultipleSelection);
        Assert.False(editor.IsSingleSelection);
        Assert.Same(second, editor.SelectedNode);
        Assert.Collection(editor.SelectedNodes,
            node => Assert.Same(first, node),
            node => Assert.Same(second, node));
        Assert.True(first.IsSelected);
        Assert.True(second.IsSelected);
        Assert.Equal(new Thickness(3), first.NodeBorderThickness);
        Assert.True(editor.DeleteSelectedCommand.CanExecute(null));

        editor.SetSelection([first]);

        Assert.True(editor.IsSingleSelection);
        Assert.False(editor.IsMultipleSelection);
        Assert.False(second.IsSelected);
        Assert.Equal(new Thickness(2), second.NodeBorderThickness);

        editor.SetSelection([]);

        Assert.False(editor.HasSelectedNodes);
        Assert.Null(editor.SelectedNode);
        Assert.False(editor.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void BatchNodeValue_MultipleConstants_UpdatesAsSingleUndoableEdit()
    {
        using var editor = new NodeEditorViewModel();
        var first = NodeTypeRegistry.Create("Data.Constant")!;
        first.NodeValue = "first";
        var second = NodeTypeRegistry.Create("Data.Constant")!;
        second.NodeValue = "second";
        editor.AddNode(first);
        editor.AddNode(second);
        editor.SetSelection([first, second]);

        Assert.True(editor.CanEditBatchConstant);
        Assert.Equal(string.Empty, editor.BatchNodeValue);

        editor.BatchNodeValue = "shared";

        Assert.Equal("shared", first.NodeValue);
        Assert.Equal("shared", second.NodeValue);

        editor.Undo();

        Assert.Equal("first", first.NodeValue);
        Assert.Equal("second", second.NodeValue);

        editor.Redo();

        Assert.Equal("shared", first.NodeValue);
        Assert.Equal("shared", second.NodeValue);
    }

    [Fact]
    public void BatchInput_MultipleCompatibleNodes_UpdatesAsSingleUndoableEdit()
    {
        using var editor = new NodeEditorViewModel();
        var first = NodeTypeRegistry.Create("Data.ReadFile")!;
        var firstInput = Assert.Single(first.Input);
        firstInput.InitialValue = "first.img";
        var second = NodeTypeRegistry.Create("Data.ReadFile")!;
        var secondInput = Assert.Single(second.Input);
        secondInput.InitialValue = "second.img";
        editor.AddNode(first);
        editor.AddNode(second);
        editor.SetSelection([first, second]);

        var batchInput = Assert.Single(editor.BatchInputs);
        Assert.True(batchInput.IsMixed);

        batchInput.TextValue = "shared.img";

        Assert.False(batchInput.IsMixed);
        Assert.Equal("shared.img", firstInput.InitialValue);
        Assert.Equal("shared.img", secondInput.InitialValue);

        editor.Undo();

        Assert.Equal("first.img", firstInput.InitialValue);
        Assert.Equal("second.img", secondInput.InitialValue);
        Assert.True(Assert.Single(editor.BatchInputs).IsMixed);

        editor.Redo();

        Assert.Equal("shared.img", firstInput.InitialValue);
        Assert.Equal("shared.img", secondInput.InitialValue);
        Assert.False(Assert.Single(editor.BatchInputs).IsMixed);
    }

    [Fact]
    public void DeleteSelectedNodes_MultipleConnectedNodes_DeletesAndRestoresAsSingleEdit()
    {
        using var editor = new NodeEditorViewModel();
        var source = NodeTypeRegistry.Create("Data.ReadFile")!;
        var target = NodeTypeRegistry.Create("Data.Concat")!;
        editor.AddNode(source);
        editor.AddNode(target);
        editor.CreateConnection(source.Output.Single(), target.Input.First());
        var connection = Assert.Single(editor.Connections);
        editor.SetSelection([source, target]);

        editor.DeleteSelectedNodes();

        Assert.Empty(editor.Nodes);
        Assert.Empty(editor.Connections);
        Assert.Empty(editor.SelectedNodes);
        Assert.False(source.IsSelected);
        Assert.False(target.IsSelected);

        editor.Undo();

        Assert.Equal(2, editor.Nodes.Count);
        Assert.Same(connection, Assert.Single(editor.Connections));
        Assert.False(source.IsSelected);
        Assert.False(target.IsSelected);

        editor.Redo();

        Assert.Empty(editor.Nodes);
        Assert.Empty(editor.Connections);
    }

    [Fact]
    public void ConnectionRemoval_IsUndoableAndRedoable()
    {
        using var editor = new NodeEditorViewModel();
        var source = NodeTypeRegistry.Create("Data.ReadFile")!;
        var target = NodeTypeRegistry.Create("Data.Concat")!;
        editor.AddNode(source);
        editor.AddNode(target);

        editor.CreateConnection(source.Output.Single(), target.Input.First());
        var connection = Assert.Single(editor.Connections);
        editor.RemoveConnectionCommand.Execute(connection);
        Assert.Empty(editor.Connections);
        editor.Undo();
        Assert.Same(connection, Assert.Single(editor.Connections));
        editor.Redo();
        Assert.Empty(editor.Connections);
    }

    [Fact]
    public void CreateConnection_SameDirectionConnectors_RejectsConnection()
    {
        using var editor = new NodeEditorViewModel();
        var first = NodeTypeRegistry.Create("Data.Concat")!;
        var second = NodeTypeRegistry.Create("Data.Concat")!;
        editor.AddNode(first);
        editor.AddNode(second);

        editor.CreateConnection(first.Input.First(), second.Input.First());
        editor.CreateConnection(first.Output.Single(), second.Output.Single());

        Assert.Empty(editor.Connections);
    }

    [Fact]
    public void CreateConnection_InputDraggedToOutput_CreatesCanonicalConnection()
    {
        using var editor = new NodeEditorViewModel();
        var source = NodeTypeRegistry.Create("Data.ReadFile")!;
        var target = NodeTypeRegistry.Create("Data.Concat")!;
        editor.AddNode(source);
        editor.AddNode(target);

        editor.CreateConnection(target.Input.First(), source.Output.Single());

        var connection = Assert.Single(editor.Connections);
        Assert.Same(source.Output.Single(), connection.Source);
        Assert.Same(target.Input.First(), connection.Target);
    }

    [Fact]
    public void Clear_DiscardsEditsFromThePreviousCanvas()
    {
        using var editor = new NodeEditorViewModel();
        editor.AddNode(NodeTypeRegistry.Create("Flow.Start")!);
        Assert.True(editor.CanUndo);

        editor.Clear();

        Assert.Empty(editor.Nodes);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        editor.Undo();
        Assert.Empty(editor.Nodes);
    }
}