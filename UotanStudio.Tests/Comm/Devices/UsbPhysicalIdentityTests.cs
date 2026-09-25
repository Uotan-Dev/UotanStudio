using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class UsbPhysicalIdentityTests
{
    private static UsbDeviceInfo AdbNode(Guid container, string instanceTail, string? serial = null) => new()
    {
        VendorId = 0x22D9,
        ProductId = 0x2769,
        InterfaceClass = 0xFF,
        InterfaceSubClass = 0x42,
        InterfaceProtocol = 0x01,
        InterfaceMetadataObserved = true,
        SerialNumber = serial,
        DeviceInstanceId = $@"USB\VID_22D9&PID_2769\{instanceTail}",
        ContainerId = container
    };

    [Fact]
    public void Of_SharedContainer_MergesInterfacesButNotOtherContainers()
    {
        var shared = Guid.NewGuid();
        var first = UsbPhysicalIdentity.Of(AdbNode(shared, "A"), null);
        var second = UsbPhysicalIdentity.Of(new UsbDeviceInfo
        {
            VendorId = 0x12D1,
            ProductId = 0x5000,
            ContainerId = shared
        }, null);
        var other = UsbPhysicalIdentity.Of(new UsbDeviceInfo
        {
            VendorId = 0x12D1,
            ProductId = 0x5000,
            ContainerId = Guid.NewGuid()
        }, null);

        Assert.Equal($"usb-container:{shared:N}", first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    public void Of_MissingContainerWithInvalidSerial_YieldsNull(string? serial)
    {
        Assert.Null(UsbPhysicalIdentity.Of(new UsbDeviceInfo { VendorId = 1, ProductId = 2 }, serial));
    }

    [Fact]
    public void Of_MissingContainerWithValidSerial_YieldsVidPidSerial()
    {
        Assert.Equal("usb:22D9:2769:SN123",
            UsbPhysicalIdentity.Of(new UsbDeviceInfo { VendorId = 0x22D9, ProductId = 0x2769 }, "SN123"));
    }

    [Fact]
    public void ServerPhysicalId_ExactPnpInstanceTail_UsesUniquePhysicalContainer()
    {
        var container = Guid.NewGuid();
        var infos = new[] { AdbNode(container, "B845EBDF"), AdbNode(Guid.NewGuid(), "OTHER") };

        Assert.Equal($"usb-container:{container:N}",
            UsbPhysicalIdentity.ServerPhysicalId(infos, "B845EBDF", "adb"));
        Assert.Null(UsbPhysicalIdentity.ServerPhysicalId(infos, "missing", "adb"));
        Assert.Null(UsbPhysicalIdentity.ServerPhysicalId(infos, "B845EBDF", "hdc"));
    }

    [Fact]
    public void ServerPhysicalId_TwoNodesShareInstanceTail_RejectsAmbiguousBind()
    {
        var infos = new[] { AdbNode(Guid.NewGuid(), "DUP"), AdbNode(Guid.NewGuid(), "DUP") };

        Assert.Null(UsbPhysicalIdentity.ServerPhysicalId(infos, "DUP", "adb"));
    }

    [Fact]
    public void ServerPhysicalId_ExactSerialMatch_BindsWhenUnique()
    {
        var container = Guid.NewGuid();
        var infos = new[] { AdbNode(container, "TAIL", serial: "SN1") };

        Assert.Equal($"usb-container:{container:N}",
            UsbPhysicalIdentity.ServerPhysicalId(infos, "SN1", "adb"));
    }
}