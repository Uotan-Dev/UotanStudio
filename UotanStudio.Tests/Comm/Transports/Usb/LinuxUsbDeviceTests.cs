using UotanStudio.Comm.Transports.Usb;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class LinuxUsbDeviceTests
{
    [Fact]
    public void ControlTransfer_LengthAboveSixteenBitWLength_ThrowsBeforeAnyHandleInspection()
    {
        var device = new LinuxUsbDevice();
        var buffer = new byte[65536];

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => device.ControlTransfer(new UsbSetupPacket { RequestType = 0xC0, Request = 0x06 }, buffer, 0, buffer.Length, 1000));

        Assert.Equal("length", exception.ParamName);
        Assert.Contains("65535", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WLength", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlTransfer_LengthAboveSixteenBitWLength_NullBufferStillRejectedByLengthGuard()
    {
        var device = new LinuxUsbDevice();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => device.ControlTransfer(new UsbSetupPacket { RequestType = 0xC0, Request = 0x06 }, null, 0, 65536, 1000));

        Assert.Equal("length", exception.ParamName);
    }

    [Fact]
    public void ControlTransfer_LengthAtSixteenBitLimit_PassesGuardAndReachesHandleCheck()
    {
        var device = new LinuxUsbDevice();
        var buffer = new byte[65535];

        Assert.Throws<UsbDeviceHandleClosedException>(
            () => device.ControlTransfer(new UsbSetupPacket { RequestType = 0xC0, Request = 0x06 }, buffer, 0, buffer.Length, 1000));
    }

    [Theory]
    [InlineData(UsbTransferPolicies.InfiniteTimeoutMs, 0u)]
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(5000, 5000u)]
    [InlineData(int.MaxValue, 2147483647u)]
    public void ConvertTimeoutMs_ManagedInfiniteSentinel_MapsToKernelWaitForever(int timeoutMs, uint expected)
        => Assert.Equal(expected, LinuxUsbDevice.ConvertTimeoutMs(timeoutMs));
}