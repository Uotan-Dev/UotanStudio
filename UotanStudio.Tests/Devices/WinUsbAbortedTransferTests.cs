using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class WinUsbAbortedTransferTests
{
    [Fact]
    public void IsAbortedTransferError_OperationAborted_ReturnsTrue()
        => Assert.True(WinUSBDevice.IsAbortedTransferError(995));

    [Fact]
    public void IsAbortedTransferError_TimeoutAndDisconnection_ReturnsFalse()
    {
        Assert.False(WinUSBDevice.IsAbortedTransferError(146));
        Assert.False(WinUSBDevice.IsAbortedTransferError(121));
        Assert.False(WinUSBDevice.IsAbortedTransferError(1167));
        Assert.False(WinUSBDevice.IsAbortedTransferError(0));
    }
}