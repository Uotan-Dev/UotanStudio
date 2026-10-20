using System.Text;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchAssetNodeTests
{
    [Fact]
    public async Task Resolve_EmbeddedResource_CapturesSharedArchiveScopeWithoutMutableParent()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var parent = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.parent", Name = "Parent" }
        };
        foreach (var name in new[] { "first", "second" })
        {
            var directory = Path.Combine(temp.Path, name);
            Directory.CreateDirectory(directory);
            var child = await PackageAsync(directory, name, token);
            child.Manifest.Id = "tests." + name;
            parent.Graph.Nodes.Add(new NodeDto { InstanceId = name, TypeKey = child.NodeTypeKey, CompositePackage = child });
            parent.Manifest.RequiredNodeTypes.Add(child.NodeTypeKey);
        }
        var path = Path.Combine(temp.Path, "parent.uotanplugin");
        await VisualPluginBundle.SaveAsync(path, parent, token);
        var loaded = VisualPluginBundle.Load(path);
        var resource = PatchAsset.Resolve(loaded.Graph.Nodes[1].CompositePackage!, "config");
        loaded.Graph.Nodes.Clear();

        Assert.Equal("second", (await resource.ReadTextAsync(token)).Text);
    }

    [Fact]
    public async Task Execute_ImportFailure_PreservesExistingTarget()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "source");
        var target = Path.Combine(temp.Path, "target");
        await File.WriteAllTextAsync(source, "original", token);
        await File.WriteAllTextAsync(target, "keep target", token);
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.import-failure", Name = "Failure" },
            Assets = [await VisualPluginAsset.FromFileAsync("config", source, token)]
        };
        await File.WriteAllTextAsync(source, "modified", token);
        var asset = PatchNodeViewModel.Create("Resource.Asset.Read");
        Set(asset, "Operation", "Select");
        Set(asset, "Asset ID", "config");
        var workspace = PatchNodeViewModel.Create("Workspace.Directory.Open");
        Set(workspace, "Root", temp.Path);
        var import = PatchNodeViewModel.Create("Workspace.Asset.Stage");
        Set(import, "Relative Path", "target");
        Set(import, "Operation", "Replace");

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([asset, workspace, import],
            [Connect(asset, "Asset", import, "Asset"), Connect(workspace, "Workspace", import, "Workspace")], token,
            new NodeExecutionOptions { Package = package });

        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("keep target", await File.ReadAllTextAsync(target, token));
    }

    [Fact]
    public async Task ReadText_InvalidUtf8_RejectsReplacementDecoding()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "invalid");
        await File.WriteAllBytesAsync(source, new byte[] { 0xc0, 0xaf }, token);
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.invalid-text", Name = "Invalid text" },
            Assets = [await VisualPluginAsset.FromFileAsync("config", source, token)]
        };

        await Assert.ThrowsAsync<DecoderFallbackException>(() => PatchAsset.Resolve(package, "config").ReadTextAsync(token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_ResourceImport_PreviewsOrPublishesVerifiedBytes(bool preview)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var package = await PackageAsync(temp.Path, "resource content", token);
        var output = Path.Combine(temp.Path, "target");
        var asset = PatchNodeViewModel.Create("Resource.Asset.Read");
        Set(asset, "Operation", "Select");
        Set(asset, "Asset ID", "config");
        var workspace = PatchNodeViewModel.Create("Workspace.Directory.Open");
        Set(workspace, "Root", temp.Path);
        var import = PatchNodeViewModel.Create("Workspace.Asset.Stage");
        Set(import, "Relative Path", "target");

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([asset, workspace, import],
            [Connect(asset, "Asset", import, "Asset"), Connect(workspace, "Workspace", import, "Workspace")], token,
            new NodeExecutionOptions { Preview = preview, Package = package });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Single(result.PatchChanges);
        Assert.Equal(!preview, File.Exists(output));
        if (!preview)
        {
            Assert.Equal("resource content", await File.ReadAllTextAsync(output, token));
        }
    }

    [Fact]
    public async Task Execute_NestedComposite_UsesOwnPackageInsteadOfParentScope()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        NodeFactoryRegistrar.RegisterAll();
        var innerDirectory = Path.Combine(temp.Path, "inner");
        Directory.CreateDirectory(innerDirectory);
        var parent = await PackageAsync(temp.Path, "parent", token);
        var inner = await PackageAsync(innerDirectory, "inner", token);
        var select = PatchNodeViewModel.Create("Resource.Asset.Read");
        Set(select, "Operation", "Select");
        Set(select, "Asset ID", "config");
        inner.Manifest.RequiredNodeTypes = ["Resource.Asset.Read"];
        inner.Graph.Nodes = WorkflowPersistenceService.BuildNodeDtos([select]);
        inner.Outputs = [new VisualPluginPortDefinition
        {
            Id = "asset", Name = "Asset", NodeInstanceId = select.InstanceId, PortId = "asset", PortType = "PatchAsset"
        }];
        var composite = new CompositeNodeViewModel(inner);
        var read = PatchNodeViewModel.Create("Resource.Asset.Read");
        Set(read, "Operation", "ReadText");

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([composite, read],
            [Connect(composite, "asset", read, "Asset")], token, new NodeExecutionOptions { Package = parent });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("inner", read.Output.Single(port => port.Title == "Text").Data);
    }

    [Fact]
    public async Task Execute_TamperedResource_DiscardsEarlierStagedChanges()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var package = await PackageAsync(temp.Path, "original", token);
        using (var archive = System.IO.Compression.ZipFile.Open(package.BundlePath!, System.IO.Compression.ZipArchiveMode.Update))
        {
            var assetEntry = "assets/" + package.Assets.Single(asset => asset.Id == "config").Sha256.ToLowerInvariant();
            archive.GetEntry(assetEntry)!.Delete();
            await using var entry = archive.CreateEntry(assetEntry).Open();
            await entry.WriteAsync("modified"u8.ToArray(), token);
        }
        var output = Path.Combine(temp.Path, "must-not-publish");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        Set(write, "Output Path", output);
        Set(write, "Text", "staged");
        var asset = PatchNodeViewModel.Create("Resource.Asset.Read");
        Set(asset, "Operation", "Select");
        Set(asset, "Asset ID", "config");
        var read = PatchNodeViewModel.Create("Resource.Asset.Read");
        Set(read, "Operation", "ReadText");

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([write, asset, read],
            [Connect(write, "Completed", asset, "Flow"), Connect(asset, "Asset", read, "Asset")], token,
            new NodeExecutionOptions { Package = package });

        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
        Assert.False(File.Exists(output));
        Assert.Equal(NodeExecutionStatus.Failed, read.ExecutionStatus);
    }

    [Fact]
    public async Task Resolve_MutablePackageChanged_RetainsCapturedResourceIdentity()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var package = await PackageAsync(temp.Path, "captured", token);
        var resource = PatchAsset.Resolve(package, "config");
        package.Assets[0].Sha256 = new string('0', 64);
        package.Assets.Clear();
        package.BundlePath = "missing";

        var text = await resource.ReadTextAsync(token);

        Assert.Equal("captured", text.Text);
    }

    [Fact]
    public async Task Execute_ResourceWithoutPackageScope_FailsClearly()
    {
        var node = PatchNodeViewModel.Create("Resource.Asset.Read");
        Set(node, "Operation", "Select");
        Set(node, "Asset ID", "config");

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([node], [], TestContext.Current.CancellationToken);

        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
    }

    [Fact]
    public async Task ReadText_Utf16Resource_PreservesEncodingAndText()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "text");
        await File.WriteAllTextAsync(source, "中文\r\n", new UnicodeEncoding(false, true), token);
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.encoding", Name = "Encoding" },
            Assets = [await VisualPluginAsset.FromFileAsync("text", source, token)]
        };

        var document = await PatchAsset.Resolve(package, "text").ReadTextAsync(token);

        Assert.Equal("utf16-le", document.Encoding);
        Assert.Equal("中文\r\n", document.Text);
    }

    [Fact]
    public void BuildRuntime_AssetPortsAndId_PreserveTypedContract()
    {
        NodeFactoryRegistrar.RegisterAll();
        var node = PatchNodeViewModel.Create("Resource.Asset.Read");
        Set(node, "Operation", "Select");
        Set(node, "Asset ID", "portable-config");
        var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([node]) };

        var restored = WorkflowRuntimeBuilder.Build(snapshot, false);

        Assert.Empty(restored.Errors);
        var selected = Assert.Single(restored.Nodes);
        Assert.Equal("portable-config", selected.Input.Single(port => port.Title == "Asset ID").InitialValue);
        Assert.Equal(NodePortType.PatchAsset, selected.Output.Single(port => port.Title == "Asset").PortType);
    }

    private static async Task<VisualPluginPackage> PackageAsync(string directory, string text, CancellationToken token)
    {
        var source = Path.Combine(directory, "input");
        await File.WriteAllTextAsync(source, text, token);
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.asset", Name = "Asset" },
            Assets = [await VisualPluginAsset.FromFileAsync("config", source, token)]
        };
        var path = Path.Combine(directory, "test.uotanplugin");
        await VisualPluginBundle.SaveAsync(path, package, token);
        return VisualPluginBundle.Load(path);
    }

    private static void Set(FlowNodeViewModel node, string title, object value)
        => node.Input.Single(port => port.Title == title).InitialValue = value;

    private static NodeConnection Connect(FlowNodeViewModel source, string output, FlowNodeViewModel target, string input) => new()
    {
        SourceInstanceId = source.InstanceId,
        SourceConnector = output,
        TargetInstanceId = target.InstanceId,
        TargetConnector = input
    };

    private sealed class Log : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}