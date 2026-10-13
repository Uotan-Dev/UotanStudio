using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices.Discovery;

public sealed class UsbProtocolClassifierBootromTableTests
{
    [Theory]
    [InlineData(0x05C6, 0x9006)]
    [InlineData(0x05C6, 0x9008)]
    [InlineData(0x05C6, 0x900E)]
    [InlineData(0x05C6, 0x901D)]
    [InlineData(0x05C6, 0x9025)]
    [InlineData(0x05C6, 0x9307)]
    [InlineData(0x0FCE, 0x9DDE)]
    [InlineData(0x0FCE, 0xADE3)]
    [InlineData(0x0FCE, 0xADE5)]
    [InlineData(0x0FCE, 0xADED)]
    [InlineData(0x1199, 0x9062)]
    [InlineData(0x1199, 0x9070)]
    [InlineData(0x1199, 0x9090)]
    [InlineData(0x0846, 0x68E0)]
    [InlineData(0x19D2, 0x0076)]
    public void Classify_QualcommEdlPortTableProductIds_ReturnsEdlBeforeInterfaceTriples(
        ushort vendorId, ushort productId)
    {
        Assert.Equal("edl", UsbProtocolClassifier.Classify(vendorId, productId, 0xFF, 0x42, 1));
        Assert.Equal("edl", UsbProtocolClassifier.Classify(BridgedDevice(vendorId, productId)));
    }

    [Theory]
    [InlineData(0x0E8D, 0x0003)]
    [InlineData(0x0E8D, 0x2000)]
    [InlineData(0x0E8D, 0x2001)]
    [InlineData(0x0E8D, 0x20FF)]
    [InlineData(0x0E8D, 0x3000)]
    [InlineData(0x1004, 0x6000)]
    [InlineData(0x22D9, 0x0006)]
    [InlineData(0x0FCE, 0xF200)]
    [InlineData(0x0FCE, 0xD1E9)]
    [InlineData(0x0FCE, 0xD1E2)]
    [InlineData(0x0FCE, 0xD1EC)]
    [InlineData(0x0FCE, 0xD1DD)]
    public void Classify_MediaTekBootAndOemDownloadPortProductIds_ReturnsMtkBeforeInterfaceTriples(
        ushort vendorId, ushort productId)
    {
        Assert.Equal("mtk", UsbProtocolClassifier.Classify(vendorId, productId, 0xFF, 0x42, 1));
        Assert.Equal("mtk", UsbProtocolClassifier.Classify(BridgedDevice(vendorId, productId)));
    }

    [Theory]
    [InlineData(0x0FCE, 0x9DDE, "edl")]
    [InlineData(0x0FCE, 0xADE3, "edl")]
    [InlineData(0x0FCE, 0xADE5, "edl")]
    [InlineData(0x0FCE, 0xADED, "edl")]
    [InlineData(0x0FCE, 0xF200, "mtk")]
    [InlineData(0x0FCE, 0xD1E9, "mtk")]
    [InlineData(0x0FCE, 0xD1E2, "mtk")]
    [InlineData(0x0FCE, 0xD1EC, "mtk")]
    [InlineData(0x0FCE, 0xD1DD, "mtk")]
    public void Classify_SameSonyVendorId_KeepsEdlAndMtkProductGroupsDisjoint(ushort vendorId,
        ushort productId, string expected)
    {
        Assert.Equal(expected, UsbProtocolClassifier.Classify(vendorId, productId, null, null, null));
    }

    [Fact]
    public void Classify_SonyEdlAndMtkBootromProductIdSets_HaveNoSharedMember()
    {
        var edlSonyProductIds = new ushort[] { 0x9DDE, 0xADE3, 0xADE5, 0xADED };
        var mtkSonyProductIds = new ushort[] { 0xF200, 0xD1E9, 0xD1E2, 0xD1EC, 0xD1DD };

        Assert.Empty(edlSonyProductIds.Intersect(mtkSonyProductIds));
    }

