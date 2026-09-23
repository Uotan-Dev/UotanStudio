using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public class EdlFactoryTests
{
    [Theory]
    [InlineData(0x05C6, 0x9008, true)]
    [InlineData(0x05C6, 0x900E, false)]
    [InlineData(0x05C6, 0x9025, false)]
    [InlineData(0x2207, 0x350B, false)]
    [InlineData(0x18D1, 0x4EE0, false)]
    [InlineData(0x0E8D, 0x0003, false)]
    public void Matches_DefaultIdentity_DoesNotInferEdlFromVendorInterface(int vendor, int product, bool expected)
    {
        var device = new UsbDeviceInfo
        {
            VendorId = (ushort)vendor,
            ProductId = (ushort)product,
            Interfaces = [new UsbInterfaceInfo { Class = 0xFF, SubClass = 1 }]
        };

        Assert.Equal(expected, new EdlFactory().Matches(device));
    }

    [Fact]
    public void Matches_ExplicitIdentity_RequiresBothIds()
    {
        var factory = new EdlFactory(0x1234, 0x5678);
        Assert.True(factory.Matches(new UsbDeviceInfo { VendorId = 0x1234, ProductId = 0x5678 }));
        Assert.False(factory.Matches(new UsbDeviceInfo { VendorId = 0x1234, ProductId = 0x9008 }));
        Assert.False(factory.Matches(new UsbDeviceInfo { VendorId = 0x05C6, ProductId = 0x5678 }));
    }

    [Fact]
    public void Matches_SubclassConstraint_DoesNotBroadenDefaultIdentity()
    {
        var factory = new EdlFactory(0);
        var device = new UsbDeviceInfo
        {
            VendorId = 0x2207,
            ProductId = 0x350B,
            Interfaces = [new UsbInterfaceInfo { Class = 0xFF, SubClass = 0 }]
        };
        Assert.False(factory.Matches(device));
        device.VendorId = 0x05C6;
        device.ProductId = 0x9008;
        Assert.True(factory.Matches(device));
        device.Interfaces = [];
        Assert.False(factory.Matches(device));
    }
}