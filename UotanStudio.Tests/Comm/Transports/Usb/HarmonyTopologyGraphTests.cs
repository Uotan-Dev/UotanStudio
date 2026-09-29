using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class HarmonyTopologyGraphTests
{
    [Fact]
    public void Shape_DeviceWithInterfaces_EmitsParentlessDeviceAndFunctionNodes()
    {
        var nodes = ShapeAll(HarmonyTopologyGraph.Shape(
        [
            NewDescriptor(7, 0x18D1, 0x4E12, bcdDevice: 0x0100, bcdUsb: 0x0200, interfaces:
            [
                NewInterface(0, 0xFF, 0x42, 0x01),
                NewInterface(1, 0xFF, 0x50, 0x01)
            ])
        ]));

        var device = Entry(nodes, "harmony-device:7");
        var adb = Entry(nodes, "harmony-device:7&MI_00");
        var hdc = Entry(nodes, "harmony-device:7&MI_01");

        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, node => Assert.Null(node.ParentInstanceId));
        Assert.Equal(UsbTopologyNodeKind.Device, device.Kind);
        Assert.Equal((ushort)0x18D1, device.VendorId);
        Assert.Equal((ushort)0x4E12, device.ProductId);
        Assert.Equal("0100", device.Revision);
        Assert.Null(device.SerialNumber);
        Assert.Equal(UsbDeviceSpeed.High, device.Speed);
        Assert.Equal(["USB\\VID_18D1&PID_4E12&REV_0100"], device.HardwareIds);
        Assert.Equal(UsbTopologyNodeKind.Function, adb.Kind);
        Assert.Equal((byte)0, adb.InterfaceNumber);
        Assert.Equal((byte?)0xFF, adb.InterfaceClass);
        Assert.Equal((byte?)0x42, adb.InterfaceSubClass);
        Assert.Equal((byte?)0x01, adb.InterfaceProtocol);
        Assert.Equal("adb", adb.Protocol);
        Assert.Equal(UsbTopologyNodeKind.Function, hdc.Kind);
        Assert.Equal((byte)1, hdc.InterfaceNumber);
        Assert.Equal((byte?)0x50, hdc.InterfaceSubClass);
        Assert.Equal("hdc", hdc.Protocol);
        Assert.Equal(UsbDeviceSpeed.High, hdc.Speed);
    }

    [Fact]
    public void Shape_DescriptorWithoutSerialOrConfiguration_DegradesToNullsWithoutThrowing()
    {
        var nodes = ShapeAll(HarmonyTopologyGraph.Shape([NewDescriptor(3, 0x0BDA, 0x8153)]));
        var device = Entry(nodes, "harmony-device:3");

        Assert.Single(nodes);
        Assert.Equal(UsbTopologyNodeKind.Device, device.Kind);
        Assert.Null(device.ParentInstanceId);
        Assert.Null(device.SerialNumber);
        Assert.Null(device.Revision);
        Assert.Null(device.InterfaceNumber);
        Assert.Null(device.InterfaceClass);
        Assert.Null(device.InterfaceSubClass);
        Assert.Null(device.InterfaceProtocol);
        Assert.Null(device.Protocol);
        Assert.Equal(UsbDeviceSpeed.Unknown, device.Speed);
        Assert.Equal(["USB\\VID_0BDA&PID_8153"], device.HardwareIds);
    }

    [Fact]
    public void Shape_HubDeviceClass_StatesHubKindForDeviceNode()
    {
        var nodes = ShapeAll(HarmonyTopologyGraph.Shape(
            [NewDescriptor(9, 0x04E5, 0x0610, deviceClass: 0x09)]));

        Assert.Equal(UsbTopologyNodeKind.Hub, nodes[0].Kind);
        Assert.Null(nodes[0].ParentInstanceId);
    }

    [Fact]
    public void Shape_MultipleDevices_KeepsEveryNodeAndShowsThemAllAsRoots()
    {
        var nodes = ShapeAll(HarmonyTopologyGraph.Shape(
        [
            NewDescriptor(1, 0x18D1, 0x4E12, interfaces: [NewInterface(0, 0xFF, 0x42, 0x01)]),
            NewDescriptor(2, 0x0BDA, 0x8153, deviceClass: 0x00)
        ]));

        var roots = UsbTopologyTree.Build(nodes);
        var rooted = UsbTopologyTree.Flatten(roots);

        Assert.Equal(3, nodes.Count);
        Assert.Equal(3, roots.Count);
        Assert.Equal(nodes.Count, rooted.Count);
        Assert.All(nodes, node => Assert.Null(node.ParentInstanceId));
        Assert.Equal(1, nodes.Count(node => node.Protocol == "adb"));
    }

    [Fact]
    public void Shape_DuplicateDeviceIds_CollapseToOneDeviceNode()
    {
        var nodes = HarmonyTopologyGraph.Shape(
        [
            NewDescriptor(4, 0x1111, 0x2222, interfaces: [NewInterface(0, 0xFF, 0x42, 0x01)]),
            NewDescriptor(4, 0x3333, 0x4444)
        ]);

        Assert.Equal(2, nodes.Count);
        Assert.Equal(1, nodes.Count(node => node.InstanceId == "harmony-device:4"));
        Assert.Equal((ushort)0x1111, nodes[0].VendorId);
    }

    [Fact]
    public void Shape_DuplicateInterfaceNumbers_CollapseToOneFunctionNode()
    {
        var nodes = HarmonyTopologyGraph.Shape([NewDescriptor(6, 0x18D1, 0x4E12, interfaces:
        [
            NewInterface(0, 0xFF, 0x42, 0x01),
            NewInterface(0, 0x08, 0x06, 0x50)
        ])]);

        Assert.Equal(2, nodes.Count);
        Assert.Equal(1, nodes.Count(node => node.InstanceId == "harmony-device:6&MI_00"));
        Assert.Equal((byte?)0xFF, nodes[1].InterfaceClass);
    }

    private static HarmonyTopologyDescriptor NewDescriptor(ulong deviceId, ushort vendorId,
        ushort productId, ushort? bcdDevice = null, ushort? bcdUsb = null, byte? deviceClass = null,
        string? serial = null, IReadOnlyList<HarmonyTopologyInterface>? interfaces = null)
        => new()
        {
            DeviceId = deviceId,
            VendorId = vendorId,
            ProductId = productId,
            BcdDevice = bcdDevice,
            BcdUsb = bcdUsb,
            DeviceClass = deviceClass,
            Serial = serial,
            Interfaces = interfaces ?? []
        };

    private static HarmonyTopologyInterface NewInterface(byte number, byte cls, byte subClass,
        byte protocol)
        => new() { Number = number, Class = cls, SubClass = subClass, Protocol = protocol };

    private static IReadOnlyList<UsbTopologyNode> ShapeAll(IReadOnlyList<UsbTopologyNode> rawNodes)
        => [.. rawNodes.Select(UsbTopologyNodes.Create)];

    private static UsbTopologyNode Entry(IReadOnlyList<UsbTopologyNode> nodes, string instanceId)
        => nodes.First(node => string.Equals(node.InstanceId, instanceId, StringComparison.Ordinal));
}