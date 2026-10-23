using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>MediaTek node inputs, transport selection and confirmation gates that must fail before any device I/O.<para>联发科节点的输入、传输选择与确认门：必须在任何设备 I/O 之前失败。</para></summary>
public sealed class MtkCardFlashNodeTests
{
    [Fact]
    public async Task Session_MissingPort_FailsBeforeOpeningSerial()
    {
        var node = MtkDeviceSessionNodeViewModel.CreateSessionNode();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ExecuteAsync(new NodeContext()));

        Assert.True(failure.Message.Contains("Port", StringComparison.Ordinal));
        Assert.Equal("missing-port", Status(node));
    }

    [Fact]
    public async Task Session_MissingLoader_FailsBeforeOpeningSerial()
    {
        var node = MtkDeviceSessionNodeViewModel.CreateSessionNode();
        Seed(node, "Port", "COM9");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ExecuteAsync(new NodeContext()));

        Assert.True(failure.Message.Contains("DA Path", StringComparison.Ordinal));
        Assert.Equal("missing-loader", Status(node));
    }

    [Fact]
    public async Task Session_UnknownProtocol_FailsBeforeOpeningSerial()
    {
        var node = MtkDeviceSessionNodeViewModel.CreateSessionNode();
        Seed(node, "Port", "COM9");
        Seed(node, "Protocol", "Kernel");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-protocol", Status(node));
    }

    [Fact]
    public async Task Session_UsbDeviceInput_ReportsUnsupportedSession()
    {
        var node = MtkDeviceSessionNodeViewModel.CreateSessionNode();
        Seed(node, "Device", Device(DeviceProtocolKind.Mtk, "MTK1"));

        await Assert.ThrowsAsync<NotSupportedException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("usb-session-unsupported", Status(node));
    }

    [Fact]
    public async Task Operation_WriteWithoutConfirmation_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "Flash");
        Seed(node, "Partition", "boot");
        Seed(node, "Image", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img"));
        Seed(node, "Confirm", false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ExecuteAsync(new NodeContext()));

        Assert.True(failure.Message.Contains("Confirm", StringComparison.Ordinal));
        Assert.Equal("confirmation-required", Status(node));
        Assert.Equal(false, node.Output.Single(port => port.Title == "Result").Data);
    }

    [Fact]
    public async Task Operation_WriteWithConfirmationButNoImage_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "Flash");
        Seed(node, "Partition", "boot");
        Seed(node, "Confirm", true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-image", Status(node));
    }

    [Fact]
    public async Task Operation_ReadWithoutTransport_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "Read");
        Seed(node, "Partition", "boot");
        Seed(node, "Output Path", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ExecuteAsync(new NodeContext()));

        Assert.True(failure.Message.Contains("Port", StringComparison.Ordinal));
        Assert.Equal("missing-transport", Status(node));
    }

    [Fact]
    public async Task Operation_UnknownProtocol_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "Read");
        Seed(node, "Partition", "boot");
        Seed(node, "Output Path", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img"));
        Seed(node, "Protocol", "Kernel");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-protocol", Status(node));
    }

    [Fact]
    public async Task Operation_UnknownOperation_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "Format");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-operation", Status(node));
    }

    [Fact]
    public async Task Operation_NonMtkDevice_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "Read");
        Seed(node, "Partition", "boot");
        Seed(node, "Output Path", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img"));
        Seed(node, "Device", Device(DeviceProtocolKind.Adb, "ADB1"));

        await Assert.ThrowsAsync<NotSupportedException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-device", Status(node));
        Assert.False(File.Exists((string)node.Input.Single(port => port.Title == "Output Path").Data!));
    }

    [Fact]
    public void Session_Ports_ExposeRequiredPortAndSessionHandle()
    {
        var node = MtkDeviceSessionNodeViewModel.CreateSessionNode();

        Assert.True(node.Input.Single(port => port.Title == "Port").IsRequired);
        Assert.Equal(NodePortType.Text, node.Input.Single(port => port.Title == "Port").PortType);
        Assert.Equal(NodePortType.Path, node.Input.Single(port => port.Title == "DA Path").PortType);
        Assert.Equal(NodePortType.Path, node.Input.Single(port => port.Title == "EMI Path").PortType);
        Assert.Equal(NodePortType.Any, node.Output.Single(port => port.Title == "Session").PortType);
        Assert.Equal("Device.Mtk.Session", node.TypeKey);
    }

    [Fact]
    public void Operation_Ports_ExposeConfirmationResultAndReturnTimeout()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();

        Assert.Equal(NodePortType.Boolean, node.Input.Single(port => port.Title == "Confirm").PortType);
        Assert.Equal(NodePortType.Text, node.Input.Single(port => port.Title == "Operation").PortType);
        Assert.Equal(NodePortType.Number, node.Input.Single(port => port.Title == "Timeout").PortType);
        Assert.False(node.Input.Single(port => port.Title == "Timeout").IsRequired);
        Assert.Equal(NodePortType.Boolean, node.Output.Single(port => port.Title == "Result").PortType);
        Assert.Equal(NodePortType.Text, node.Output.Single(port => port.Title == "Status").PortType);
        Assert.Equal("Device.Mtk.Operation", node.TypeKey);
    }

    [Fact]
    public void Wait_Ports_ExposeSerialProtocolAndTimeout()
    {
        var node = MtkWaitNodeViewModel.CreateWaitNode();

        Assert.Equal(NodePortType.Text, node.Input.Single(port => port.Title == "Serial").PortType);
        Assert.Equal(NodePortType.Text, node.Input.Single(port => port.Title == "Protocol").PortType);
        Assert.Equal(NodePortType.Number, node.Input.Single(port => port.Title == "Timeout").PortType);
        Assert.Equal(NodePortType.Device, node.Output.Single(port => port.Title == "Device").PortType);
        Assert.Equal(NodePortType.Text, node.Output.Single(port => port.Title == "Status").PortType);
        Assert.Equal("Device.Mtk.Wait", node.TypeKey);
    }

    [Fact]
    public void Wait_Spec_IsReadOnlyWithoutConfirmation()
    {
        var spec = MtkHdcNodeSpecs.All.Single(candidate => candidate.TypeKey == "Device.Mtk.Wait");

        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Mtk, spec.RequiredProtocol);
        Assert.True(spec.Capabilities.HasFlag(NodeCapability.RequiresDevice));
        Assert.True(spec.Capabilities.HasFlag(NodeCapability.ReadsDevice));
        Assert.False(spec.Capabilities.HasFlag(NodeCapability.MutatesDevice));
        Assert.False(spec.RequiresConfirmation);
    }

    [Fact]
    public async Task Wait_NonPositiveTimeout_FailsBeforePolling()
    {
        var node = MtkWaitNodeViewModel.CreateWaitNode();
        Seed(node, "Timeout", 0d);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-timeout", Status(node));
        Assert.Null(node.Output.Single(port => port.Title == "Device").Data);
    }

    [Fact]
    public async Task Wait_NonMtkProtocol_FailsBeforePolling()
    {
        var node = MtkWaitNodeViewModel.CreateWaitNode();
        Seed(node, "Protocol", "adb");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-protocol", Status(node));
        Assert.Null(node.Output.Single(port => port.Title == "Device").Data);
    }

    [Fact]
    public async Task Operation_ReturnToBromWithoutConfirmation_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "ReturnToBrom");
        Seed(node, "Confirm", false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ExecuteAsync(new NodeContext()));

        Assert.True(failure.Message.Contains("Confirm", StringComparison.Ordinal));
        Assert.Equal("confirmation-required", Status(node));
        Assert.Equal(false, node.Output.Single(port => port.Title == "Result").Data);
    }

    [Fact]
    public async Task Operation_ReturnToBromWithConfirmationButNoTransport_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "ReturnToBrom");
        Seed(node, "Confirm", true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ExecuteAsync(new NodeContext()));

        Assert.True(failure.Message.Contains("Port", StringComparison.Ordinal));
        Assert.Equal("missing-transport", Status(node));
    }

    [Fact]
    public async Task Operation_ReturnToBromNegativeTimeout_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "ReturnToBrom");
        Seed(node, "Confirm", true);
        Seed(node, "Timeout", -1d);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-timeout", Status(node));
    }

    [Fact]
    public async Task Operation_ReturnToBromNonMtkDevice_FailsBeforeConnecting()
    {
        var node = MtkDeviceOperationNodeViewModel.CreateOperationNode();
        Seed(node, "Operation", "ReturnToBrom");
        Seed(node, "Confirm", true);
        Seed(node, "Device", Device(DeviceProtocolKind.Adb, "ADB1"));

        await Assert.ThrowsAsync<NotSupportedException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-device", Status(node));
    }

    private static DetectedDevice Device(DeviceProtocolKind protocol, string serial) => new()
    {
        Key = $"usb:{serial}",
        Serial = serial,
        Protocol = protocol,
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