using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class LibUsbInterfaceClaimTests
{
    [Fact]
    public void SuccessfulClaim_DoesNotDetach()
    {
        Assert.True(LibUsbInterfaceClaim.TryClaim(() => true,
            () => throw new InvalidOperationException("Must not detach an owned interface."), out var error));
        Assert.Null(error);
    }

    [Fact]
    public void BusyClaim_UnsupportedDetach_PreservesClaimErrorAndStops()
    {
        var failure = new UsbException(Error.Busy);
        var claims = 0;
        var detaches = 0;
        Assert.False(LibUsbInterfaceClaim.TryClaim(() => { claims++; throw failure; },
            () => { detaches++; throw new UsbException(Error.NotSupported); }, out var error));
        Assert.Same(failure, error);
        Assert.Equal(4, claims);
        Assert.Equal(3, detaches);
    }

    [Fact]
    public void RetryAfterDetach_CanAcquireInterface()
    {
        var detached = false;
        Assert.True(LibUsbInterfaceClaim.TryClaim(() => detached,
            () => detached = true, out _));
    }

    [Fact]
    public void FalseClaimWithoutRecovery_IsFailure()
    {
        Assert.False(LibUsbInterfaceClaim.TryClaim(() => false, null, out var error));
        Assert.IsType<IOException>(error);
    }
}