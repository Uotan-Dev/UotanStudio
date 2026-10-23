using UotanStudio.Services;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Behaviour of the Pack node's declared format and file list, without invoking a real packing backend.<para>测试 Pack 节点声明的格式与文件清单，不触发真实打包后端。</para></summary>
public sealed class FirmwarePackNodeTests
{
    [Fact]
    public void PackNode_DeclaresFormatAndFileListPorts()
    {
        var node = Factory("Firmware.Container.Pack");

        Assert.Equal(NodePortType.Text, node.Input.Single(port => port.Title == "Format").PortType);
        Assert.Equal(NodePortType.List, node.Input.Single(port => port.Title == "File List").PortType);
        Assert.Equal("auto", Convert.ToString(node.Input.Single(port => port.Title == "Format").InitialValue));
    }

    [Fact]
    public void PackNode_FormatChoices_CoverEveryRegisteredContainer()
    {
        var node = Factory("Firmware.Container.Pack");
        var choices = node.Input.Single(port => port.Title == "Format").Choices.Select(choice => choice.Value).ToArray();

        Assert.Contains("auto", choices);
        Assert.Contains("ext4", choices);
        Assert.Contains("erofs", choices);
        Assert.Contains("cpio", choices);
    }

    [Fact]
    public async Task PackNode_MissingInputDirectory_Fails()
    {
        var node = Factory("Firmware.Container.Pack");
        Seed(node, "Input Dir", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task PackNode_FileListNamingMissingPath_FailsBeforePacking()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "present.txt"), "x", TestContext.Current.CancellationToken);
        var node = Factory("Firmware.Container.Pack");
        var output = Path.Combine(directory, "out.img");
        try
        {
            Seed(node, "Input Dir", directory);
            Seed(node, "Output Path", output);
            Seed(node, "File List", new object[] { "present.txt", "absent.txt" });

            var result = await RunAsync(node);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Contains("absent.txt", node.ExecutionError);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackNode_WithoutMetadataAndAutoFormat_RequiresMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "a.bin"), "x", TestContext.Current.CancellationToken);
        var node = Factory("Firmware.Container.Pack");
        try
        {
            Seed(node, "Input Dir", directory);
            Seed(node, "Output Path", Path.Combine(directory, "out.img"));
            Seed(node, "Format", "auto");

            var result = await RunAsync(node);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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