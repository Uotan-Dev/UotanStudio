using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class ScreenMirrorTransportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Usb_UsesServerPresenceRegardlessOfTargetList(bool present)
        => Assert.Equal(present, ScreenMirrorViewModel.ShouldUseAdbServer(DeviceTransportKind.Usb, () => present));

    [Theory]
    [InlineData(DeviceTransportKind.Tcp, true)]
    [InlineData(DeviceTransportKind.Unknown, false)]
    public void ExistingRoutes_DoNotProbeServer(DeviceTransportKind transport, bool expected)
        => Assert.Equal(expected, ScreenMirrorViewModel.ShouldUseAdbServer(transport,
            () => throw new InvalidOperationException("Unexpected server probe.")));

    [Fact]
    public void ServerFailure_DoesNotSilentlyFallBackToUsb()
        => Assert.Throws<IOException>(() => ScreenMirrorViewModel.ShouldUseAdbServer(
            DeviceTransportKind.Usb, () => throw new IOException("Server state is unknown.")));
}