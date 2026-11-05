using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Usb;

/// <summary>
/// Verifies the pure pieces of the Windows topology speed resolution: the hub driver's
/// <c>USB_DEVICE_SPEED</c> byte mapping, and the node resolution that correlates devices with hub ports by
/// driver key (falling back to VID/PID) and lets composite interface children inherit their device node's
/// speed.
/// <para>验证 Windows 拓扑速度解析的纯逻辑部分：集线器驱动 <c>USB_DEVICE_SPEED</c> 字节的映射，以及
/// 「按驱动键把设备与集线器端口关联（回退 VID/PID）、复合接口子节点继承所属设备节点速度」的节点解析。</para>
/// </summary>
public sealed class WindowsUsbTopologySpeedTests
{
    private const string RootHubId = "USB\\ROOT_HUB30\\4&2C51F0B6&0&1";
    private const string DeviceKey = "{36fc9e60-c465-11cf-8056-444553540000}\\0007";

    [Theory]
    [InlineData((byte)0, UsbDeviceSpeed.Low)]
    [InlineData((byte)1, UsbDeviceSpeed.Full)]
    [InlineData((byte)2, UsbDeviceSpeed.High)]
    [InlineData((byte)3, UsbDeviceSpeed.Super)]
    [InlineData((byte)4, UsbDeviceSpeed.Unknown)]
    [InlineData((byte)255, UsbDeviceSpeed.Unknown)]
    public void FromPortSpeedByte_HubSpeedByte_MapsToSpeed(byte speedByte, UsbDeviceSpeed expected)
    {
        Assert.Equal(expected, UsbSpeedInference.FromPortSpeedByte(speedByte));
    }

    [Fact]
    public void ResolveNodeSpeeds_DeviceWithMatchingDriverKey_TakesPortSpeed()
    {
        var raws = new List<UsbDevNodeRawRecord>
        {
            CreateNode("USB\\VID_18D1&PID_4EE0\\ABC123", RootHubId, DeviceKey,
                ["USB\\VID_18D1&PID_4EE0&REV_0400"]),
            CreateNode("USB\\VID_3151&PID_4015\\10000030", RootHubId, "{class-guid}\\0042",
                ["USB\\VID_3151&PID_4015&REV_0100"])
        };
        var hubPorts = new List<HubPortSnapshot>
        {
            new(RootHubId, 2, 0x3151, 0x4015, UsbDeviceSpeed.High, "{class-guid}\\0042"),
            new(RootHubId, 3, 0x18D1, 0x4EE0, UsbDeviceSpeed.SuperPlus, DeviceKey)
        };

        var speeds = WindowsUsbTopologyProvider.ResolveNodeSpeeds(raws, hubPorts);

        Assert.Equal(UsbDeviceSpeed.SuperPlus, speeds["USB\\VID_18D1&PID_4EE0\\ABC123"]);
        Assert.Equal(UsbDeviceSpeed.High, speeds["USB\\VID_3151&PID_4015\\10000030"]);
    }

    [Fact]
    public void ResolveNodeSpeeds_DriverKeyMissing_FallsBackToVidPidOnParentHub()
    {
        var raws = new List<UsbDevNodeRawRecord>
        {
            CreateNode("USB\\VID_2207&PID_350B\\SER1", RootHubId, null,
                ["USB\\VID_2207&PID_350B&REV_0300"])
        };
        var hubPorts = new List<HubPortSnapshot>
        {
            new(RootHubId, 1, 0x046D, 0xC52B, UsbDeviceSpeed.Full, "{other-guid}\\0001"),
            new(RootHubId, 2, 0x2207, 0x350B, UsbDeviceSpeed.High, null)
        };

        var speeds = WindowsUsbTopologyProvider.ResolveNodeSpeeds(raws, hubPorts);

        Assert.Equal(UsbDeviceSpeed.High, speeds["USB\\VID_2207&PID_350B\\SER1"]);
    }

    [Fact]
    public void ResolveNodeSpeeds_CompositeInterfaceChild_InheritsDeviceSpeed()
    {
        var raws = new List<UsbDevNodeRawRecord>
        {
            CreateNode("USB\\VID_18D1&PID_4EE0\\ABC123", RootHubId, DeviceKey,
                ["USB\\VID_18D1&PID_4EE0&REV_0400"]),
            CreateNode("USB\\VID_18D1&PID_4EE0&MI_02\\ABC123&2", "USB\\VID_18D1&PID_4EE0\\ABC123",
                "{function-guid}\\0012", ["USB\\VID_18D1&PID_4EE0&MI_02"])
        };
        var hubPorts = new List<HubPortSnapshot>
        {
            new(RootHubId, 3, 0x18D1, 0x4EE0, UsbDeviceSpeed.Super, DeviceKey)
        };

        var speeds = WindowsUsbTopologyProvider.ResolveNodeSpeeds(raws, hubPorts);

        Assert.Equal(UsbDeviceSpeed.Super, speeds["USB\\VID_18D1&PID_4EE0\\ABC123"]);
        Assert.Equal(UsbDeviceSpeed.Super, speeds["USB\\VID_18D1&PID_4EE0&MI_02\\ABC123&2"]);
    }

    [Fact]
    public void ResolveNodeSpeeds_NodeWithoutUsbParentOrMatch_StaysUnknown()
    {
        var raws = new List<UsbDevNodeRawRecord>
        {
            CreateNode("PCI\\VEN_1022&DEV_1487\\4&38AB2C1&0&00", null, "{pci-guid}\\0001", []),
            CreateNode("USB\\ROOT_HUB30\\4&2C51F0B6&0&1", "PCI\\VEN_1022&DEV_1487\\4&38AB2C1&0&00",
                "{hub-guid}\\0001", []),
            CreateNode("USB\\VID_2207&PID_350B\\SER1", RootHubId, null,
                ["USB\\VID_2207&PID_350B&REV_0300"])
        };
        var hubPorts = new List<HubPortSnapshot>
        {
            new(RootHubId, 1, 0x046D, 0xC52B, UsbDeviceSpeed.Full, "{other-guid}\\0001")
        };

        var speeds = WindowsUsbTopologyProvider.ResolveNodeSpeeds(raws, hubPorts);

        Assert.Equal(UsbDeviceSpeed.Unknown, speeds["USB\\VID_2207&PID_350B\\SER1"]);
        Assert.DoesNotContain("PCI\\VEN_1022&DEV_1487\\4&38AB2C1&0&00", speeds.Keys);
        Assert.DoesNotContain("USB\\ROOT_HUB30\\4&2C51F0B6&0&1", speeds.Keys);
    }

    private static UsbDevNodeRawRecord CreateNode(string instanceId, string? parentInstanceId,
        string? driverKeyName, IReadOnlyList<string> hardwareIds) => new()
        {
            InstanceId = instanceId,
            ParentInstanceId = parentInstanceId,
            DriverKeyName = driverKeyName,
            HardwareIds = hardwareIds
        };
}