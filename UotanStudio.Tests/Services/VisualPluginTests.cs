using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class VisualPluginTests
{
    [Theory]
    [InlineData("\"Manifest\":null")]
    [InlineData("\"Graph\":null")]
    [InlineData("\"Inputs\":null")]
    [InlineData("\"Outputs\":[null]")]
    [InlineData("\"Manifest\":{\"Id\":null}")]
    [InlineData("\"Manifest\":{\"RequiredNodeTypes\":null}")]
    [InlineData("\"Manifest\":{\"Platforms\":[null]}")]
    [InlineData("\"Graph\":{\"Nodes\":null}")]
    [InlineData("\"Graph\":{\"Nodes\":[null]}")]
    [InlineData("\"Graph\":{\"Connections\":[null]}")]
    [InlineData("\"Graph\":{\"Nodes\":[{\"InstanceId\":\"n\",\"TypeKey\":\"File.Binary.Read\",\"InputPorts\":null}]}")]
    [InlineData("\"Graph\":{\"Nodes\":[{\"InstanceId\":\"n\",\"TypeKey\":\"File.Binary.Read\",\"OutputPorts\":[null]}]}")]
    [InlineData("\"Graph\":{\"Nodes\":[{\"InstanceId\":\"n\",\"TypeKey\":\"File.Binary.Read\",\"OutputPorts\":[{\"Id\":null}]}]}")]
    [InlineData("\"Kind\":255")]
    public async Task VisualPluginFile_MalformedPackage_RejectsThroughValidation(string member)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "malformed.uotanplugin.json");
        var manifest = member.StartsWith("\"Manifest\"", StringComparison.Ordinal)
            ? string.Empty
            : "\"Manifest\":{\"Id\":\"tests.invalid\",\"Name\":\"Invalid input\",\"RequiredNodeTypes\":[\"File.Binary.Read\"]},";
        await File.WriteAllTextAsync(path, "{" + manifest + member + "}", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => VisualPluginFile.LoadAsync(path, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => VisualPluginFile.Load(path));
    }

    [Fact]
    public async Task VisualPluginFile_NullInternalPort_PreservesExistingOutputOnRejectedSave()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "existing.uotanplugin.json");
        var package = CreateTemplatePackage();
        await VisualPluginFile.SaveAsync(path, package, TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        package.Graph.Nodes[0].OutputPorts.Add(null!);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => VisualPluginFile.SaveAsync(path, package, TestContext.Current.CancellationToken));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VisualPluginFile_RoundTrip_PreservesStablePortsAndConnections()
    {
        using var temp = new TempDirectory();
        var package = CreateTemplatePackage();
        var path = Path.Combine(temp.Path, "portable.uotanplugin.json");

        await VisualPluginFile.SaveAsync(path, package, TestContext.Current.CancellationToken);
        var loaded = await VisualPluginFile.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("data", loaded.Graph.Nodes[0].OutputPorts[0].Id);
        Assert.Equal("data", Assert.Single(loaded.Graph.Connections).SourcePortId);
        Assert.Equal("input", Assert.Single(loaded.Graph.Connections).TargetPortId);
    }

    [Fact]
    public async Task VisualPluginFile_OversizedInput_IsRejectedBeforeDeserialization()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "oversized.uotanplugin.json");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(VisualPluginValidation.MaximumFileSize + 1);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => VisualPluginFile.LoadAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("exceeds", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualPluginValidation_DuplicateNodeAndSelfReference_AreRejected()
    {
        var package = CreateTemplatePackage();
        package.Kind = VisualPluginKind.CompositeNode;
        package.Graph.Nodes[0].TypeKey = package.NodeTypeKey;
        package.Graph.Nodes.Add(new NodeDto
        {
            InstanceId = package.Graph.Nodes[0].InstanceId,
            TypeKey = "File.Binary.Read"
        });
        package.Manifest.RequiredNodeTypes = [package.NodeTypeKey, "File.Binary.Read"];

        var errors = VisualPluginValidation.Validate(package);

        Assert.Contains(errors, error => error.Contains("cannot contain itself", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Duplicate graph node id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompositeNode_ExecutesInternalGraphAndMapsPublicOutput()
    {
        NodeFactoryRegistrar.RegisterAll();
        var field = InteractionNodeViewModel.Create("UI.Field.Create");
        field.Input.Single(port => port.Title == "Label").InitialValue = "Portable label";
        Assert.Single(field.Output).ExposeOnComposite = true;
        var package = VisualPluginPackageBuilder.Create("Portable value", "tests.portable-value",
            VisualPluginKind.CompositeNode, [field], []);
        var composite = new CompositeNodeViewModel(package);

        await composite.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken));

        var element = Assert.IsType<UotanStudio.Projects.Interactions.InteractionElement>(Assert.Single(composite.Output).Data);
        Assert.Equal("Portable label", element.Label);
    }


    [Fact]
    public void PackageBuilder_AbsolutePathDefaults_AreRemovedForPortability()
    {
        NodeFactoryRegistrar.RegisterAll();
        var node = NodeTypeRegistry.Create("File.Binary.Read")!;
        var path = Assert.Single(node.Input, port => port.PortType == NodePortType.Path);
        path.InitialValue = OperatingSystem.IsWindows() ? @"C:\firmware\boot.img" : "/firmware/boot.img";
        path.ExposeOnComposite = true;

        var package = VisualPluginPackageBuilder.Create("Portable path", "tests.portable-path",
            VisualPluginKind.CompositeNode, [node], []);

        Assert.Null(Assert.Single(package.Inputs, input => input.PortType == nameof(NodePortType.Path)).DefaultValue);
        Assert.Null(package.Graph.Nodes[0].InputInitialValuesByPortId);
    }

    [Fact]
    public void PackageBuilder_LongInternalPortId_ProducesBoundedPublicId()
    {
        NodeFactoryRegistrar.RegisterAll();
        var constant = NodeTypeRegistry.Create("Data.Value.Convert")!;
        Assert.Single(constant.Output).PortId = new string('a', 128);
        Assert.Single(constant.Output).ExposeOnComposite = true;

        var package = VisualPluginPackageBuilder.Create("Bounded port", "tests.bounded-port",
            VisualPluginKind.CompositeNode, [constant], []);

        Assert.InRange(Assert.Single(package.Outputs).Id.Length, 1, 128);
        Assert.Empty(VisualPluginValidation.Validate(package));
    }

    [Fact]
    public void ImportTemplate_AppendsRemapsAndUndoesAsOneEdit()
    {
        NodeFactoryRegistrar.RegisterAll();
        var constant = NodeTypeRegistry.Create("Data.Value.Convert")!;
        var package = VisualPluginPackageBuilder.Create("Template", "tests.template",
            VisualPluginKind.WorkflowTemplate, [constant], []);
        using var editor = new NodeEditorViewModel();

        editor.ImportTemplate(package);

        var imported = Assert.Single(editor.Nodes);
        Assert.NotEqual(constant.InstanceId, imported.InstanceId);
        Assert.True(editor.CanUndo);
        editor.Undo();
        Assert.Empty(editor.Nodes);
    }

    [Fact]
    public async Task LoadFromAsync_PersistedNodes_RetainInstanceIds()
    {
        NodeFactoryRegistrar.RegisterAll();
        var package = CreateTemplatePackage();
        using var editor = new NodeEditorViewModel();

        await editor.LoadFromAsync(package.Graph);

        Assert.Equal(["source", "target"], editor.Nodes.Select(node => node.InstanceId));
    }

    [Fact]
    public async Task CreateUpdated_ReorderedGraph_PreservesPublicPortContractAndManifest()
    {
        NodeFactoryRegistrar.RegisterAll();
        var constant = NodeTypeRegistry.Create("Data.Value.Convert")!;
        var reader = NodeTypeRegistry.Create("File.Binary.Read")!;
        Assert.Single(reader.Input, port => port.PortType == NodePortType.Path).IsRequired = true;
        var package = VisualPluginPackageBuilder.Create("Stable contract", "tests.stable-contract",
            VisualPluginKind.CompositeNode, [constant, reader], [], "Tester");
        package.Manifest.Version = "2.3.4";
        package.Manifest.Description = "Retained description";
        package.Manifest.Platforms = ["windows", "linux"];
        var existingInput = Assert.Single(package.Inputs, input => input.NodeInstanceId == reader.InstanceId);
        existingInput.Id = "firmware-path";
        existingInput.Name = "Firmware path";
        existingInput.Required = true;
        using var editor = new NodeEditorViewModel();
        await editor.LoadFromAsync(package.Graph);
        editor.Nodes.Move(1, 0);

        var updated = VisualPluginPackageBuilder.CreateUpdated(package, editor.Nodes, editor.Connections);

        var updatedInput = Assert.Single(updated.Inputs, input => input.NodeInstanceId == reader.InstanceId);
        Assert.Equal("firmware-path", updatedInput.Id);
        Assert.Equal("Firmware path", updatedInput.Name);
        Assert.True(updatedInput.Required);
        Assert.Equal("2.3.4", updated.Manifest.Version);
        Assert.Equal("Retained description", updated.Manifest.Description);
        Assert.Equal(["windows", "linux"], updated.Manifest.Platforms);
        Assert.Empty(VisualPluginValidation.Validate(updated));
    }

    [Fact]
    public async Task CompositeNode_MissingRequiredInput_ThrowsBeforeExecutingGraph()
    {
        NodeFactoryRegistrar.RegisterAll();
        var reader = NodeTypeRegistry.Create("File.Binary.Read")!;
        Assert.Single(reader.Input, port => port.PortType == NodePortType.Path).IsRequired = true;
        var package = VisualPluginPackageBuilder.Create("Required input", "tests.required-input",
            VisualPluginKind.CompositeNode, [reader], []);
        var input = Assert.Single(package.Inputs);
        input.Required = true;
        input.DefaultValue = null;
        var composite = new CompositeNodeViewModel(package);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composite.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));

        Assert.Contains("required", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(input.Name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompositeNode_ConnectedNullRequiredInput_DoesNotUseStoredDefault()
    {
        NodeFactoryRegistrar.RegisterAll();
        var reader = NodeTypeRegistry.Create("File.Binary.Read")!;
        var package = VisualPluginPackageBuilder.Create("Required input", "tests.connected-null",
            VisualPluginKind.CompositeNode, [reader], []);
        var definition = Assert.Single(package.Inputs);
        definition.Required = true;
        var composite = new CompositeNodeViewModel(package);
        var input = Assert.Single(composite.Input);
        input.InitialValue = "must-not-be-read.bin";
        var source = new FlowNodeViewModel("Null source", NodeCategory.Data);
        source.Output.Add(new NodeConnectorViewModel { Title = "Value", IsInput = false, PortType = NodePortType.Path });
        var result = await new NodeExecutionEngine(new TestExecutionContext(TestContext.Current.CancellationToken))
            .ExecuteAsync([source, composite], [new NodeConnection
            {
                SourceInstanceId = source.InstanceId, SourceConnector = "Value",
                TargetInstanceId = composite.InstanceId, TargetConnector = definition.Id
            }], TestContext.Current.CancellationToken);

        Assert.Equal(NodeExecutionStatus.Failed, composite.ExecutionStatus);
        Assert.Contains(composite.InstanceId, result.FailedNodes);
        Assert.Null(input.Data);
        Assert.True(input.HasRuntimeSource);
    }

    [Fact]
    public void VisualPluginValidation_InvalidPortContracts_AreRejected()
    {
        var package = CreateTemplatePackage();
        package.Kind = VisualPluginKind.CompositeNode;
        package.Outputs =
        [
            new VisualPluginPortDefinition
            {
                Id = "first-output",
                Name = "First output",
                PortType = "Number",
                NodeInstanceId = "source",
                PortId = "data"
            },
            new VisualPluginPortDefinition
            {
                Id = "second-output",
                Name = "Second output",
                PortType = "Data",
                NodeInstanceId = "source",
                PortId = "data",
                Required = true
            }
        ];
        package.Graph.Nodes[1].InputPorts[0].PortType = "Unsupported";
        package.Graph.Connections[0].SourcePortId = "missing";

        var errors = VisualPluginValidation.Validate(package);

        Assert.Contains(errors, error => error.Contains("unknown type 'Unsupported'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("source port 'missing' does not exist", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("type does not match", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("exposed more than once", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("cannot be required", StringComparison.Ordinal));
    }

    [Fact]
    public void VisualPluginValidation_DevicePortType_IsSupported()
    {
        var package = CreateTemplatePackage();
        package.Graph.Nodes[0].OutputPorts[0].PortType = "Device";
        package.Graph.Nodes[1].InputPorts[0].PortType = "Device";

        var errors = VisualPluginValidation.Validate(package);

        Assert.DoesNotContain(errors, error => error.Contains("unknown type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Manager_InstallDisableEnableAndRemove_UpdatesRegistry()
    {
        using var temp = new TempDirectory();
        var sourceDirectory = Path.Combine(temp.Path, "source");
        var managerDirectory = Path.Combine(temp.Path, "installed");
        Directory.CreateDirectory(sourceDirectory);
        NodeFactoryRegistrar.RegisterAll();
        var constant = NodeTypeRegistry.Create("Data.Value.Convert")!;
        var package = VisualPluginPackageBuilder.Create("Managed", "tests.managed",
            VisualPluginKind.CompositeNode, [constant], []);
        var source = Path.Combine(sourceDirectory, "managed.uotanplugin.json");
        await VisualPluginFile.SaveAsync(source, package, TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(managerDirectory);

        await manager.InstallAsync(source, TestContext.Current.CancellationToken);
        Assert.True(NodeTypeRegistry.Contains(package.NodeTypeKey));
        Assert.True(Assert.Single(manager.Registrations).IsEnabled);

        await manager.SetEnabledAsync(package.Manifest.Id, false, TestContext.Current.CancellationToken);
        Assert.False(NodeTypeRegistry.Contains(package.NodeTypeKey));
        var disabled = Assert.Single(manager.Registrations);
        Assert.False(disabled.IsEnabled);

        var replacement = VisualPluginPackageBuilder.Create("Managed", "tests.managed",
            VisualPluginKind.CompositeNode, [NodeTypeRegistry.Create("Data.Value.Convert")!], []);
        replacement.Manifest.Description = "Updated graph";
        await manager.UpdateAsync(disabled, replacement, TestContext.Current.CancellationToken);
        var updated = Assert.Single(manager.Registrations);
        Assert.False(updated.IsEnabled);
        Assert.Equal("Updated graph", updated.Description);

        await manager.SetEnabledAsync(package.Manifest.Id, true, TestContext.Current.CancellationToken);
        Assert.True(NodeTypeRegistry.Contains(package.NodeTypeKey));

        await manager.RemoveAsync(package.Manifest.Id, TestContext.Current.CancellationToken);
        Assert.Empty(manager.Registrations);
        Assert.False(NodeTypeRegistry.Contains(package.NodeTypeKey));
        Assert.False(File.Exists(updated.FilePath));
        Assert.True(File.Exists(source));
        manager.Refresh();
        Assert.Empty(manager.Registrations);
    }

    [Fact]
    public async Task RemoveAsync_DuplicateIds_DeletesOnlySelectedFile()
    {
        using var temp = new TempDirectory();
        var package = CreateTemplatePackage();
        var first = Path.Combine(temp.Path, "first.uotanplugin.json");
        var second = Path.Combine(temp.Path, "second.uotanplugin.json");
        await VisualPluginFile.SaveAsync(first, package, TestContext.Current.CancellationToken);
        await VisualPluginFile.SaveAsync(second, package, TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(temp.Path);
        var selected = Assert.Single(manager.Registrations, item => item.FilePath == second);

        await manager.RemoveAsync(selected, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(second));
        Assert.True(File.Exists(first));
        manager.Refresh();
        Assert.Equal(first, Assert.Single(manager.Registrations).FilePath);
    }

    [Fact]
    public async Task RemoveAsync_InvalidPackage_DeletesFileAndSurvivesRefresh()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "invalid.uotanplugin.json");
        await File.WriteAllTextAsync(path, "invalid json", TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(temp.Path);
        var selected = Assert.Single(manager.Registrations);
        Assert.False(selected.IsAvailable);

        await manager.RemoveAsync(selected, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(path));
        manager.Refresh();
        Assert.Empty(manager.Registrations);
    }

    [Fact]
    public async Task UpdateAsync_RemovedPackage_DoesNotRecreateFile()
    {
        using var temp = new TempDirectory();
        var package = CreateTemplatePackage();
        var path = Path.Combine(temp.Path, "removed.uotanplugin.json");
        await VisualPluginFile.SaveAsync(path, package, TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(temp.Path);
        var selected = Assert.Single(manager.Registrations);
        await manager.RemoveAsync(selected, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UpdateAsync(selected, package, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
        Assert.Empty(manager.Registrations);
    }

    [Fact]
    public async Task RemoveAsync_FileLocked_KeepsFileAndRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows file-sharing semantics are required.");
        }
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "locked.uotanplugin.json");
        await VisualPluginFile.SaveAsync(path, CreateTemplatePackage(), TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(temp.Path);
        var selected = Assert.Single(manager.Registrations);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Assert.ThrowsAsync<IOException>(() =>
            manager.RemoveAsync(selected, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(path));
        Assert.Same(selected, Assert.Single(manager.Registrations));
    }

    [Fact]
    public async Task Manager_CyclicCompositeDependencies_AreNotRegistered()
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var first = CreateDependencyPackage("tests.cycle-a", "VisualPlugin.tests.cycle-b");
        var second = CreateDependencyPackage("tests.cycle-b", "VisualPlugin.tests.cycle-a");
        await VisualPluginFile.SaveAsync(Path.Combine(temp.Path, "a.uotanplugin.json"), first,
            TestContext.Current.CancellationToken);
        await VisualPluginFile.SaveAsync(Path.Combine(temp.Path, "b.uotanplugin.json"), second,
            TestContext.Current.CancellationToken);

        var manager = new VisualPluginManager(temp.Path);

        Assert.Equal(2, manager.Registrations.Count);
        Assert.All(manager.Registrations, registration =>
            Assert.Contains(registration.Errors, error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase)));
        Assert.False(NodeTypeRegistry.Contains(first.NodeTypeKey));
        Assert.False(NodeTypeRegistry.Contains(second.NodeTypeKey));
    }

    [Fact]
    public async Task Manager_UpdateCreatingDependencyCycle_RestoresAvailablePackage()
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var first = CreateDependencyPackage("tests.rollback-a", "File.Binary.Read");
        var second = CreateDependencyPackage("tests.rollback-b", first.NodeTypeKey);
        await VisualPluginFile.SaveAsync(Path.Combine(temp.Path, "a.uotanplugin.json"), first,
            TestContext.Current.CancellationToken);
        await VisualPluginFile.SaveAsync(Path.Combine(temp.Path, "b.uotanplugin.json"), second,
            TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(temp.Path);
        var registration = Assert.Single(manager.Registrations, item => item.Id == first.Manifest.Id);
        var invalidUpdate = CreateDependencyPackage(first.Manifest.Id, second.NodeTypeKey);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.UpdateAsync(registration, invalidUpdate, TestContext.Current.CancellationToken));

        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(manager.Registrations, item => Assert.True(item.IsAvailable));
        var restored = await VisualPluginFile.LoadAsync(registration.FilePath,
            TestContext.Current.CancellationToken);
        Assert.Equal(["File.Binary.Read"], restored.Manifest.RequiredNodeTypes);
    }

    [Fact]
    public async Task Manager_AotCapability_None_KeepsDataPackagesAvailable()
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var package = CreateDependencyPackage("tests.aot-active", "File.Binary.Read");
        await VisualPluginFile.SaveAsync(Path.Combine(temp.Path, "aot.uotanplugin.json"), package,
            TestContext.Current.CancellationToken);
        RuntimeCapabilityProbe.SetCapabilityOverride(PluginRuntimeCapability.None);
        try
        {
            var manager = new VisualPluginManager(temp.Path);
            var registration = Assert.Single(manager.Registrations);

            Assert.True(registration.IsAvailable);
            Assert.DoesNotContain(registration.Errors,
                error => error.Contains("AOT", StringComparison.OrdinalIgnoreCase));
            Assert.True(NodeTypeRegistry.Contains(package.NodeTypeKey));
        }
        finally
        {
            RuntimeCapabilityProbe.SetCapabilityOverride(null);
        }
    }

    [Fact]
    public async Task Manager_CodeLoadingCapability_LeavesCompositePluginsAvailable()
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var package = CreateDependencyPackage("tests.jit-active", "File.Binary.Read");
        await VisualPluginFile.SaveAsync(Path.Combine(temp.Path, "jit.uotanplugin.json"), package,
            TestContext.Current.CancellationToken);
        RuntimeCapabilityProbe.SetCapabilityOverride(PluginRuntimeCapability.CodeLoading);
        try
        {
            var manager = new VisualPluginManager(temp.Path);
            var registration = Assert.Single(manager.Registrations);

            Assert.True(registration.IsAvailable);
            Assert.True(NodeTypeRegistry.Contains(package.NodeTypeKey));
        }
        finally
        {
            RuntimeCapabilityProbe.SetCapabilityOverride(null);
        }
    }

    private static VisualPluginPackage CreateTemplatePackage() => new()
    {
        Kind = VisualPluginKind.WorkflowTemplate,
        Manifest = new VisualPluginManifest
        {
            Id = "tests.roundtrip",
            Name = "Round trip",
            RequiredNodeTypes = ["File.Binary.Read", "Data.Value.Convert"]
        },
        Graph = new WorkflowSnapshot
        {
            Nodes =
            [
                new NodeDto
                {
                    InstanceId = "source",
                    TypeKey = "File.Binary.Read",
                    OutputPorts = [new NodePortDto { Id = "data", Title = "Data", PortType = "Data" }]
                },
                new NodeDto
                {
                    InstanceId = "target",
                    TypeKey = "Data.Value.Convert",
                    InputPorts = [new NodePortDto { Id = "input", Title = "Input", PortType = "Data" }]
                }
            ],
            Connections =
            [
                new ConnectionDto
                {
                    SourceInstanceId = "source",
                    SourcePortId = "data",
                    SourceConnectorTitle = "Data",
                    TargetInstanceId = "target",
                    TargetPortId = "input",
                    TargetConnectorTitle = "Input"
                }
            ]
        }
    };

    private static VisualPluginPackage CreateDependencyPackage(string id, string dependency) => new()
    {
        Kind = VisualPluginKind.CompositeNode,
        Manifest = new VisualPluginManifest
        {
            Id = id,
            Name = id,
            RequiredNodeTypes = [dependency]
        },
        Graph = new WorkflowSnapshot
        {
            Nodes = [new NodeDto { InstanceId = "dependency", TypeKey = dependency }]
        }
    };

    private sealed class TestExecutionContext(CancellationToken cancellationToken) : INodeExecutionContext, IUnifiedLog
    {
        public IUnifiedLog Log => this;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public void ReportProgress(double progress) { }
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}