using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class LibUsbTopologyGraphTests
{
    [Fact]
    public void Build_DeviceOnSinglePortChain_EmitsBusRootAndParsesIdentityFields()
    {
        var nodes = ShapeAll(LibUsbTopologyGraph.Build(
        [
            NewDevice(1, [2], 0x22D9, 0x2769, bcdDevice: 0x0504, serial: "UOTAN1234567",
                speed: UsbDeviceSpeed.High)
        ]));

        var busRoot = Entry(nodes, "libusb-bus:1");
        var device = Entry(nodes, "libusb-port:1:2");
        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));

        Assert.Equal(2, nodes.Count);
        Assert.Equal(nodes.Count, rooted.Count);
        Assert.Same(busRoot, rooted[0]);
        Assert.Same(device, rooted[1]);
        Assert.Equal(UsbTopologyNodeKind.RootHub, busRoot.Kind);
        Assert.Equal("Bus 001", busRoot.Description);
        Assert.Null(busRoot.ParentInstanceId);
        Assert.Equal(busRoot.InstanceId, device.ParentInstanceId);
        Assert.Equal(UsbTopologyNodeKind.Device, device.Kind);
        Assert.Equal((ushort)0x22D9, device.VendorId);
        Assert.Equal((ushort)0x2769, device.ProductId);
        Assert.Equal("0504", device.Revision);
        Assert.Equal("UOTAN1234567", device.SerialNumber);
        Assert.Equal(UsbDeviceSpeed.High, device.Speed);
        Assert.Equal(["USB\\VID_22D9&PID_2769&REV_0504"], device.HardwareIds);
        Assert.Equal(["libusb-port:1:2"], device.LocationPaths);
        Assert.Null(device.Description);
        Assert.Null(device.InterfaceNumber);
        Assert.Null(device.Protocol);
    }

    [Fact]
    public void Build_ChainImpliesMissingHubLevels_EmitsInferredHubAncestors()
    {
        var nodes = ShapeAll(LibUsbTopologyGraph.Build(
        [
            NewDevice(2, [7, 2, 5], 0x0BDA, 0x8153)
        ]));

        var busRoot = Entry(nodes, "libusb-bus:2");
        var outerHub = Entry(nodes, "libusb-port:2:7");
        var innerHub = Entry(nodes, "libusb-port:2:7.2");
        var device = Entry(nodes, "libusb-port:2:7.2.5");
        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));

        Assert.Equal(4, nodes.Count);
        Assert.Equal(nodes.Count, rooted.Count);
        Assert.Same(busRoot, rooted[0]);
        Assert.Same(outerHub, rooted[1]);
        Assert.Same(innerHub, rooted[2]);
        Assert.Same(device, rooted[3]);
        Assert.Equal(UsbTopologyNodeKind.Hub, outerHub.Kind);
        Assert.Null(outerHub.Description);
        Assert.Equal(busRoot.InstanceId, outerHub.ParentInstanceId);
        Assert.Equal(UsbTopologyNodeKind.Hub, innerHub.Kind);
        Assert.Null(innerHub.Description);
        Assert.Equal(outerHub.InstanceId, innerHub.ParentInstanceId);
        Assert.Equal(innerHub.InstanceId, device.ParentInstanceId);
        Assert.Equal(UsbTopologyNodeKind.Device, device.Kind);
    }

    [Fact]
    public void Build_ObservedDeviceHostingDeeperChain_StatedAsHubOnSameBusOnly()
    {
        var nodes = ShapeAll(LibUsbTopologyGraph.Build(
        [
            NewDevice(1, [3], 0x2109, 0x2817),
            NewDevice(1, [3, 1], 0x045E, 0x00CB),
            NewDevice(2, [3, 1], 0x045E, 0x00CC)
        ]));

        var hosting = Entry(nodes, "libusb-port:1:3");
        var hosted = Entry(nodes, "libusb-port:1:3.1");
        var otherBusHubLevel = Entry(nodes, "libusb-port:2:3");
        var otherBusDevice = Entry(nodes, "libusb-port:2:3.1");

        Assert.Equal(6, nodes.Count);
        Assert.Equal(UsbTopologyNodeKind.Hub, hosting.Kind);
        Assert.Equal((ushort)0x2109, hosting.VendorId);
        Assert.Equal(hosting.InstanceId, hosted.ParentInstanceId);
        Assert.Equal(UsbTopologyNodeKind.Device, hosted.Kind);
        Assert.Equal(UsbTopologyNodeKind.Hub, otherBusHubLevel.Kind);
        Assert.Null(otherBusHubLevel.VendorId);
        Assert.Equal("libusb-bus:2", otherBusHubLevel.ParentInstanceId);
        Assert.Equal(otherBusHubLevel.InstanceId, otherBusDevice.ParentInstanceId);
    }

    [Fact]
    public void Build_AdbInterfaceTripleOnDevice_ClassifiesAdbProtocolThroughNodeCreation()
    {
        var device = NewDevice(1, [2], 0x18D1, 0x4E12,
            [NewInterface(0, 0xFF, 0x42, 0x01)], speed: UsbDeviceSpeed.High);
        var nodes = ShapeAll(LibUsbTopologyGraph.Build([device]));

        var function = Entry(nodes, "libusb-port:1:2&MI_00");

        Assert.Equal(3, nodes.Count);
        Assert.Equal(UsbTopologyNodeKind.Function, function.Kind);
        Assert.Equal("libusb-port:1:2", function.ParentInstanceId);
        Assert.Equal((byte)0, function.InterfaceNumber);
        Assert.Equal((byte?)0xFF, function.InterfaceClass);
        Assert.Equal((byte?)0x42, function.InterfaceSubClass);
        Assert.Equal((byte?)0x01, function.InterfaceProtocol);
        Assert.Equal((ushort)0x18D1, function.VendorId);
        Assert.Equal((ushort)0x4E12, function.ProductId);
        Assert.Equal("adb", function.Protocol);
        Assert.Equal(UsbDeviceSpeed.High, function.Speed);
        Assert.Equal(["USB\\VID_18D1&PID_4E12&MI_00"], function.HardwareIds);
    }

    [Fact]
    public void Build_MixedTopologyWithInterfacesAndUnchainableDevice_KeepsEveryNodeInFlattenedTree()
    {
        var nodes = ShapeAll(LibUsbTopologyGraph.Build(
        [
            NewDevice(1, [2], 0x22D9, 0x2769,
            [
                NewInterface(0, 0xFF, 0x42, 0x01),
                NewInterface(1, 0x08, 0x06, 0x50)
            ]),
            NewDevice(1, [2, 1], 0x045E, 0x00CB),
            NewDevice(1, [], 0x1234, 0x5678)
        ]));

        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));
        var fallback = Entry(nodes, "libusb-device:1:1234:5678");

        Assert.Equal(6, nodes.Count);
        Assert.Equal(nodes.Count, rooted.Count);
        Assert.Equal(nodes.Count,
            nodes.Select(node => node.InstanceId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("libusb-bus:1", fallback.ParentInstanceId);
        Assert.Equal(UsbTopologyNodeKind.Device, fallback.Kind);
        Assert.Equal((ushort)0x1234, fallback.VendorId);
    }

    [Fact]
    public void Build_DuplicateDeviceRecords_CollapseToOneDeviceNode()
    {
        var nodes = LibUsbTopologyGraph.Build(
        [
            NewDevice(1, [5], 0x0BDA, 0x8153),
            NewDevice(1, [5], 0x0BDA, 0x8153)
        ]);

        Assert.Equal(2, nodes.Count);
        Assert.Equal(1, nodes.Count(node => node.InstanceId == "libusb-port:1:5"));
    }

    [Fact]
    public void UsbTopologyTree_BuildWithDuplicateAndBlankInstanceIds_CollapsesToDistinctNodes()
    {
        var nodes = ShapeAll(LibUsbTopologyGraph.Build([NewDevice(1, [2], 0x22D9, 0x2769)]));
        var duplicate = Entry(nodes, "libusb-port:1:2");
        IReadOnlyList<UsbTopologyNode> withExtras =
        [
            .. nodes,
            duplicate,
            new UsbTopologyNode { InstanceId = " ", Kind = UsbTopologyNodeKind.Unknown }
        ];

        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(withExtras));

        Assert.Equal(4, withExtras.Count);
        Assert.Equal(nodes.Count, rooted.Count);
        Assert.Equal(nodes.Count,
            rooted.Select(node => node.InstanceId).Distinct(StringComparer.Ordinal).Count());
    }

    private static LibUsbTopologyDevice NewDevice(byte bus, IReadOnlyList<byte> chain, ushort vendorId,
        ushort productId, IReadOnlyList<LibUsbTopologyInterface>? interfaces = null,
        ushort? bcdDevice = null, string? serial = null, UsbDeviceSpeed speed = UsbDeviceSpeed.Unknown)
        => new()
        {
            BusNumber = bus,
            PortChain = chain,
            VendorId = vendorId,
            ProductId = productId,
            Interfaces = interfaces ?? [],
            BcdDevice = bcdDevice,
            Serial = serial,
            Speed = speed
        };

    private static LibUsbTopologyInterface NewInterface(byte number, byte cls, byte subClass,
        byte protocol)
        => new() { Number = number, Class = cls, SubClass = subClass, Protocol = protocol };

    private static IReadOnlyList<UsbTopologyNode> ShapeAll(IReadOnlyList<UsbTopologyNode> rawNodes)
        => [.. rawNodes.Select(UsbTopologyNodes.Create)];

    private static UsbTopologyNode Entry(IReadOnlyList<UsbTopologyNode> nodes, string instanceId)
        => nodes.First(node => string.Equals(node.InstanceId, instanceId, StringComparison.Ordinal));
}