    [Theory]
    [InlineData(0x0FCE, 0x9DEF)]
    [InlineData(0x0FCE, 0xD1EE)]
    [InlineData(0x0FCE, 0x0003)]
    [InlineData(0x0FCE, 0x0076)]
    [InlineData(0x0E8D, 0x20FE)]
    [InlineData(0x0E8D, 0x2002)]
    [InlineData(0x0E8D, 0x3001)]
    [InlineData(0x0E8D, 0x6000)]
    [InlineData(0x05C6, 0x9DDE)]
    [InlineData(0x05C6, 0x9026)]
    [InlineData(0x1199, 0x9071)]
    [InlineData(0x0846, 0x68E2)]
    [InlineData(0x19D2, 0x0077)]
    [InlineData(0x1004, 0x6001)]
    [InlineData(0x22D9, 0x0003)]
    [InlineData(0x045E, 0x0003)]
    public void Classify_ProductIdOutsideEveryBootromTable_ReturnsNull(ushort vendorId, ushort productId)
    {
        Assert.Null(UsbProtocolClassifier.Classify(vendorId, productId, null, null, null));

        var device = new UsbDeviceInfo
        {
            VendorId = vendorId,
            ProductId = productId,
            InterfaceMetadataObserved = true,
            Interfaces = []
        };
        Assert.Null(UsbProtocolClassifier.Classify(device));
    }

    [Fact]
    public void Classify_MissingProductId_ReturnsNullForEveryBootromVendor()
    {
        Assert.Null(UsbProtocolClassifier.Classify(0x0FCE, null, null, null, null));
        Assert.Null(UsbProtocolClassifier.Classify(0x0E8D, null, null, null, null));
        Assert.Null(UsbProtocolClassifier.Classify(0x05C6, null, null, null, null));
        Assert.Null(UsbProtocolClassifier.Classify(0x19D2, null, null, null, null));
    }

    [Theory]
    [InlineData(0x05C6, 0x9025, "edl")]
    [InlineData(0x0FCE, 0xADE5, "edl")]
    [InlineData(0x1199, 0x9090, "edl")]
    [InlineData(0x0846, 0x68E0, "edl")]
    [InlineData(0x19D2, 0x0076, "edl")]
    [InlineData(0x0E8D, 0x20FF, "mtk")]
    [InlineData(0x0E8D, 0x3000, "mtk")]
    public void Classify_NewBootromProductIdWithFastbootInterface_ReturnsBootromProtocol(ushort vendorId,
        ushort productId, string expected)
    {
        Assert.Equal(expected, UsbProtocolClassifier.Classify(vendorId, productId, 0xFF, 0x42, 3));
        Assert.Equal(expected, UsbProtocolClassifier.Classify(vendorId, productId, 0xFF, 0x50, 1));

        var device = new UsbDeviceInfo
        {
            VendorId = vendorId,
            ProductId = productId,
            InterfaceMetadataObserved = true,
            Interfaces =
            [
                new UsbInterfaceInfo { Class = 0xFF, SubClass = 0x42, Protocol = 3 },
                new UsbInterfaceInfo { Class = 0xFF, SubClass = 0x50, Protocol = 1 }
            ]
        };
        Assert.Equal(expected, UsbProtocolClassifier.Classify(device));
    }

    [Fact]
    public void Classify_UnknownProductIdWithDebugBridgeInterface_StillReturnsThatBridge()
    {
        Assert.Equal("fastboot", UsbProtocolClassifier.Classify(0x0FCE, 0x9DEF, 0xFF, 0x42, 3));
        Assert.Equal("hdc", UsbProtocolClassifier.Classify(0x0E8D, 0x3001, 0xFF, 0x50, 1));
        Assert.Equal("adb", UsbProtocolClassifier.Classify(0x1199, 0x9071, 0xFF, 0x42, 1));
    }

    [Fact]
    public void Classify_RockchipBranch_KeepsMaskromAndLoaderProductIdsUnchanged()
    {
        Assert.Equal("rkdl", UsbProtocolClassifier.Classify(0x2207, 0x330C, null, null, null));
        Assert.Equal("rkdl", UsbProtocolClassifier.Classify(0x2207, 0x3001, null, null, null));
        Assert.Null(UsbProtocolClassifier.Classify(0x2207, 0x0000, null, null, null));
    }

    private static UsbDeviceInfo BridgedDevice(ushort vendorId, ushort productId) => new()
    {
        VendorId = vendorId,
        ProductId = productId,
        InterfaceMetadataObserved = true,
        Interfaces = [new UsbInterfaceInfo { Class = 0xFF, SubClass = 0x42, Protocol = 1 }]
    };
}