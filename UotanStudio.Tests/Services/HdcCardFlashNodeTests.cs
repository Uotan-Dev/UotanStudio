using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>HDC node target, path and timeout gates that must fail before any host channel is opened.<para>HDC 节点的目标、路径与超时门：必须在打开任何主机通道之前失败。</para></summary>
public sealed class HdcCardFlashNodeTests
{
    [Fact]
    public async Task Wait_ZeroTimeout_FailsBeforeScanning()
    {
        var node = HdcWaitNodeViewModel.CreateWaitNode();
        Seed(node, "Timeout", 0d);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-timeout", Status(node));
        Assert.Equal(false, node.Output.Single(port => port.Title == "Ready").Data);
    }

    [Fact]
    public async Task Shell_MissingDevice_FailsBeforeOpeningSession()
    {
        var node = HdcShellNodeViewModel.CreateShellNode();
        Seed(node, "Command", "param get const.product.model");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("no-device", Status(node));
        Assert.Equal(false, node.Output.Single(port => port.Title == "Result").Data);
    }

    [Fact]
    public async Task Shell_MissingCommand_FailsBeforeOpeningSession()
    {
        var node = HdcShellNodeViewModel.CreateShellNode();
        Seed(node, "Device", Device("HDC1"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ExecuteAsync(new NodeContext()));

        Assert.True(failure.Message.Contains("Command", StringComparison.Ordinal));
        Assert.Equal("missing-command", Status(node));
    }

    [Fact]
    public async Task Shell_NonHdcDevice_FailsBeforeOpeningSession()
    {
        var node = HdcShellNodeViewModel.CreateShellNode();
        Seed(node, "Device", new DetectedDevice
        {
            Key = "usb:ADB1",
            Serial = "ADB1",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb
        });
        Seed(node, "Command", "param get const.product.model");

        await Assert.ThrowsAsync<NotSupportedException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-device", Status(node));
    }

    [Fact]
    public async Task Push_MissingLocalPath_FailsBeforeTransfer()
    {
        var node = HdcPushNodeViewModel.CreatePushNode();
        Seed(node, "Device", Device("HDC1"));
        Seed(node, "Remote Path", "/data/local/tmp/payload");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-local", Status(node));
        Assert.Equal(false, node.Output.Single(port => port.Title == "Result").Data);
    }

    [Fact]
    public async Task Push_MissingRemotePath_FailsBeforeTransfer()
    {
        var local = Path.GetTempFileName();
        try
        {
            var node = HdcPushNodeViewModel.CreatePushNode();
            Seed(node, "Device", Device("HDC1"));
            Seed(node, "Local Path", local);

            await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

            Assert.Equal("missing-remote", Status(node));
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public async Task Pull_MissingRemotePath_FailsBeforeTransfer()
    {
        var node = HdcPullNodeViewModel.CreatePullNode();
        Seed(node, "Device", Device("HDC1"));
        Seed(node, "Local Path", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-remote", Status(node));
        Assert.Null(node.Output.Single(port => port.Title == "Path").Data);
    }

    [Fact]
    public async Task Pull_MissingDevice_FailsBeforeTransfer()
    {
        var node = HdcPullNodeViewModel.CreatePullNode();
        Seed(node, "Remote Path", "/data/local/tmp/payload");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("no-device", Status(node));
    }

    [Fact]
    public void Wait_Ports_ExposeDeviceAndReadiness()
    {
        var node = HdcWaitNodeViewModel.CreateWaitNode();

        Assert.Equal(NodePortType.Device, node.Output.Single(port => port.Title == "Device").PortType);
        Assert.Equal(NodePortType.Boolean, node.Output.Single(port => port.Title == "Ready").PortType);
        Assert.Equal(NodePortType.Number, node.Input.Single(port => port.Title == "Timeout").PortType);
        Assert.Equal("Device.Hdc.Wait", node.TypeKey);
    }

    [Fact]
    public void PushAndPull_Ports_ExposePathAndResult()
    {
        var push = HdcPushNodeViewModel.CreatePushNode();
        var pull = HdcPullNodeViewModel.CreatePullNode();

        Assert.Equal(NodePortType.Path, push.Input.Single(port => port.Title == "Local Path").PortType);
        Assert.Equal(NodePortType.Boolean, push.Output.Single(port => port.Title == "Result").PortType);
        Assert.Equal(NodePortType.Path, pull.Input.Single(port => port.Title == "Local Path").PortType);
        Assert.Equal(NodePortType.Path, pull.Output.Single(port => port.Title == "Path").PortType);
        Assert.Equal("Device.Hdc.Push", push.TypeKey);
        Assert.Equal("Device.Hdc.Pull", pull.TypeKey);
    }

    private static DetectedDevice Device(string serial) => new()
    {
        Key = $"usb:{serial}",
        Serial = serial,
        Protocol = DeviceProtocolKind.Hdc,
        Transport = DeviceTransportKind.Usb
    };

    private static object? Status(FlowNodeViewModel node) =>
        node.Output.Single(port => port.Title == "Status").Data;

    private static void Seed(FlowNodeViewModel node, string inputPort, object? value) =>
        node.Input.Single(port => port.Title == inputPort).Data = value;

    private sealed class NodeContext : INodeExecutionContext
    {
        public IUnifiedLog Log { get; } = new TestLog();

        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

        public void ReportProgress(double progress)
        {
        }
    }

    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message)
        {
        }

        public void Write(LogLevel level, string source, string format, params object[] args)
        {
        }

        public void SetMinimumLevel(LogLevel level)
        {
        }
    }
}