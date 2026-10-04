using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkModeClassifierOemTests
{
    [Fact]
    public void Classify_LgPreloaderPort_ReturnsPreloaderAndNamesProductTable()
    {
        var result = MtkModeClassifier.Classify(OemDevice(0x1004, 0x6000));

        Assert.Equal(MtkUsbMode.Preloader, result.Mode);
        Assert.Contains("preloader port", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_OppoPreloaderPort_ReturnsPreloaderAndNamesProductTable()
    {
        var result = MtkModeClassifier.Classify(OemDevice(0x22D9, 0x0006));

        Assert.Equal(MtkUsbMode.Preloader, result.Mode);
        Assert.Contains("preloader port", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xF200)]
    [InlineData(0xD1E9)]
    [InlineData(0xD1E2)]
    [InlineData(0xD1EC)]
    [InlineData(0xD1DD)]
    public void Classify_SonyBootRomPort_ReturnsBootRom(ushort productId)
    {
        var result = MtkModeClassifier.Classify(OemDevice(0x0FCE, productId));

        Assert.Equal(MtkUsbMode.BootRom, result.Mode);
        Assert.Contains("bootrom port", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_OemVendorUnknownProductId_ReturnsUnknownAndNamesDocumentedPorts()
    {
        var result = MtkModeClassifier.Classify(OemDevice(0x0FCE, 0x9999));

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("not a documented OEM download port", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_NonOemNonMediaTekVendor_KeepsNotMediaTekVendorReason()
    {
        var result = MtkModeClassifier.Classify(OemDevice(0x05C6, 0x0003));

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("not the MediaTek vendor", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_MediaTekBootRomPath_IsUnchangedByOemResolution()
    {
        var result = MtkModeClassifier.Classify(new UsbDeviceInfo
        {
            VendorId = 0x0E8D,
            ProductId = 0x0003,
            InterfaceMetadataObserved = true,
            Interfaces = [new UsbInterfaceInfo
            {
                InterfaceNumber = 0,
                Class = 0xFF,
                SubClass = 0,
                Protocol = 0,
                Endpoints = [new UsbEndpointInfo
                {
                    EndpointAddress = 0x81,
                    Attributes = 0x02,
                    MaxPacketSize = 512
                }]
            }]
        });

        Assert.Equal(MtkUsbMode.BootRom, result.Mode);
        Assert.Contains("enumerated interface", result.Reason, StringComparison.Ordinal);
    }

    private static UsbDeviceInfo OemDevice(ushort vendorId, ushort productId) => new()
    {
        VendorId = vendorId,
        ProductId = productId,
        InterfaceMetadataObserved = false
    };
}