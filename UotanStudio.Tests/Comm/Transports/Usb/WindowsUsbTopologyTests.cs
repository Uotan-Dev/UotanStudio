using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class WindowsUsbTopologyTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public void ShapeNode_RootHubInstanceId_DerivesRootHubKind()
    {
        var shaped = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\ROOT_HUB30\5&1a2b3c&0&0",
            HardwareIds = [@"USB\ROOT_HUB30&8086&1000"],
            ClassName = "USB",
            DeviceDescription = "USB Root Hub (USB 3.0)"
        });

        Assert.Equal(UsbTopologyNodeKind.RootHub, shaped.Kind);
    }

    [Fact]
    public void ShapeNode_DeviceWithVidPid_DerivesDeviceKind()
    {
        var shaped = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769\5&1a2b3c&0&2",
            HardwareIds = [@"USB\VID_22D9&PID_2769"],
            ClassName = "USBDevice"
        });

        Assert.Equal(UsbTopologyNodeKind.Device, shaped.Kind);
    }

    [Fact]
    public void ShapeNode_FunctionInterface_DerivesFunctionKind()
    {
        var shaped = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769&MI_01\6&1a2b3c&0&0001",
            HardwareIds = [@"USB\VID_22D9&PID_2769&MI_01"]
        });

        Assert.Equal(UsbTopologyNodeKind.Function, shaped.Kind);
    }

    [Fact]
    public void ShapeNode_RevisionAndInterfaceMarker_DerivesIdentityAndInterfaceNumber()
    {
        var shaped = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769&REV_0504&MI_01\6&1a2b3c&0&0001",
            HardwareIds = [@"USB\VID_22D9&PID_2769&REV_0504&MI_01"]
        });

        Assert.Equal((ushort)0x22D9, shaped.VendorId);
        Assert.Equal((ushort)0x2769, shaped.ProductId);
        Assert.Equal((byte)1, shaped.InterfaceNumber);
        Assert.Equal("0504", shaped.Revision);
    }

    [Fact]
    public void ShapeNode_AdbCompatibleTriple_ClassifiesAdbProtocol()
    {
        var shaped = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769&MI_01\6&1a2b3c&0&0001",
            HardwareIds = [@"USB\VID_22D9&PID_2769&MI_01"],
            CompatibleIds = [@"USB\Class_ff&SubClass_42&Prot_01"]
        });

        Assert.Equal((byte?)0xFF, shaped.InterfaceClass);
        Assert.Equal((byte?)0x42, shaped.InterfaceSubClass);
        Assert.Equal((byte?)0x01, shaped.InterfaceProtocol);
        Assert.Equal("adb", shaped.Protocol);
    }

    [Fact]
    public void ShapeNode_DescriptionFallsBackToFriendlyThenBusThenDevice()
    {
        var deviceOnly = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769\5&1a2b3c&0&2",
            DeviceDescription = "USB Serial Device"
        });
        var busReported = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769\5&1a2b3c&0&2",
            DeviceDescription = "USB Serial Device",
            BusReportedDeviceDescription = "Uotan ADB Device"
        });
        var friendly = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769\5&1a2b3c&0&2",
            DeviceDescription = "USB Serial Device",
            BusReportedDeviceDescription = "Uotan ADB Device",
            FriendlyName = "My Phone"
        });

        Assert.Equal("USB Serial Device", deviceOnly.Description);
        Assert.Equal("Uotan ADB Device", busReported.Description);
        Assert.Equal("My Phone", friendly.Description);
    }

    [Fact]
    public void RegistersAdbInterfaceClass_GuidWithoutAdbClass_ReportsFalse()
    {
        var shaped = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769&MI_01\6&1a2b3c&0&0001",
            HardwareIds = [@"USB\VID_22D9&PID_2769&MI_01"],
            DeviceInterfaceGuids = [Guid.NewGuid()]
        });

        Assert.False(UsbTopologyNodes.RegistersAdbInterfaceClass(shaped));
    }

    [Fact]
    public void RegistersAdbInterfaceClass_GuidWithAdbClass_ReportsTrue()
    {
        var shaped = Shape(new UsbDevNodeRawRecord
        {
            InstanceId = @"USB\VID_22D9&PID_2769&MI_01\6&1a2b3c&0&0001",
            HardwareIds = [@"USB\VID_22D9&PID_2769&MI_01"],
            DeviceInterfaceGuids = [Guid.NewGuid(), UsbTopologyNodes.AdbInterfaceClass]
        });

        Assert.True(UsbTopologyNodes.RegistersAdbInterfaceClass(shaped));
    }

    [Fact(Skip = "Windows PnP device tree is required.", SkipUnless = nameof(IsWindows))]
    public void ReadFlatNodes_RealWindowsMachine_SurfacesPnpDataAndKeepsEveryNodeInTree()
    {
        var nodes = WindowsUsbTopologyProvider.Instance.ReadFlatNodes()
            .Select(UsbTopologyNodes.Create)
            .ToArray();

        Assert.SkipWhen(nodes.Length == 0, "No Windows PnP USB device nodes are exposed on this host.");
        Assert.NotEmpty(nodes);
        Assert.Contains(nodes, node => !string.IsNullOrWhiteSpace(node.ClassName));
        Assert.Contains(nodes, node => !string.IsNullOrWhiteSpace(node.Description));

        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));
        Assert.Equal(nodes.Length, rooted.Count);
    }

    private static UsbTopologyNode Shape(UsbDevNodeRawRecord raw)
        => UsbTopologyNodes.Create(WindowsUsbTopologyProvider.ShapeNode(raw));
}