using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Pins the two documented command-interface shapes of a MediaTek preloader: the vendor-specific 0xFF bulk pair
/// and the CDC Data class 0x0A pair a device publishes when it presents itself as a CDC-ACM composite device.
/// Requiring 0xFF alone rejected a real preloader whose data interface is 0x0A.
/// <para>固化联发科 preloader 的两种已记载命令接口形态：厂商专属 0xFF bulk 对，以及设备以 CDC-ACM 复合设备
/// 呈现时公布的 CDC Data 类 0x0A 对。只要求 0xFF 会把真实 preloader 拒之门外，其数据接口正是 0x0A。</para>
/// </summary>
public sealed class MtkCommandInterfaceShapeTests
{
    [Fact]
    public void Select_CdcDataBulkPair_IsSelected()
    {
        var device = NewCdcPreloader();

        var selection = MtkCommandInterfacePolicy.Select(device);

        Assert.Equal(MtkCommandInterfaceStatus.Selected, selection.Status);
        Assert.Equal((byte)0, selection.InterfaceNumber);
    }

    [Fact]
    public void Classify_CdcDataBulkPair_IsPreloaderStage()
    {
        var device = NewCdcPreloader();

        var classification = MtkModeClassifier.Classify(device);

        Assert.Equal(MtkUsbMode.Preloader, classification.Mode);
    }

    [Fact]
    public void Select_VendorBulkPair_StillSelects()
    {
        var device = new UsbDeviceInfo
        {
            DeviceKey = "vendor-shape",
            VendorId = 0x0E8D,
            ProductId = 0x2000,
            InterfaceMetadataObserved = true,
            Interfaces =
            [
                new UsbInterfaceInfo
                {
                    InterfaceNumber = 7,
                    Class = 0xFF,
                    SubClass = 0x00,
                    Protocol = 0x00,
                    Endpoints =
                    [
                        new UsbEndpointInfo { EndpointAddress = 0x01, Attributes = 0x02 },
                        new UsbEndpointInfo { EndpointAddress = 0x81, Attributes = 0x02 }
                    ]
                }
            ]
        };

        var selection = MtkCommandInterfacePolicy.Select(device);

        Assert.Equal(MtkCommandInterfaceStatus.Selected, selection.Status);
        Assert.Equal((byte)7, selection.InterfaceNumber);
    }

    private static UsbDeviceInfo NewCdcPreloader() => new()
    {
        DeviceKey = "cdc-preloader",
        VendorId = 0x0E8D,
        ProductId = 0x2000,
        InterfaceMetadataObserved = true,
        Interfaces =
        [
            new UsbInterfaceInfo
            {
                InterfaceNumber = 0,
                Class = 0x0A,
                Endpoints =
                [
                    new UsbEndpointInfo { EndpointAddress = 0x01, Attributes = 0x02, MaxPacketSize = 512 },
                    new UsbEndpointInfo { EndpointAddress = 0x81, Attributes = 0x02, MaxPacketSize = 512 }
                ]
            },
            new UsbInterfaceInfo
            {
                InterfaceNumber = 1,
                Class = 0x02,
                SubClass = 0x02,
                Protocol = 0x01,
                Endpoints =
                [
                    new UsbEndpointInfo { EndpointAddress = 0x83, Attributes = 0x03, MaxPacketSize = 64 }
                ]
            }
        ]
    };
}