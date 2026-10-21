using System.Collections.Concurrent;
using Avalonia;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Services;
using UotanStudio.Services.Plugins;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NodeFactoryRegistrarTests
{
    [Fact]
    public void FacadeAndRegistry_PublishSameSnapshot()
    {
        Assert.Equal(
            NodeFactoryRegistrar.Descriptors.Select(descriptor => descriptor.TypeKey),
            PluginRegistry.Shared.Descriptors.Select(descriptor => descriptor.TypeKey));
        Assert.Same(NodeFactoryRegistrar.BuiltInTypeKeys, PluginRegistry.Shared.BuiltInTypeKeys);
    }

    [Fact]
    public void Descriptors_RequiresConfirmation_MatchesMutatingDeviceOperations()
    {
        var confirming = NodeFactoryRegistrar.Descriptors
            .Where(descriptor => descriptor.RequiresConfirmation)
            .Select(descriptor => descriptor.TypeKey)
            .OrderBy(typeKey => typeKey, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Device.Adb.Push",
                "Device.Edl.Erase",
                "Device.Edl.Flash",
                "Device.Edl.RepairGpt",
                "Device.Edl.UfsProvision",
                "Device.Hdc.Push",
                "Device.Hdc.Shell",
                "Device.Mtk.Operation",
                "Device.Partition.Erase",
                "Device.Partition.Flash",
                "Device.Power.Reboot",
                "Device.Rkdl.Flash",
                "Device.Shell.Command",
                "Device.Slot.Switch"
            ],
            confirming);
        Assert.All(
            NodeFactoryRegistrar.Descriptors.Where(descriptor => descriptor.RequiresConfirmation),
            descriptor => Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.MutatesDevice)));
    }

    [Fact]
    public void Descriptors_RequiredDeviceState_OnlyDeclaredForDeviceNodes()
    {
        foreach (var descriptor in NodeFactoryRegistrar.Descriptors)
        {
            if (descriptor.TypeKey.StartsWith("Mirroring.", StringComparison.Ordinal))
            {
                continue;
            }
            var expectedNone = !descriptor.TypeKey.StartsWith("Device.", StringComparison.Ordinal);
            Assert.Equal(expectedNone, descriptor.RequiredDeviceState == DeviceRequiredState.None);
        }

        var flash = Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == "Device.Partition.Flash");
        Assert.Equal(DeviceRequiredState.Fastboot, flash.RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Fastboot,
            Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == "Device.Table.Read").RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Adb,
            Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == "Device.Shell.Command").RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Edl,
            Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == "Device.Edl.Flash").RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Rkdl,
            Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == "Device.Rkdl.Wait").RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Mtk,
            Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == "Device.Mtk.Operation").RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Hdc,
            Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == "Device.Hdc.Pull").RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.AnyDevice,
            Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == "Device.Wait.ForDevice").RequiredDeviceState);
    }

    [Fact]
    public void RegisterVisualPlugin_NonCompositeKind_Throws()
    {
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.WorkflowTemplate,
            Manifest = new VisualPluginManifest { Id = "tests.registry.template", Name = "Template" }
        };

        Assert.Throws<ArgumentException>(() => NodeFactoryRegistrar.RegisterVisualPlugin(package));
    }

    [Fact]
    public async Task RegisterVisualPlugin_ConcurrentUpdates_PreservesDescriptorSnapshotsAndPackages()
    {
        await RegisterConcurrentPluginsAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    private static async Task RegisterConcurrentPluginsAsync()
    {
        var prefix = "tests.registry." + Guid.NewGuid().ToString("N") + ".";
        var token = TestContext.Current.CancellationToken;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 32; index++)
            {
                token.ThrowIfCancellationRequested();
                var package = new VisualPluginPackage
                {
                    Kind = VisualPluginKind.CompositeNode,
                    Manifest = new VisualPluginManifest { Id = prefix + worker + "." + index, Name = "Concurrent composite" }
                };
                try
                {
                    NodeFactoryRegistrar.RegisterVisualPlugin(package);
                    var snapshot = NodeFactoryRegistrar.Descriptors;
                    Assert.Single(snapshot, descriptor => descriptor.TypeKey == package.NodeTypeKey);
                    Assert.True(NodeFactoryRegistrar.TryGetVisualPluginPackage(package.NodeTypeKey, out var registered));
                    Assert.Same(package, registered);
                    NodeFactoryRegistrar.RegisterAll();
                    Assert.IsType<CompositeNodeViewModel>(NodeTypeRegistry.Create(package.NodeTypeKey));
                    NodeFactoryRegistrar.UnregisterVisualPlugin(package.NodeTypeKey);
                    Assert.Single(snapshot, descriptor => descriptor.TypeKey == package.NodeTypeKey);
                    Assert.False(NodeFactoryRegistrar.TryGetVisualPluginPackage(package.NodeTypeKey, out _));
                    Assert.False(NodeTypeRegistry.Contains(package.NodeTypeKey));
                }
                finally
                {
                    NodeFactoryRegistrar.UnregisterVisualPlugin(package.NodeTypeKey);
                }
            }
        }, token)));
    }

    [Fact]
    public void Toolbox_ObjectFamiliesAndAliases_MakesPatchOperationsDiscoverable()
    {
        using var editor = new NodeEditorViewModel();
        var binary = Assert.Single(editor.ToolboxItems, item => item.TypeKey == "Binary.Search");

        Assert.True(binary.Matches("Binary.Search"));
        Assert.True(binary.Matches("Binary.Pattern"));
        Assert.Contains(editor.ToolboxItems, item => item.FamilyId == "Config");
        Assert.DoesNotContain(editor.ToolboxItems, item => item.TypeKey.StartsWith("Patch.", StringComparison.Ordinal));
    }

    [Fact]
    public void ParameterSchema_ConditionalInputs_PreservesValuesAndProtectionDefaults()
    {
        var descriptor = Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "Image.File.Attributes");
        var node = descriptor.Factory();
        node.Input.Single(input => input.Title == "Operation").InitialValue = "Set";
        var guard = node.Input.Single(input => input.Title == "Check Old Value");
        var expected = node.Input.Single(input => input.Title == "Expected UID");
        expected.InitialValue = "1000";

        Assert.True(expected.IsAdvanced);
        Assert.False(expected.IsVisibleWhen!(node));
        guard.InitialValue = true;
        Assert.True(expected.IsVisibleWhen(node));
        guard.InitialValue = false;
        Assert.Equal("1000", expected.InitialValue);
        Assert.True(node.Input.Single(input => input.Title == "Extraction Budget").IsAdvanced);
    }

    [Fact]
    public void ParameterSchema_ConnectedOperation_LeavesPotentiallyRelevantInputsVisible()
    {
        var descriptor = Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "Config.Edit");
        var node = descriptor.Factory();
        node.Input.Single(input => input.Title == "Format").InitialValue = "Json";
        var operation = node.Input.Single(input => input.Title == "Operation");
        var value = node.Input.Single(input => input.Title == "Value");
        operation.InitialValue = "Delete";
        Assert.False(value.IsVisibleWhen!(node));

        operation.SourceDescription = "upstream operation";

        Assert.True(value.IsVisibleWhen(node));
    }

    [Fact]
    public void Descriptors_NewIdentityContract_HasSegmentsAndFrozenPortIds()
    {
        foreach (var descriptor in NodeFactoryRegistrar.Descriptors.Where(descriptor => NodeFactoryRegistrar.BuiltInTypeKeys.Contains(descriptor.TypeKey)))
        {
            Assert.True(descriptor.TypeKey.Split('.').Length >= 2);
            var node = descriptor.Factory();
            foreach (var port in node.Input.Concat(node.Output))
            {
                Assert.False(string.IsNullOrWhiteSpace(port.PortId));
                var identity = port.StableId;
                port.Title = "Renamed display label";
                Assert.Equal(identity, port.StableId);
            }
        }
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("bootloader")]
    [InlineData("super")]
    public void FlashPreset_DifferentTargets_UsesOneExplicitPartitionContract(string partition)
    {
        var descriptor = Assert.Single(NodeFactoryRegistrar.Descriptors, candidate => candidate.TypeKey == "Device.Partition.Flash");
        var node = descriptor.Factory();
        node.Input.Single(input => input.PortId == "partition").InitialValue = partition;

        Assert.Equal("Device.Partition.Flash", node.TypeKey);
        Assert.Contains(node.Input, input => input.Title == "Image");
        Assert.Equal(partition, node.Input.Single(input => input.Title == "Partition").InitialValue);
        Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "File.Artifact.Publish");
    }

    [Theory]
    [InlineData("Firmware.Container.Unpack", "Firmware Path")]
    [InlineData("Firmware.Container.Pack", "Input Dir")]
    [InlineData("Image.Super.Pack", "Device Size")]
    [InlineData("Network.File.Download", "URL")]
    [InlineData("Network.File.Download", "Output Path")]
    [InlineData("Device.Property.Read", "Name")]
    [InlineData("Data.Value.Compare", "Data A")]
    public void Descriptors_RequiredInputs_FollowOperationContract(string typeKey, string title)
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();

        Assert.True(node.Input.Single(input => input.Title == title).IsRequired);
    }

    [Theory]
    [InlineData("Firmware.Container.Pack", "Config")]
    [InlineData("Validation.Signature.Verify", "Public Key")]
    [InlineData("File.Artifact.Publish", "Data")]
    [InlineData("File.Artifact.Publish", "Artifact")]
    public void Descriptors_OptionalInputs_DoNotBlockPreflight(string typeKey, string title)
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();

        Assert.False(node.Input.Single(input => input.Title == title).IsRequired);
    }

    [Fact]
    public void FirmwarePack_LegacyConfigPort_ExposesCorrectPathLabel()
    {
        var pack = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
            descriptor.TypeKey == "Firmware.Container.Pack").Factory();
        var legacy = pack.Input.Single(input => input.Title == "Config");
        var output = pack.Input.Single(input => input.Title == "Output Path");

        Assert.Equal("config", legacy.StableId);
        Assert.True(legacy.IsAdvanced);
        Assert.Equal("Node_Conn_LegacyOutputPath", legacy.DisplayResourceKey);
        Assert.Equal(NodePortType.Path, output.PortType);
    }

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
                    NodeTypeRegistry.Register(key, () => PatchNodeViewModel.Create("Config.Edit"));
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
    public void Descriptors_DoNotContainRemovedLegacyHdcOrDexNodes()
    {
        var keys = NodeFactoryRegistrar.Descriptors.Select(d => d.TypeKey).ToArray();

        Assert.DoesNotContain(keys, key => key.StartsWith("HDC.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, key => key.Contains("Dex", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Device.Hdc.Wait", keys);
    }

    [Fact]
    public void Descriptors_ExposeExecutionCapabilities()
    {
        var flash = Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "Device.Partition.Flash");
        Assert.True(flash.RequiresConfirmation);
        Assert.True(flash.Capabilities.HasFlag(NodeCapability.RequiresDevice));
        Assert.True(flash.Capabilities.HasFlag(NodeCapability.MutatesDevice));

        var branch = Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "Flow.Condition.Branch");
        Assert.True(branch.Capabilities.HasFlag(NodeCapability.ControlFlow));

        var download = Assert.Single(NodeFactoryRegistrar.Descriptors, descriptor => descriptor.TypeKey == "Network.File.Download");
        Assert.True(download.Capabilities.HasFlag(NodeCapability.RequiresNetwork));
        Assert.True(download.Capabilities.HasFlag(NodeCapability.ProducesArtifact));
    }

    [Fact]
    public void EffectfulNodes_FlowPorts_ConnectToBranchesAndLoops()
    {
        var branch = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
            descriptor.TypeKey == "Flow.Condition.Branch").Factory();
        var firmware = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
            descriptor.TypeKey == "Firmware.Container.Unpack").Factory();
        var flash = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
            descriptor.TypeKey == "Device.Partition.Flash").Factory();

        Assert.Contains(branch.Input, port => port.Title == "Flow" && port.PortType == NodePortType.Flow);
        foreach (var node in new[] { firmware, flash })
        {
            Assert.Contains(node.Input, port => port.Title == "Flow" && port.PortType == NodePortType.Flow);
            Assert.Contains(node.Output, port => port.Title == "Completed" && port.PortType == NodePortType.Flow);
        }
    }

    [Fact]
    public void Toolbox_IsBuiltFromTheSameDescriptorSet()
    {
        using var editor = new NodeEditorViewModel();

        var toolboxItems = editor.ToolboxItems.ToArray();
        Assert.Equal(NodeFactoryRegistrar.Descriptors.Count(descriptor => descriptor.IsToolboxVisible), toolboxItems.Length);
        Assert.Equal(
            NodeFactoryRegistrar.Descriptors.Where(descriptor => descriptor.IsToolboxVisible)
                .Select(descriptor => descriptor.TypeKey).OrderBy(key => key),
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
                    new NodeDto
                    {
                        InstanceId = "hdc", TypeKey = "HDC.Remount", Category = "Flash",
                        OutputPorts = [new NodePortDto { Title = "Flow", PortType = "Flow" }]
                    },
                    new NodeDto
                    {
                        InstanceId = "dex", TypeKey = "Dex.Parse", Category = "Dex",
                        InputPorts = [new NodePortDto { Title = "Flow", PortType = "Flow" }]
                    }
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
            Assert.Single(editor.Connections);
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
    public async Task LoadFromAsync_MergedTypeKeyAlias_CreatesSuccessorWithRecordedDefaults()
    {
        NodeFactoryRegistrar.RegisterAll();
        using var editor = new NodeEditorViewModel();
        var snapshot = new WorkflowSnapshot
        {
            Nodes =
            [
                new NodeDto
                {
                    InstanceId = "gh",
                    TypeKey = "Network.GitHub.Download",
                    InputInitialValues = new Dictionary<string, string>
                    {
                        ["URL"] = "https://github.com/uotanstudio/uotanstudio/releases/download/v1/app.zip",
                        ["Output Path"] = "app.zip"
                    }
                }
            ]
        };

        await editor.LoadFromAsync(snapshot);

        var node = Assert.Single(editor.Nodes);
        Assert.Equal("Network.File.Download", node.TypeKey);
        Assert.Equal("GitHub Mirror", node.Input.Single(port => port.Title == "Source").InitialValue);
        Assert.Equal("https://github.com/uotanstudio/uotanstudio/releases/download/v1/app.zip",
            node.Input.Single(port => port.Title == "URL").InitialValue);
    }

    [Fact]
    public async Task WorkflowSnapshot_PreservesConfigurableNodeState()
    {
        using var editor = new NodeEditorViewModel();
        var reader = NodeTypeRegistry.Create("File.Binary.Read")!;
        reader.Input.Single(port => port.Title == "Path").InitialValue = "boot.img";
        reader.Location = new Point(12, 34);
        editor.AddNode(reader);

        var image = NodeTypeRegistry.Create("Resource.Project.Image")!;
        image.SelectedImage = "images/boot.img";
        editor.AddNode(image);

        var snapshot = new WorkflowSnapshot
        {
            Nodes = WorkflowPersistenceService.BuildNodeDtos(editor.Nodes)
        };
        using var restored = new NodeEditorViewModel();
        await restored.LoadFromAsync(snapshot);

        var restoredReader = Assert.Single(restored.Nodes, node => node.TypeKey == "File.Binary.Read");
        Assert.Equal("boot.img", restoredReader.Input.Single(port => port.Title == "Path").InitialValue);
        Assert.Equal(new Point(12, 34), restoredReader.Location);
        var restoredImage = Assert.Single(restored.Nodes, node => node.TypeKey == "Resource.Project.Image");
        Assert.Equal("images/boot.img", restoredImage.SelectedImage);
    }

    [Fact]
    public void UndoDepth_DropsOldestCommand()
    {
        using var editor = new NodeEditorViewModel();
        for (var index = 1; index <= 101; index++)
        {
            var node = NodeTypeRegistry.Create("File.Binary.Read")!;
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
        var reader = NodeTypeRegistry.Create("File.Binary.Read")!;
        reader.Input.Single(port => port.Title == "Path").InitialValue = "boot.img";
        reader.CustomName = "configured";
        reader.IsSelected = true;
        editor.AddNode(reader);

        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Copy, editor.EditorId));
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Paste, editor.EditorId));

        Assert.Equal(2, editor.Nodes.Count);
        var copy = Assert.Single(editor.Nodes, node => !ReferenceEquals(node, reader));
        Assert.Equal("File.Binary.Read", copy.TypeKey);
        Assert.Equal("configured", copy.CustomName);
        Assert.Equal("boot.img", copy.Input.Single(port => port.Title == "Path").InitialValue);
        Assert.NotEmpty(copy.Output);
    }

    [Fact]
    public void PropertyEdits_AreUndoableAndRedoable()
    {
        using var editor = new NodeEditorViewModel();
        var reader = NodeTypeRegistry.Create("File.Binary.Read")!;
        editor.AddNode(reader);

        reader.CustomName = "boot reader";
        editor.Undo();
        Assert.Equal(string.Empty, reader.CustomName);
        editor.Redo();
        Assert.Equal("boot reader", reader.CustomName);

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
        var first = NodeTypeRegistry.Create("File.Binary.Read")!;
        var second = NodeTypeRegistry.Create("File.Binary.Read")!;
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
    public void BatchInput_MultipleCompatibleNodes_UpdatesAsSingleUndoableEdit()
    {
        using var editor = new NodeEditorViewModel();
        var first = NodeTypeRegistry.Create("File.Binary.Read")!;
        var firstInput = Assert.Single(first.Input);
        firstInput.InitialValue = "first.img";
        var second = NodeTypeRegistry.Create("File.Binary.Read")!;
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
        var source = NodeTypeRegistry.Create("File.Binary.Read")!;
        var target = NodeTypeRegistry.Create("Data.Bytes.Concat")!;
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
        var source = NodeTypeRegistry.Create("File.Binary.Read")!;
        var target = NodeTypeRegistry.Create("Data.Bytes.Concat")!;
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
        var first = NodeTypeRegistry.Create("Data.Bytes.Concat")!;
        var second = NodeTypeRegistry.Create("Data.Bytes.Concat")!;
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
        var source = NodeTypeRegistry.Create("File.Binary.Read")!;
        var target = NodeTypeRegistry.Create("Data.Bytes.Concat")!;
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
        editor.AddNode(NodeTypeRegistry.Create("File.Binary.Read")!);
        Assert.True(editor.CanUndo);

        editor.Clear();

        Assert.Empty(editor.Nodes);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        editor.Undo();
        Assert.Empty(editor.Nodes);
    }
}