using UotanStudio.Comm.Devices.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class CommDeviceTransportOpenerTests
{
    [Theory]
    [InlineData("usb", "Native", "direct-usb", "invalid-key")]
    [InlineData("adb-server@127.0.0.1:5037", "usb", "server", "serial")]
    [InlineData("hdc-server@127.0.0.1:8710", "hdc-server", "direct-usb", "key")]
    public async Task OpenAsync_InvalidRoute_RejectsWithoutOpening(string source, string transport, string route, string address)
    {
        var opener = new CommDeviceTransportOpener(null!);
        var endpoint = new DeviceEndpoint { Source = source, Transport = transport, Route = route, Address = address, Serial = address };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            opener.OpenAsync(endpoint, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_AlreadyCancelled_DoesNotOpen()
    {
        var opener = new CommDeviceTransportOpener(null!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var endpoint = new DeviceEndpoint { Source = "usb", Transport = UsbApiKind.Native.ToString(), Address = "key" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opener.OpenAsync(endpoint, cancellation.Token));
    }
}
