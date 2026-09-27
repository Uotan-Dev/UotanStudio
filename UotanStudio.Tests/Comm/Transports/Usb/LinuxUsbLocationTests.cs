using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using Xunit;
using static UotanStudio.Comm.Transports.Usb.Backend.Linux.LinuxUsbAPI;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class LinuxUsbLocationTests
{
    [Fact]
    public void ConnectionInfo_UapiLayout_HasFixedSizeAndOffsets()
    {
        Assert.Equal(24, Marshal.SizeOf<Usbdevfs_conninfo_ex>());
        Assert.Equal(4, Marshal.OffsetOf<Usbdevfs_conninfo_ex>(nameof(Usbdevfs_conninfo_ex.BusNumber)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<Usbdevfs_conninfo_ex>(nameof(Usbdevfs_conninfo_ex.DeviceAddress)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<Usbdevfs_conninfo_ex>(nameof(Usbdevfs_conninfo_ex.PortCount)).ToInt32());
        Assert.Equal(17, Marshal.OffsetOf<Usbdevfs_conninfo_ex>(nameof(Usbdevfs_conninfo_ex.Port0)).ToInt32());
        Assert.Equal(23, Marshal.OffsetOf<Usbdevfs_conninfo_ex>(nameof(Usbdevfs_conninfo_ex.Port6)).ToInt32());
        Assert.Equal(0x80185520u, USBDEVFS_CONNINFO_EX);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23)]
    [InlineData(127)]
    public void Format_DeviceAddressChanges_PreservesPortIdentity(uint address)
    {
        var info = Info();
        info.DeviceAddress = address;

        Assert.Equal("linux-port:1:2.3", LinuxUsbPortIdentity.Format(info, $"/dev/bus/usb/001/{address:000}"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("root")]
    [InlineData("overflow")]
    [InlineData("zero-port")]
    [InlineData("zero-bus")]
    [InlineData("zero-address")]
    [InlineData("invalid-address")]
    [InlineData("other-bus")]
    [InlineData("other-address")]
    [InlineData("malformed-path")]
    public void Format_IncompleteOrMismatchedEvidence_ReturnsNull(string scenario)
    {
        var info = Info();
        var path = "/dev/bus/usb/001/007";
        switch (scenario)
        {
            case "short": info.Size = 23; break;
            case "root": info.PortCount = 0; break;
            case "overflow": info.PortCount = 8; break;
            case "zero-port": info.Port1 = 0; break;
            case "zero-bus": info.BusNumber = 0; break;
            case "zero-address": info.DeviceAddress = 0; break;
            case "invalid-address": info.DeviceAddress = 128; break;
            case "other-bus": path = "/dev/bus/usb/002/007"; break;
            case "other-address": path = "/dev/bus/usb/001/008"; break;
            case "malformed-path": path = "/dev/bus/usb/001/+7"; break;
        }

        Assert.Null(LinuxUsbPortIdentity.Format(info, path));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "linux-port:1:2.3", true)]
    [InlineData("linux-port:1:2.3", null, false)]
    [InlineData("linux-port:1:2.3", "linux-port:1:2.3", true)]
    [InlineData("linux-port:1:2.3", "linux-port:1:2.4", false)]
    [InlineData("linux-port:1:2.3", "linux-port:2:2.3", false)]
    public void Matches_OpenedHandleEvidence_RequiresSelectedPort(string? expected, string? observed, bool matches)
    {
        Assert.Equal(matches, LinuxUsbPortIdentity.Matches(expected, observed));
    }

    private static Usbdevfs_conninfo_ex Info() => new()
    {
        Size = 24,
        BusNumber = 1,
        DeviceAddress = 7,
        PortCount = 2,
        Port0 = 2,
        Port1 = 3
    };
}