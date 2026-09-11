using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class ScreenMirrorTransportTests
{
    [Theory]
    [InlineData("selected", true)]
    [InlineData("other", false)]
    [InlineData("SELECTED", false)]
    [InlineData("", false)]
    public void Usb_UsesOnlyExactServerTarget(string serial, bool expected)
        => Assert.Equal(expected, ScreenMirrorViewModel.ShouldUseAdbServer(
            DeviceTransportKind.Usb, serial, () => ["selected", "second"]));

    [Fact]
    public void Usb_WithoutServer_UsesDirectSession()
        => Assert.False(ScreenMirrorViewModel.ShouldUseAdbServer(DeviceTransportKind.Usb, "selected", () => []));

    [Theory]
    [InlineData(DeviceTransportKind.Tcp, true)]
    [InlineData(DeviceTransportKind.Unknown, false)]
    public void ExistingRoutes_DoNotProbeServer(DeviceTransportKind transport, bool expected)
        => Assert.Equal(expected, ScreenMirrorViewModel.ShouldUseAdbServer(transport, "selected",
            () => throw new InvalidOperationException("Unexpected server probe.")));

    [Fact]
    public void ServerFailure_DoesNotSilentlyFallBackToUsb()
        => Assert.Throws<IOException>(() => ScreenMirrorViewModel.ShouldUseAdbServer(
            DeviceTransportKind.Usb, "selected", () => throw new IOException("Server disappeared.")));
}
