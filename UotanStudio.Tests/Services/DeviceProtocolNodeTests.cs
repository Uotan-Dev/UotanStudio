using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class DeviceProtocolNodeTests
{
    private static readonly string[] WriteTypeKeys =
    [
        "Device.Edl.Flash",
        "Device.Edl.Erase",
        "Device.Edl.RepairGpt",
        "Device.Edl.UfsProvision",
        "Device.Edl.UfsReset",
        "Device.Rkdl.Flash",
        "Device.Adb.Push"
    ];

    private static readonly string[] ReadTypeKeys =
    [
        "Device.Edl.Backup",
        "Device.Edl.ReadPartition",
        "Device.Rkdl.ReadPartition",
        "Device.Rkdl.Wait",
        "Device.Adb.Pull",
        "Device.Wait.ForDevice",
        "Device.Fastboot.GetVariable"
    ];

    [Fact]
    public void Specs_CoverEveryProtocolTypeKeyWithMatchingFactories()
    {
        Assert.Equal(14, DeviceProtocolNodeSpecs.All.Count);
        Assert.Equal(14, DeviceProtocolNodeSpecs.CreateDescriptors().Count);
        Assert.Equal(14, DeviceProtocolNodeSpecs.All.Select(static spec => spec.TypeKey).Distinct(StringComparer.Ordinal).Count());

        foreach (var spec in DeviceProtocolNodeSpecs.All)
        {
            var node = spec.Factory();
            Assert.Equal(spec.TypeKey, node.TypeKey);
            Assert.Equal(NodeCategory.Flash, node.Category);
            Assert.Equal(spec.TypeKey, spec.ToCatalogDescriptor().Factory().TypeKey);
        }
    }

    [Fact]
    public void WriteSpecs_RequireConfirmationAndDeclareMutation()
    {
        foreach (var typeKey in WriteTypeKeys)
        {
            var spec = Spec(typeKey);
            Assert.True(spec.RequiresConfirmation);
            Assert.True(spec.Capabilities.HasFlag(NodeCapability.MutatesDevice));
            Assert.True(spec.Capabilities.HasFlag(NodeCapability.RequiresDevice));

            var descriptor = spec.ToCatalogDescriptor();
            Assert.True(descriptor.RequiresConfirmation);
            Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.MutatesDevice));
            Assert.Equal("Device", descriptor.CategoryPath);
            Assert.Equal(typeKey, descriptor.TypeKey);
        }
    }

    [Fact]
    public void ReadSpecs_DeclareReadOnlyCapabilitiesWithoutConfirmation()
    {
        foreach (var typeKey in ReadTypeKeys)
        {
            var spec = Spec(typeKey);
            Assert.False(spec.RequiresConfirmation);
            Assert.False(spec.Capabilities.HasFlag(NodeCapability.MutatesDevice));
            Assert.True(spec.Capabilities.HasFlag(NodeCapability.ReadsDevice));
            Assert.True(spec.Capabilities.HasFlag(NodeCapability.RequiresDevice));

            var descriptor = spec.ToCatalogDescriptor();
            Assert.False(descriptor.RequiresConfirmation);
            Assert.False(descriptor.Capabilities.HasFlag(NodeCapability.MutatesDevice));
        }
    }

    [Fact]
    public void Specs_DeclareTheExactProtocol()
    {
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Edl, Spec("Device.Edl.Flash").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Edl, Spec("Device.Edl.Erase").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Edl, Spec("Device.Edl.Backup").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Edl, Spec("Device.Edl.RepairGpt").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Edl, Spec("Device.Edl.UfsProvision").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Edl, Spec("Device.Edl.UfsReset").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Edl, Spec("Device.Edl.ReadPartition").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Rkdl, Spec("Device.Rkdl.Flash").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Rkdl, Spec("Device.Rkdl.ReadPartition").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Rkdl, Spec("Device.Rkdl.Wait").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Adb, Spec("Device.Adb.Push").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Adb, Spec("Device.Adb.Pull").RequiredProtocol);
        Assert.Equal<DeviceProtocolKind?>(DeviceProtocolKind.Fastboot,
            Spec("Device.Fastboot.GetVariable").RequiredProtocol);
        Assert.Null(Spec("Device.Wait.ForDevice").RequiredProtocol);
    }

    [Fact]
    public void CatalogDescriptors_MapDeclaredProtocolToRequiredDeviceState()
    {
        Assert.Equal(DeviceRequiredState.Adb, Spec("Device.Adb.Push").ToCatalogDescriptor().RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Fastboot,
            Spec("Device.Fastboot.GetVariable").ToCatalogDescriptor().RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.AnyDevice,
            Spec("Device.Wait.ForDevice").ToCatalogDescriptor().RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Edl, Spec("Device.Edl.Flash").ToCatalogDescriptor().RequiredDeviceState);
        Assert.Equal(DeviceRequiredState.Rkdl, Spec("Device.Rkdl.Flash").ToCatalogDescriptor().RequiredDeviceState);
    }

    [Fact]
    public void EdlFlash_Ports_DeclareDevicePartitionImageAndOptionalTooling()
    {
        var node = Create("Device.Edl.Flash");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Partition", NodePortType.Text, required: true);
        AssertInput(node, "Image", NodePortType.Path, required: true);
        AssertInput(node, "Programmer", NodePortType.Path, required: false);
        AssertInput(node, "LUN", NodePortType.Number, required: false);
        AssertInput(node, "Verify", NodePortType.Boolean, required: false);
        AssertOutput(node, "Result", NodePortType.Boolean);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void EdlErase_Ports_DeclareDevicePartitionAndOptionalTooling()
    {
        var node = Create("Device.Edl.Erase");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Partition", NodePortType.Text, required: true);
        AssertInput(node, "Programmer", NodePortType.Path, required: false);
        AssertInput(node, "LUN", NodePortType.Number, required: false);
        AssertOutput(node, "Result", NodePortType.Boolean);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void EdlBackup_Ports_DeclareDevicePartitionAndOutputPath()
    {
        var node = Create("Device.Edl.Backup");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Partition", NodePortType.Text, required: true);
        AssertInput(node, "Output Path", NodePortType.Path, required: true);
        AssertOutput(node, "Path", NodePortType.Path);
        AssertOutput(node, "Bytes", NodePortType.Number);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void EdlRepairGpt_Ports_DeclareDeviceAndOptionalTooling()
    {
        var node = Create("Device.Edl.RepairGpt");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Programmer", NodePortType.Path, required: false);
        AssertInput(node, "LUN", NodePortType.Number, required: false);
        AssertOutput(node, "Result", NodePortType.Boolean);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void EdlUfsProvision_Ports_DeclareProfilePreviewAndProgrammer()
    {
        var node = Create("Device.Edl.UfsProvision");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Profile", NodePortType.Path, required: true);
        AssertInput(node, "Programmer", NodePortType.Path, required: false);
        AssertInput(node, "Preview", NodePortType.Boolean, required: false);
        AssertOutput(node, "Result", NodePortType.Boolean);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void EdlUfsReset_Ports_DeclareDeviceProgrammerAndConfirm()
    {
        var node = Create("Device.Edl.UfsReset");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Programmer", NodePortType.Path, required: false);
        AssertInput(node, "Confirm", NodePortType.Boolean, required: false);
        AssertOutput(node, "Result", NodePortType.Boolean);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void EdlReadPartition_Ports_DeclareOutputAndOptionalTooling()
    {
        var node = Create("Device.Edl.ReadPartition");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Partition", NodePortType.Text, required: true);
        AssertInput(node, "Output Path", NodePortType.Path, required: true);
        AssertInput(node, "Programmer", NodePortType.Path, required: false);
        AssertInput(node, "LUN", NodePortType.Number, required: false);
        AssertOutput(node, "Path", NodePortType.Path);
        AssertOutput(node, "Bytes", NodePortType.Number);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void RkdlFlash_Ports_DeclareLoaderPath()
    {
        var node = Create("Device.Rkdl.Flash");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Partition", NodePortType.Text, required: true);
        AssertInput(node, "Image", NodePortType.Path, required: true);
        AssertInput(node, "Loader", NodePortType.Path, required: false);
        AssertOutput(node, "Result", NodePortType.Boolean);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void RkdlReadPartition_Ports_DeclareParameterAndOutputPath()
    {
        var node = Create("Device.Rkdl.ReadPartition");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Partition", NodePortType.Text, required: true);
        AssertInput(node, "Parameter", NodePortType.Path, required: true);
        AssertInput(node, "Output Path", NodePortType.Path, required: true);
        AssertOutput(node, "Path", NodePortType.Path);
        AssertOutput(node, "Bytes", NodePortType.Number);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void RkdlWait_Ports_DeclareSerialTimeoutAndDeviceOutput()
    {
        var node = Create("Device.Rkdl.Wait");

        AssertInput(node, "Serial", NodePortType.Text, required: false);
        AssertInput(node, "Timeout", NodePortType.Number, required: false);
        AssertInput(node, "Interval", NodePortType.Number, required: false);
        AssertOutput(node, "Device", NodePortType.Device);
        AssertOutput(node, "Status", NodePortType.Text);
        AssertOutput(node, "Elapsed", NodePortType.Number);
    }

    [Fact]
    public void AdbPush_Ports_DeclareLocalAndRemotePaths()
    {
        var node = Create("Device.Adb.Push");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Local Path", NodePortType.Path, required: true);
        AssertInput(node, "Remote Path", NodePortType.Text, required: true);
        AssertOutput(node, "Result", NodePortType.Boolean);
        AssertOutput(node, "Bytes", NodePortType.Number);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void AdbPull_Ports_DeclareRemoteAndLocalPaths()
    {
        var node = Create("Device.Adb.Pull");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Remote Path", NodePortType.Text, required: true);
        AssertInput(node, "Local Path", NodePortType.Path, required: true);
        AssertOutput(node, "Path", NodePortType.Path);
        AssertOutput(node, "Bytes", NodePortType.Number);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Fact]
    public void WaitForDevice_Ports_DeclareProtocolAndTimeout()
    {
        var node = Create("Device.Wait.ForDevice");

        AssertInput(node, "Serial", NodePortType.Text, required: false);
        AssertInput(node, "Protocol", NodePortType.Text, required: false);
        AssertInput(node, "Timeout", NodePortType.Number, required: false);
        AssertInput(node, "Interval", NodePortType.Number, required: false);
        AssertOutput(node, "Device", NodePortType.Device);
        AssertOutput(node, "Status", NodePortType.Text);
        AssertOutput(node, "Elapsed", NodePortType.Number);
    }

    [Fact]
    public void FastbootGetVariable_Ports_DeclareNameAndValue()
    {
        var node = Create("Device.Fastboot.GetVariable");

        AssertInput(node, "Device", NodePortType.Device, required: true);
        AssertInput(node, "Name", NodePortType.Text, required: true);
        AssertOutput(node, "Value", NodePortType.Text);
        AssertOutput(node, "Status", NodePortType.Text);
    }

    [Theory]
    [InlineData("Device.Edl.Flash")]
    [InlineData("Device.Edl.Erase")]
    [InlineData("Device.Edl.Backup")]
    [InlineData("Device.Edl.RepairGpt")]
    [InlineData("Device.Edl.UfsProvision")]
    [InlineData("Device.Edl.UfsReset")]
    [InlineData("Device.Edl.ReadPartition")]
    [InlineData("Device.Rkdl.Flash")]
    [InlineData("Device.Rkdl.ReadPartition")]
    [InlineData("Device.Adb.Push")]
    [InlineData("Device.Adb.Pull")]
    [InlineData("Device.Fastboot.GetVariable")]
    public async Task ExecuteAsync_MissingDevice_FailsBeforeOpeningASession(string typeKey)
    {
        var node = Create(typeKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("no-device", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task EdlFlash_MissingImage_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Edl.Flash");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Edl));
        Seed(node, "Partition", "boot");
        Seed(node, "Image", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.img"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-image", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task EdlErase_MissingPartition_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Edl.Erase");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Edl));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-partition", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task EdlBackup_MissingOutputPath_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Edl.Backup");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Edl));
        Seed(node, "Partition", "boot");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-output", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task EdlUfsProvision_MissingProfile_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Edl.UfsProvision");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Edl));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-profile", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task EdlUfsReset_WriteWithoutConfirmation_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Edl.UfsReset");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Edl));
        Seed(node, "Confirm", false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("confirmation-required", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task EdlReadPartition_MissingOutputPath_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Edl.ReadPartition");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Edl));
        Seed(node, "Partition", "boot");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-output", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task RkdlFlash_MissingImage_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Rkdl.Flash");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Rkdl));
        Seed(node, "Partition", "boot");
        Seed(node, "Image", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.img"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-image", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task RkdlReadPartition_MissingParameter_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Rkdl.ReadPartition");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Rkdl));
        Seed(node, "Partition", "boot");
        Seed(node, "Parameter", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt"));
        Seed(node, "Output Path", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-parameter", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task AdbPush_MissingLocalPath_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Adb.Push");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Adb));
        Seed(node, "Local Path", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin"));
        Seed(node, "Remote Path", "/data/local/tmp/payload.bin");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-local-path", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task AdbPull_MissingRemotePath_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Adb.Pull");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Adb));
        Seed(node, "Local Path", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-remote-path", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task FastbootGetVariable_MissingName_FailsBeforeOpeningASession()
    {
        var node = Create("Device.Fastboot.GetVariable");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Fastboot));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("missing-name", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task FastbootGetVariable_NonFastbootDevice_FailsBeforeQuerying()
    {
        var node = Create("Device.Fastboot.GetVariable");
        Seed(node, "Device", FakeDevice(DeviceProtocolKind.Adb));
        Seed(node, "Name", "product");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("wrong-protocol", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task WaitForDevice_UnsupportedProtocol_FailsBeforePolling()
    {
        var node = Create("Device.Wait.ForDevice");
        Seed(node, "Protocol", "serial");

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Null(node.Output.Single(port => port.Title == "Device").Data);
    }

    [Fact]
    public async Task WaitForDevice_NonPositiveTimeout_FailsBeforePolling()
    {
        var node = Create("Device.Wait.ForDevice");
        Seed(node, "Protocol", "any");
        Seed(node, "Timeout", 0d);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-timeout", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task WaitForDevice_NonPositiveInterval_FailsBeforePolling()
    {
        var node = Create("Device.Wait.ForDevice");
        Seed(node, "Protocol", "any");
        Seed(node, "Timeout", 1d);
        Seed(node, "Interval", 0d);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-interval", node.Output.Single(port => port.Title == "Status").Data);
    }

    [Fact]
    public async Task RkdlWait_NonPositiveTimeout_FailsBeforePolling()
    {
        var node = Create("Device.Rkdl.Wait");
        Seed(node, "Timeout", 0d);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ExecuteAsync(new NodeContext()));

        Assert.Equal("invalid-timeout", node.Output.Single(port => port.Title == "Status").Data);
    }

    private static DeviceProtocolNodeSpec Spec(string typeKey) =>
        DeviceProtocolNodeSpecs.All.Single(spec => string.Equals(spec.TypeKey, typeKey, StringComparison.Ordinal));

    private static FlowNodeViewModel Create(string typeKey) => Spec(typeKey).Factory();

    private static void AssertInput(FlowNodeViewModel node, string title, NodePortType type, bool required)
    {
        var port = node.Input.Single(candidate => string.Equals(candidate.Title, title, StringComparison.Ordinal));
        Assert.Equal(type, port.PortType);
        Assert.Equal(required, port.IsRequired);
    }

    private static void AssertOutput(FlowNodeViewModel node, string title, NodePortType type)
    {
        var port = node.Output.Single(candidate => string.Equals(candidate.Title, title, StringComparison.Ordinal));
        Assert.Equal(type, port.PortType);
    }

    private static void Seed(FlowNodeViewModel node, string title, object? value)
    {
        var port = node.Input.Single(candidate => string.Equals(candidate.Title, title, StringComparison.Ordinal));
        port.InitialValue = value;
        port.Data = value;
    }

    private static DetectedDevice FakeDevice(DeviceProtocolKind protocol) => new()
    {
        Key = "test:" + protocol,
        Serial = "UOTAN-TEST",
        Protocol = protocol,
        Transport = DeviceTransportKind.Usb
    };

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