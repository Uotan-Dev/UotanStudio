using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Pins the arrival-time handshake decision that captures a short-lived preloader: BROM, preloader and
/// not-yet-classified MTK download ports are handshaken immediately, while stages that must never receive
/// boot bytes are left alone.
/// <para>固化捕获短暂 preloader 的到达握手决策：BROM、preloader 与尚未分类的 MTK 下载端口立即握手，
/// 绝不能接收 boot 字节的阶段则跳过。</para>
/// </summary>
public sealed class MtkArrivalHandshakeTests
{
    [Fact]
    public void ShouldHandshakeArrival_BromPortByProductId_ReturnsTrue()
    {
        var device = NewUsb(0x0E8D, 0x0003);

        Assert.True(DeviceMonitorService.ShouldHandshakeArrival(device));
    }

    [Fact]
    public void ShouldHandshakeArrival_PreloaderWithoutInterfaceMetadata_ReturnsTrue()
    {
        var device = NewUsb(0x0E8D, 0x2000);

        Assert.True(DeviceMonitorService.ShouldHandshakeArrival(device));
    }

    [Fact]
    public void ShouldHandshakeArrival_LoggingOnlyPort_ReturnsFalse()
    {
        var device = NewUsb(0x0E8D, 0x2000);
        device.InterfaceMetadataObserved = true;
        device.Interfaces =
        [
            new UsbInterfaceInfo
            {
                Class = 0xFF,
                SubClass = 0x42,
                Endpoints =
                [
                    new UsbEndpointInfo { EndpointAddress = 0x81, Attributes = 0x02 },
                ]
            }
        ];

        Assert.False(DeviceMonitorService.ShouldHandshakeArrival(device));
    }

    [Fact]
    public void ShouldHandshakeArrival_NonMtkPort_ReturnsFalse()
    {
        Assert.False(DeviceMonitorService.ShouldHandshakeArrival(NewUsb(0x05C6, 0x9008)));
    }

    private static UsbDeviceInfo NewUsb(ushort vendorId, ushort productId) => new()
    {
        DeviceKey = $"usb-test-{vendorId:X4}-{productId:X4}",
        VendorId = vendorId,
        ProductId = productId,
    };
}