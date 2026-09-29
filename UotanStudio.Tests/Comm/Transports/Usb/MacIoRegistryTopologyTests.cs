using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.MacOS;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class MacIoRegistryTopologyTests
{
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    [Theory]
    [InlineData("AppleUSBXHCI", null, UsbTopologyNodeKind.HostController)]
    [InlineData("IOPCIDevice", null, UsbTopologyNodeKind.HostController)]
    [InlineData("IOUSBHostController", null, UsbTopologyNodeKind.HostController)]
    [InlineData("AppleUSBRootHub", null, UsbTopologyNodeKind.RootHub)]
    [InlineData("IOUSBHostInterface", null, UsbTopologyNodeKind.Function)]
    [InlineData("IOUSBInterface", null, UsbTopologyNodeKind.Function)]
    [InlineData("IOUSBHostDevice", (byte)0x09, UsbTopologyNodeKind.Hub)]
    [InlineData("IOUSBHostDevice", (byte)0x00, UsbTopologyNodeKind.Device)]
    [InlineData("IOUSBDevice", null, UsbTopologyNodeKind.Device)]
    public void ResolveKind_IoClassAndDeviceClassEvidence_StatesDocumentedRole(string ioClass, byte? deviceClass,
        UsbTopologyNodeKind expected)
        => Assert.Equal(expected, MacIoRegistryTopology.ResolveKind(ioClass, deviceClass));

    [Theory]
    [InlineData("High", UsbDeviceSpeed.High)]
    [InlineData("Full Speed", UsbDeviceSpeed.Full)]
    [InlineData("Low", UsbDeviceSpeed.Low)]
    [InlineData("Unknown", UsbDeviceSpeed.Unknown)]
    [InlineData("Super", UsbDeviceSpeed.Super)]
    [InlineData("SuperPlus", UsbDeviceSpeed.SuperPlus)]
    [InlineData("Super Speed Plus", UsbDeviceSpeed.SuperPlus)]
    [InlineData("480", UsbDeviceSpeed.High)]
    [InlineData("5000", UsbDeviceSpeed.Super)]
    [InlineData("", UsbDeviceSpeed.Unknown)]
    [InlineData(null, UsbDeviceSpeed.Unknown)]
    public void MapSpeed_KnownAndAbsentSpeedTokens_MapToDocumentedBuckets(string? text, UsbDeviceSpeed expected)
        => Assert.Equal(expected, MacIoRegistryTopology.MapSpeed(text));

    [Fact]
    public void Shape_DeviceBagWithVendorAndProduct_PublishesHexIdentifiers()
    {
        var device = Shape(new MacIoRegistryEntry
        {
            InstanceId = "/IODeviceTree:/usb/hub@010000/device@020000",
            IOClass = "IOUSBHostDevice",
            VendorId = 0x18D1,
            ProductId = 0x4E11,
            BcdDevice = 0x0504,
            SerialNumber = "UOTAN1234567",
            VendorName = "Google Inc.",
            ProductName = "Nexus Device",
            Speed = "High"
        });

        Assert.Equal(UsbTopologyNodeKind.Device, device.Kind);
        Assert.Equal((ushort)0x18D1, device.VendorId);
        Assert.Equal((ushort)0x4E11, device.ProductId);
        Assert.Equal("0504", device.Revision);
        Assert.Equal("UOTAN1234567", device.SerialNumber);
        Assert.Equal("Google Inc.", device.ManufacturerName);
        Assert.Equal("Nexus Device", device.Description);
        Assert.Equal("IOUSBHostDevice", device.ClassName);
        Assert.Equal(UsbDeviceSpeed.High, device.Speed);
        Assert.Equal(["usb:v18D1p4E11"], device.HardwareIds);
        Assert.Null(device.InterfaceClass);
        Assert.Null(device.InterfaceNumber);
        Assert.Null(device.Protocol);
    }

    [Fact]
    public void Shape_InterfaceBagWithAdbTripleInheritsVendorAndClassifiesAdbProtocol()
    {
        var device = new MacIoRegistryEntry
        {
            InstanceId = "/IODeviceTree:/usb/hub@010000/device@020000",
            IOClass = "IOUSBHostDevice",
            VendorId = 0x18D1,
            ProductId = 0x4E11,
            BcdDevice = 0x0504,
            SerialNumber = "UOTAN1234567",
            Speed = "High"
        };
        var function = new MacIoRegistryEntry
        {
            InstanceId = "/IODeviceTree:/usb/hub@010000/device@020000/interface@00",
            ParentInstanceId = device.InstanceId,
            IOClass = "IOUSBHostInterface",
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
            InterfaceProtocol = 0x01,
            InterfaceNumber = 0,
            InterfaceName = "ADB Interface"
        };

        var shaped = Shape(function, device);

        Assert.Equal(UsbTopologyNodeKind.Function, shaped.Kind);
        Assert.Equal((ushort)0x18D1, shaped.VendorId);
        Assert.Equal((ushort)0x4E11, shaped.ProductId);
        Assert.Equal("UOTAN1234567", shaped.SerialNumber);
        Assert.Equal(UsbDeviceSpeed.High, shaped.Speed);
        Assert.Equal((byte)0, shaped.InterfaceNumber);
        Assert.Equal((byte?)0xFF, shaped.InterfaceClass);
        Assert.Equal((byte?)0x42, shaped.InterfaceSubClass);
        Assert.Equal((byte?)0x01, shaped.InterfaceProtocol);
        Assert.Equal("ADB Interface", shaped.Description);
        Assert.Equal("IOUSBHostInterface", shaped.ClassName);
        Assert.Equal("adb", shaped.Protocol);
    }

    [Fact]
    public void Shape_InterfaceBagWithoutAdbTripleLeavesProtocolNull()
    {
        var device = new MacIoRegistryEntry
        {
            InstanceId = "/IODeviceTree:/usb/device@010000",
            IOClass = "IOUSBHostDevice",
            VendorId = 0x0BDA,
            ProductId = 0x8153
        };
        var function = new MacIoRegistryEntry
        {
            InstanceId = "/IODeviceTree:/usb/device@010000/interface@00",
            ParentInstanceId = device.InstanceId,
            IOClass = "IOUSBInterface",
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
            InterfaceProtocol = 0x05
        };

        var shaped = Shape(function, device);

        Assert.Equal(UsbTopologyNodeKind.Function, shaped.Kind);
        Assert.Equal((byte?)0x05, shaped.InterfaceProtocol);
        Assert.Null(shaped.Protocol);
    }

    [Fact]
    public void Shape_EmptyDeviceBagDegradesToNullsWithoutThrowing()
    {
        var node = Shape(new MacIoRegistryEntry { InstanceId = "/IODeviceTree:/usb/device@010000" });

        Assert.Equal(UsbTopologyNodeKind.Device, node.Kind);
        Assert.Null(node.ParentInstanceId);
        Assert.Null(node.VendorId);
        Assert.Null(node.ProductId);
        Assert.Null(node.Revision);
        Assert.Null(node.SerialNumber);
        Assert.Null(node.Description);
        Assert.Null(node.ManufacturerName);
        Assert.Null(node.ClassName);
        Assert.Null(node.InterfaceClass);
        Assert.Null(node.InterfaceSubClass);
        Assert.Null(node.InterfaceProtocol);
        Assert.Null(node.InterfaceNumber);
        Assert.Null(node.Protocol);
        Assert.Empty(node.HardwareIds);
        Assert.Equal(UsbDeviceSpeed.Unknown, node.Speed);
    }

    [Fact]
    public void Shape_InterfaceBagWithoutParentIdentityLeavesVendorAndProtocolNull()
    {
        var function = Shape(new MacIoRegistryEntry
        {
            InstanceId = "/IODeviceTree:/usb/orphan@01/interface@00",
            IOClass = "IOUSBHostInterface",
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
            InterfaceProtocol = 0x01
        });

        Assert.Equal(UsbTopologyNodeKind.Function, function.Kind);
        Assert.Null(function.VendorId);
        Assert.Null(function.Protocol);
        Assert.Equal((byte?)0xFF, function.InterfaceClass);
    }

    [Fact]
    public void Build_ControllerRootHubDeviceInterfaceChain_KeepsEveryNodeConnected()
    {
        var bags = new[]
        {
            new MacIoRegistryEntry
            {
                InstanceId = "/PCI0/ARPT/RP05/PXS0/HC17",
                IOClass = "AppleUSBXHCI"
            },
            new MacIoRegistryEntry
            {
                InstanceId = "/PCI0/ARPT/RP05/PXS0/HC17/AppleUSBRootHub",
                ParentInstanceId = "/PCI0/ARPT/RP05/PXS0/HC17",
                IOClass = "AppleUSBRootHub"
            },
            new MacIoRegistryEntry
            {
                InstanceId = "/PCI0/ARPT/RP05/PXS0/HC17/AppleUSBRootHub/device@010000",
                ParentInstanceId = "/PCI0/ARPT/RP05/PXS0/HC17/AppleUSBRootHub",
                IOClass = "IOUSBHostDevice",
                VendorId = 0x22D9,
                ProductId = 0x2769,
                Speed = "High"
            },
            new MacIoRegistryEntry
            {
                InstanceId = "/PCI0/ARPT/RP05/PXS0/HC17/AppleUSBRootHub/device@010000/interface@00",
                ParentInstanceId = "/PCI0/ARPT/RP05/PXS0/HC17/AppleUSBRootHub/device@010000",
                IOClass = "IOUSBHostInterface",
                InterfaceClass = 0xFF,
                InterfaceSubClass = 0x42,
                InterfaceProtocol = 0x01
            }
        };

        var nodes = ShapeAll(bags);
        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));

        Assert.Equal(bags.Length, rooted.Count);
        Assert.Equal(bags.Length, nodes.Count);
        Assert.Equal(1, rooted.Count(node => node.Kind == UsbTopologyNodeKind.HostController));
        var controller = rooted.First(node => node.Kind == UsbTopologyNodeKind.HostController);
        Assert.Null(controller.ParentInstanceId);
        var rootHub = rooted.First(node => node.Kind == UsbTopologyNodeKind.RootHub);
        Assert.Equal(controller.InstanceId, rootHub.ParentInstanceId);
        var device = rooted.First(node => node.Kind == UsbTopologyNodeKind.Device);
        Assert.Equal(rootHub.InstanceId, device.ParentInstanceId);
        var function = rooted.First(node => node.Kind == UsbTopologyNodeKind.Function);
        Assert.Equal(device.InstanceId, function.ParentInstanceId);
        Assert.Equal("adb", function.Protocol);
    }

    [Fact]
    public void ReadFlatNodes_RealMacIoRegistry_KeepsEveryNodeInBuiltTree()
    {
        Assert.SkipWhen(!IsMacOS,
            "Enumerating the I/O Registry USB plane for real requires a macOS host with a reachable IOKit framework.");

        var nodes = MacIoRegistryTopologyProvider.Instance.ReadFlatNodes()
            .Select(UsbTopologyNodes.Create)
            .ToArray();

        Assert.Equal(nodes.Length, nodes.Select(node => node.InstanceId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.InstanceId)));

        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));
        Assert.Equal(nodes.Length, rooted.Count);
    }

    private static IReadOnlyList<UsbTopologyNode> ShapeAll(IReadOnlyList<MacIoRegistryEntry> bags)
    {
        var byId = bags.ToDictionary(bag => bag.InstanceId, StringComparer.Ordinal);
        return [.. bags.Select(bag =>
        {
            var parent = bag.ParentInstanceId is { Length: > 0 } parentId &&
                byId.TryGetValue(parentId, out var parentBag)
                ? parentBag
                : null;
            return UsbTopologyNodes.Create(MacIoRegistryTopology.Shape(bag, parent));
        })];
    }

    private static UsbTopologyNode Shape(MacIoRegistryEntry bag, MacIoRegistryEntry? parent = null)
        => UsbTopologyNodes.Create(MacIoRegistryTopology.Shape(bag, parent));
}