using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.Nodes;
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

    [Fact]
    public void Specs_RegisterMtkAndHdcNodesOnce()
    {
        Assert.Equal(13, MtkHdcNodeSpecs.All.Count);
        Assert.Equal(13, MtkHdcNodeSpecs.All.Select(spec => spec.TypeKey).Distinct(StringComparer.Ordinal).Count());
        foreach (var spec in MtkHdcNodeSpecs.All)
        {
            Assert.Equal(spec.TypeKey, spec.Factory().TypeKey);
        }
    }

    [Theory]
    [InlineData("Device.Hdc.Install", true)]
    [InlineData("Device.Hdc.Uninstall", true)]
    [InlineData("Device.Hdc.Remount", true)]
    [InlineData("Device.Hdc.Disconnect", true)]
    [InlineData("Device.Hdc.Property", false)]
    [InlineData("Device.Hdc.Forward", false)]
    public void CapabilitySpecs_DeclareDeviceAndConfirmationPolicy(string typeKey, bool confirming)
    {
        var spec = MtkHdcNodeSpecs.All.Single(candidate => candidate.TypeKey == typeKey);

        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Hdc, spec.RequiredProtocol);
        Assert.True(spec.Capabilities.HasFlag(NodeCapability.RequiresDevice));
        Assert.Equal(confirming, spec.Capabilities.HasFlag(NodeCapability.MutatesDevice));
        Assert.Equal(confirming, spec.RequiresConfirmation);
        Assert.Equal(confirming, spec.ToCatalogDescriptor().RequiresConfirmation);
    }

    [Theory]
    [InlineData("Device.Hdc.Install")]
    [InlineData("Device.Hdc.Uninstall")]
    [InlineData("Device.Hdc.Property")]
    [InlineData("Device.Hdc.Forward")]
    [InlineData("Device.Hdc.Remount")]
    [InlineData("Device.Hdc.Disconnect")]
    public async Task Capability_MissingDevice_FailsBeforeTransfer(string typeKey)
    {
        var node = CreateCapability(typeKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("no-device", Status(node));
    }

    [Fact]
    public async Task Capability_InstallMissingPackage_FailsBeforeTransfer()
    {
        var node = CreateCapability("Device.Hdc.Install");
        Seed(node, "Device", Device("HDC1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-package", Status(node));
    }

    [Fact]
    public async Task Capability_UninstallMissingBundle_FailsBeforeTransfer()
    {
        var node = CreateCapability("Device.Hdc.Uninstall");
        Seed(node, "Device", Device("HDC1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-bundle", Status(node));
    }

    [Fact]
    public async Task Capability_PropertyMissingName_FailsBeforeTransfer()
    {
        var node = CreateCapability("Device.Hdc.Property");
        Seed(node, "Device", Device("HDC1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-name", Status(node));
    }

    [Fact]
    public async Task Capability_ForwardInvalidPort_FailsBeforeTransfer()
    {
        var node = CreateCapability("Device.Hdc.Forward");
        Seed(node, "Device", Device("HDC1"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-port", Status(node));
    }

    [Fact]
    public void CapabilityNodes_Ports_ExposeDeviceAndSummary()
    {
        foreach (var typeKey in new[]
        {
            "Device.Hdc.Install", "Device.Hdc.Uninstall", "Device.Hdc.Property", "Device.Hdc.Forward",
            "Device.Hdc.Remount", "Device.Hdc.Disconnect"
        })
        {
            var node = CreateCapability(typeKey);

            Assert.Equal(typeKey, node.TypeKey);
            Assert.Equal(NodePortType.Device, Input(node, "Device").PortType);
            Assert.Equal(NodePortType.Text, Input(node, "Serial").PortType);
            Assert.Equal(NodePortType.Text, Output(node, "Status").PortType);
            Assert.Equal(NodePortType.Text, Output(node, "Output").PortType);
        }

        Assert.Equal(NodePortType.Text, Output(CreateCapability("Device.Hdc.Property"), "Value").PortType);
    }

    [Theory]
    [InlineData("Device.Hdc.Install", "Package", NodePortType.Path)]
    [InlineData("Device.Hdc.Uninstall", "Bundle", NodePortType.Text)]
    [InlineData("Device.Hdc.Property", "Name", NodePortType.Text)]
    [InlineData("Device.Hdc.Forward", "Local Port", NodePortType.Number)]
    [InlineData("Device.Hdc.Forward", "Remote Port", NodePortType.Number)]
    public void CapabilityNodes_RequiredInputs_FollowOperationContract(string typeKey, string title, NodePortType portType)
    {
        var port = Input(CreateCapability(typeKey), title);

        Assert.Equal(portType, port.PortType);
        Assert.True(port.IsRequired);
    }

    [Fact]
    public void Forward_ReverseInput_IsOptionalBoolean()
    {
        var reverse = Input(CreateCapability("Device.Hdc.Forward"), "Reverse");

        Assert.Equal(NodePortType.Boolean, reverse.PortType);
        Assert.False(reverse.IsRequired);
    }

    private static FlowNodeViewModel CreateCapability(string typeKey) =>
        MtkHdcNodeSpecs.All.Single(spec => spec.TypeKey == typeKey).Factory();

    private static NodeConnectorViewModel Input(FlowNodeViewModel node, string title) =>
        node.Input.Single(port => port.Title == title);

    private static NodeConnectorViewModel Output(FlowNodeViewModel node, string title) =>
        node.Output.Single(port => port.Title == title);

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