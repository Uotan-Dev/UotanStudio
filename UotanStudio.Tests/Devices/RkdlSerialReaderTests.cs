using UotanStudio.Comm.Devices.Discovery;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// RKDevTool-aligned display identity of Rockchip bootrom/loader rows: the parent node and every
/// interface child of one USB port must share one location suffix, while malformed ids without a
/// device-instance tail expose none.
/// <para>与 RKDevTool 对齐的瑞芯微 bootrom/loader 行显示身份：同一 USB 端口的父节点与每个接口
/// 子节点必须共享同一位置后缀，而缺少设备实例尾部的畸形 ID 不提供任何位置。</para>
/// </summary>
public sealed class RkdlSerialReaderTests
{
    [Theory]
    [InlineData(@"USB\VID_2207&PID_350B&MI_00\6&2F0A3B7&0&2&0000", "6&2F0A3B7&0&2")]
    [InlineData(@"USB\VID_2207&PID_350B&MI_01\6&2F0A3B7&0&2&0001", "6&2F0A3B7&0&2")]
    [InlineData(@"USB\VID_2207&PID_350B\6&2F0A3B7&0&2", "6&2F0A3B7&0&2")]
    public void InstanceLocationTail_ParentAndInterfaceChildren_MapToSamePortSuffix(
        string instanceId, string expected)
    {
        Assert.Equal(expected, RkdlSerialReader.InstanceLocationTail(instanceId));
    }

    [Fact]
    public void InstanceLocationTail_SingleSegmentTail_IsRejected()
    {
        Assert.Null(RkdlSerialReader.InstanceLocationTail(@"USB\VID_2207&PID_350B\6"));
    }

    [Fact]
    public void InstanceLocationTail_MissingTail_IsRejected()
    {
        Assert.Null(RkdlSerialReader.InstanceLocationTail(@"USB\VID_2207&PID_350B\"));
        Assert.Null(RkdlSerialReader.InstanceLocationTail(@"USB\VID_2207&PID_350B"));
    }

    [Theory]
    [InlineData("PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(1)#USB(3)", "1-3")]
    [InlineData("PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(1)#USB(1)#USB(1)", "1-1-1")]
    [InlineData("PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(1)#USB(2)#USBMI(0)", "1-2")]
    [InlineData("ACPI(_SB_)#ACPI(PCI0)#ACPI(XHC0)#ACPI(RHUB)#ACPI(PRT1)#USB(3)", null)]
    public void ParseRootChain_RootAnchoredPath_YieldsHubChain(string locationPath, string? expected)
    {
        Assert.Equal(expected, UsbLocationChain.ParseRootChain(locationPath));
    }

    [Fact]
    public void ParseRootChain_EmptyPath_IsRejected()
    {
        Assert.Null(UsbLocationChain.ParseRootChain(null));
        Assert.Null(UsbLocationChain.ParseRootChain(string.Empty));
    }
}