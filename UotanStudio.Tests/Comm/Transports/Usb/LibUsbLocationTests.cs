using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class LibUsbLocationTests
{
    [Theory]
    [InlineData(1, new byte[] { 2 }, "libusb-port:1:2")]
    [InlineData(1, new byte[] { 2, 3 }, "libusb-port:1:2.3")]
    [InlineData(0, new byte[] { 255 }, "libusb-port:0:255")]
    [InlineData(255, new byte[] { 1, 2, 3, 4, 5, 6, 7 }, "libusb-port:255:1.2.3.4.5.6.7")]
    public void PortIdentity_ObservedBusAndChain_ProducesAddressIndependentLocation(byte bus, byte[] ports, string expected)
    {
        Assert.Equal(expected, LibUsbPortIdentity.Format(bus, ports));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 1, 0, 2 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void PortIdentity_MissingOrInvalidChain_DoesNotInventEvidence(byte[] ports)
    {
        Assert.Null(LibUsbPortIdentity.Format(1, ports));
    }

    [Theory]
    [InlineData("matched", true)]
    [InlineData("bus", false)]
    [InlineData("address", false)]
    [InlineData("vid", false)]
    [InlineData("pid", false)]
    [InlineData("path", false)]
    [InlineData("port", false)]
    [InlineData("missing-port", false)]
    public void MatchesSelectedLocation_MismatchedSelector_DoesNotFallBackToSameModel(string scenario, bool expected)
    {
        using var device = SelectedDevice();

        var matched = device.MatchesSelectedLocation(
            scenario == "bus" ? (byte)2 : (byte)1,
            scenario == "address" ? (byte)8 : (byte)7,
            scenario == "vid" ? (ushort)0x18D1 : (ushort)0x0E8D,
            scenario == "pid" ? (ushort)0x2002 : (ushort)0x2001,
            scenario == "path" ? "Bus 1 Device 8: 0E8D:2001" : device.DevicePath,
            scenario == "port" ? "libusb-port:1:2.4" : scenario == "missing-port" ? null : device.PhysicalPortPath);

        Assert.Equal(expected, matched);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchesSelectedLocation_ExplicitAddressOrPath_DoesNotRequireUnavailablePortMetadata(bool addressOnly)
    {
        using var device = SelectedDevice();
        device.PhysicalPortPath = null;
        var path = device.DevicePath;
        if (addressOnly)
        {
            device.DevicePath = string.Empty;
        }
        else
        {
            device.BusNumber = 0;
            device.DeviceAddress = 0;
        }

        Assert.True(device.MatchesSelectedLocation(1, 7, 0x0E8D, 0x2001, path, null));
        Assert.False(device.MatchesSelectedLocation(1, 8, 0x0E8D, 0x2001, "Bus 1 Device 8: 0E8D:2001", null));
    }

    [Fact]
    public void MatchesSelectedLocation_NoAddressOrPath_RejectsVidPidOnlySelection()
    {
        using var device = new LibUsbDevice { Vid = 0x0E8D, Pid = 0x2001 };

        Assert.False(device.MatchesSelectedLocation(1, 7, 0x0E8D, 0x2001, "Bus 1 Device 7: 0E8D:2001", "libusb-port:1:2.3"));
    }

    private static LibUsbDevice SelectedDevice() => new()
    {
        Vid = 0x0E8D,
        Pid = 0x2001,
        BusNumber = 1,
        DeviceAddress = 7,
        DevicePath = "Bus 1 Device 7: 0E8D:2001",
        PhysicalPortPath = "libusb-port:1:2.3"
    };
}