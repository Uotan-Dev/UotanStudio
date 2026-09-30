using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class WinUsbHandleLifetimeTests
{
    [Fact]
    public void ReadChunk_HandleNeverOpened_ThrowsHandleClosedBeforeNativeCall()
    {
        var probe = new ClosedHandleProbe();

        Assert.Throws<UsbDeviceHandleClosedException>(probe.InvokeReadChunk);
    }

    [Fact]
    public void WriteChunk_AfterDispose_ThrowsHandleClosedBeforeNativeCall()
    {
        var probe = new ClosedHandleProbe();
        probe.Dispose();

        Assert.Throws<UsbDeviceHandleClosedException>(probe.InvokeWriteChunk);
    }

    [Fact]
    public void GetSerialNumber_AfterDispose_ThrowsHandleClosedInsteadOfReturningError()
    {
        var device = new WinUSBDevice();
        device.Dispose();

        Assert.Throws<UsbDeviceHandleClosedException>(() => device.GetSerialNumber());
    }

    [Fact]
    public void ControlTransfer_HandleNeverOpened_ThrowsHandleClosed()
    {
        var device = new WinUSBDevice();

        Assert.Throws<UsbDeviceHandleClosedException>(() => device.ControlTransfer(default, null, 0, 0, 1_000));
    }

    private sealed class ClosedHandleProbe : WinUSBDevice
    {
        public void InvokeReadChunk() => ReadChunk(IntPtr.Zero, 4, 1_000);

        public void InvokeWriteChunk() => WriteChunk(IntPtr.Zero, 4, 1_000);
    }
}