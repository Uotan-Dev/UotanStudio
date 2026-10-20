using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Behaviour of the logical-partition unpack node, including input rejection and non-LP image rejection.<para>逻辑分区解包节点的行为，包括输入拒绝与非 LP 镜像拒绝。</para></summary>
public sealed class SuperUnpackNodeTests
{
    [Fact]
    public void SuperUnpack_Descriptor_DeclaresFirmwareArtifactWithoutConfirmation()
    {
        var descriptor = NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Image.Super.Unpack");

        Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.ProducesArtifact));
        Assert.False(descriptor.RequiresConfirmation);
        Assert.Equal("Firmware", descriptor.CategoryPath);
    }

    [Fact]
    public void SuperUnpack_ExposesInventoryAndExtractedOutputs()
    {
        var node = Factory();

        Assert.Equal(NodePortType.List, node.Output.Single(port => port.Title == "Available").PortType);
        Assert.Equal(NodePortType.List, node.Output.Single(port => port.Title == "Extracted").PortType);
        Assert.Equal(NodePortType.Number, node.Output.Single(port => port.Title == "Count").PortType);
    }

    [Fact]
    public async Task SuperUnpack_MissingImage_Fails()
    {
        var node = Factory();
        Seed(node, "Super", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img"));
        Seed(node, "Partitions", "system");
        Seed(node, "Output Dir", Path.GetTempPath());

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task SuperUnpack_NonLpImage_FailsWithoutProducingOutputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var image = Path.Combine(directory, "super.img");
        await File.WriteAllBytesAsync(image, new byte[8192], TestContext.Current.CancellationToken);
        var node = Factory();
        try
        {
            Seed(node, "Super", image);
            Seed(node, "Partitions", "system");
            Seed(node, "Output Dir", Path.Combine(directory, "out"));

            var result = await RunAsync(node);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Null(node.Output.Single(port => port.Title == "Count").Data);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SuperUnpack_AndSuperPack_AreRegisteredAsAPair()
    {
        var keys = NodeFactoryRegistrar.Descriptors
            .Where(item => item.TypeKey.StartsWith("Image.Super.", StringComparison.Ordinal))
            .Select(item => item.TypeKey)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Image.Super.Pack", "Image.Super.Unpack"], keys);
    }

    private static FlowNodeViewModel Factory()
        => NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Image.Super.Unpack").Factory();

    private static void Seed(FlowNodeViewModel node, string inputPort, object? value)
        => node.Input.Single(port => port.Title == inputPort).InitialValue = value;

    private static Task<ExecutionResult> RunAsync(FlowNodeViewModel node)
        => new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [], TestContext.Current.CancellationToken);

    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }

        public void Write(LogLevel level, string source, string format, params object[] args) { }

        public void SetMinimumLevel(LogLevel level) { }
    }
}