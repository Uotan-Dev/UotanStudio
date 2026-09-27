using UotanStudio.Comm.Transports.Usb.Backend.MacOS;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class MacUsbLocationTests
{
    [Theory]
    [InlineData(0x01100000L, "macos-port:01100000")]
    [InlineData(0xFA120000L, "macos-port:FA120000")]
    [InlineData(-99483648L, "macos-port:FA120000")]
    [InlineData(2147483648L, "macos-port:80000000")]
    [InlineData(-2147483648L, "macos-port:80000000")]
    public void Format_SignedOrUnsignedLocation_PreservesNativeBits(long value, string expected)
    {
        Assert.Equal(expected, MacUsbPortIdentity.Format(value));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(4294967296L)]
    [InlineData(-2147483649L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Format_MissingOrOutOfRangeLocation_RejectsTruncation(long value)
    {
        Assert.Null(MacUsbPortIdentity.Format(value));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "macos-port:01100000", true)]
    [InlineData("macos-port:01100000", null, false)]
    [InlineData("macos-port:01100000", "macos-port:01100000", true)]
    [InlineData("macos-port:01100000", "macos-port:01200000", false)]
    [InlineData("macos-port:01100000", "macos-port:02100000", false)]
    public void Matches_OpenedObjectLocation_RequiresSelectedPort(string? expected, string? observed, bool matches)
    {
        Assert.Equal(matches, MacUsbPortIdentity.Matches(expected, observed));
    }
}