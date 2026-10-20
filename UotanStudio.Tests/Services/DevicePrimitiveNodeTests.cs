using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Device-side primitives: input validation, structured partition output and read-only declaration.<para>设备侧原语：输入校验、结构化分区输出与只读声明。</para></summary>
public sealed class DevicePrimitiveNodeTests
{
    [Fact]
    public async Task PartitionRead_MissingDevice_FailsBeforeTouchingDisk()
    {
        var node = Factory("Device.Partition.Read");
        var target = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img");
        Seed(node, "Partition", "boot");
        Seed(node, "Output Path", target);

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task PartitionRead_MissingPartition_FailsBeforeTouchingDisk()
    {
        var node = Factory("Device.Partition.Read");
        var target = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img");
        Seed(node, "Output Path", target);

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void PartitionRead_Descriptor_DeclaredReadOnlyAndFastbootScoped()
    {
        var descriptor = NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Device.Partition.Read");

        Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.ReadsDevice));
        Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.RequiresDevice));
        Assert.False(descriptor.Capabilities.HasFlag(NodeCapability.MutatesDevice));
        Assert.False(descriptor.RequiresConfirmation);
        Assert.Equal(DeviceRequiredState.Fastboot, descriptor.RequiredDeviceState);
    }

    [Fact]
    public void TableRead_Descriptor_ExposesStructuredCollections()
    {
        var node = Factory("Device.Table.Read");

        Assert.Equal(NodePortType.List, node.Output.Single(port => port.Title == "Entries").PortType);
        Assert.Equal(NodePortType.List, node.Output.Single(port => port.Title == "Names").PortType);
        Assert.Equal(NodePortType.Text, node.Output.Single(port => port.Title == "Partitions").PortType);
    }

    [Fact]
    public void TableRead_NameSizeText_SplitsIntoNamesAndFields()
    {
        var entries = new object[] { "boot=67108864", "vendor_boot=100663296" };
        var split = Factory("Data.List.ElementAt");
        var item = entries[1] as string;
        var index = item!.IndexOf('=', StringComparison.Ordinal);

        Assert.Equal("vendor_boot", item[..index]);
        Assert.Equal("100663296", item[(index + 1)..]);
        Assert.Equal(NodePortType.List, split.Input.Single(port => port.Title == "List").PortType);
    }

    private static FlowNodeViewModel Factory(string typeKey)
        => NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();

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