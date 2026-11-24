using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Devices;

public class FastBootPortEnumeratorTests
{
    [Theory]
    [InlineData("USB\\VID_0E8D&PID_2000\\123", 0x0E8D, 0x2000)]
    [InlineData("USB\\VID_1782&PID_4D00\\abc", 0x1782, 0x4D00)]
    [InlineData("USB\\vid_05c6&pid_9008\\def", 0x05C6, 0x9008)]
    public void TryParseVidPid_KnownHardwareId_ParsesBothIds(string hardwareId, ushort expectedVid, ushort expectedPid)
    {
        Assert.True(FastBootPortEnumerator.TryParseVidPid(hardwareId, out var vendorId, out var productId));
        Assert.Equal(expectedVid, vendorId);
        Assert.Equal(expectedPid, productId);
    }

    [Theory]
    [InlineData("USB\\VID_0E8D\\123")]
    [InlineData("USB\\PID_2000\\123")]
    [InlineData("")]
    public void TryParseVidPid_MalformedId_Rejects(string hardwareId)
    {
        Assert.False(FastBootPortEnumerator.TryParseVidPid(hardwareId, out _, out _));
    }
}