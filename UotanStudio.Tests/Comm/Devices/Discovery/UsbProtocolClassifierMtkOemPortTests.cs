using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices.Discovery;

public sealed class UsbProtocolClassifierMtkOemPortTests
{
    [Theory]
    [InlineData(0x0E8D, 0x0003)]
    [InlineData(0x0E8D, 0x2000)]
    [InlineData(0x0E8D, 0x2001)]
    [InlineData(0x1004, 0x6000)]
    [InlineData(0x22D9, 0x0006)]
    [InlineData(0x0FCE, 0xF200)]
    [InlineData(0x0FCE, 0xD1E9)]
    [InlineData(0x0FCE, 0xD1E2)]
    [InlineData(0x0FCE, 0xD1EC)]
    [InlineData(0x0FCE, 0xD1DD)]
    public void Classify_MtkBootOrOemDownloadPort_ReturnsMtkBeforeInterfaceTriples(ushort vendorId,
        ushort productId)
    {
        var device = new UsbDeviceInfo
        {
            VendorId = vendorId,
            ProductId = productId,
            InterfaceMetadataObserved = true,
            Interfaces = [new UsbInterfaceInfo { Class = 0xFF, SubClass = 0x42, Protocol = 1 }]
        };

        Assert.Equal("mtk", UsbProtocolClassifier.Classify(device));
        Assert.Equal("mtk", UsbProtocolClassifier.Classify(vendorId, productId, 0xFF, 0x42, 1));
    }

    [Theory]
    [InlineData(0x0E8D, 0x6000)]
    [InlineData(0x0E8D, 0x20FF)]
    [InlineData(0x0E8D, 0x3000)]
    [InlineData(0x1004, 0x6001)]
    [InlineData(0x22D9, 0x0003)]
    [InlineData(0x0FCE, 0x0003)]
    [InlineData(0x0FCE, 0xD1EE)]
    public void Classify_VendorProductIdOutsideDocumentedMtkSet_FallsBackToInterfaceOrUnknown(
        ushort vendorId, ushort productId)
    {
        Assert.Null(UsbProtocolClassifier.Classify(vendorId, productId, null, null, null));
    }

    [Fact]
    public void Classify_MediaTekBootRomSemantics_UnchangedForOtherProductIdsAndNullProduct()
    {
        Assert.Equal("mtk", UsbProtocolClassifier.Classify(0x0E8D, 0x0003, null, null, null));
        Assert.Null(UsbProtocolClassifier.Classify(0x0E8D, null, null, null, null));
        Assert.Equal("adb", UsbProtocolClassifier.Classify(0x0E8D, 0x0007, 0xFF, 0x42, 1));
    }

    [Fact]
    public void Classify_OppoPreloaderPort_DoesNotMatchOtherVendorBooleans()
    {
        Assert.Equal("rkdl", UsbProtocolClassifier.Classify(0x2207, 0x0100, null, null, null));
        Assert.Null(UsbProtocolClassifier.Classify(0x22D8, 0x0006, null, null, null));
    }

    [Theory]
    [InlineData(0x0E8D, 0x0003, true)]
    [InlineData(0x0E8D, 0x2000, true)]
    [InlineData(0x0E8D, 0x2001, true)]
    [InlineData(0x0E8D, 0x6000, false)]
    [InlineData(0x1004, 0x6000, true)]
    [InlineData(0x22D9, 0x0006, true)]
    [InlineData(0x0FCE, 0xF200, true)]
    [InlineData(0x0FCE, 0xD1E9, true)]
    [InlineData(0x0FCE, 0xD1E2, true)]
    [InlineData(0x0FCE, 0xD1EC, true)]
    [InlineData(0x0FCE, 0xD1DD, true)]
    [InlineData(0x0FCE, 0xF201, false)]
    [InlineData(0x1004, 0x6001, false)]
    public void Matches_MtkBootAndOemDownloadPorts_AcceptsOnlyDocumentedPairs(ushort vendorId,
        ushort productId, bool expected)
    {
        var factory = new MtkFactory();

        Assert.Equal(expected, factory.Matches(new UsbDeviceInfo { VendorId = vendorId, ProductId = productId }));
    }
}