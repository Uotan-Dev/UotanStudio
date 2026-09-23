using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public class DeviceCommunicationConfigurationTests
{
    [Theory]
    [InlineData(UsbApiKind.Native, "native")]
    [InlineData(UsbApiKind.LibUsbDotNet, "libusb")]
    public void EnumerateDevices_ForcedBackend_OnlyCallsSelectedProvider(UsbApiKind kind, string expected)
    {
        var calls = new List<string>();
        var registry = new UsbApiRegistry();
        registry.Register("native", () => new RecordingProvider("native", calls));
        registry.Register("libusb", () => new RecordingProvider("libusb", calls));
        var layer = new UsbCommunicationLayer(registry, new() { UsbBackend = kind });

        layer.EnumerateDevices(UsbApiKind.Auto, null, TestContext.Current.CancellationToken);

        Assert.Equal([expected], calls);
    }

    [Theory]
    [InlineData(UsbApiKind.Native, "libusb")]
    [InlineData(UsbApiKind.LibUsbDotNet, "native")]
    public void EnumerateDevices_ForcedBackendMissing_DoesNotFallBack(UsbApiKind kind, string available)
    {
        var calls = new List<string>();
        var registry = new UsbApiRegistry();
        registry.Register(available, () => new RecordingProvider(available, calls));
        var layer = new UsbCommunicationLayer(registry, new() { UsbBackend = kind });

        Assert.Empty(layer.EnumerateDevices(UsbApiKind.Auto, null, TestContext.Current.CancellationToken));
        Assert.Empty(calls);
    }

    [Fact]
    public void EnumerateDevices_ExplicitApi_OverridesConfiguredDefault()
    {
        var calls = new List<string>();
        var registry = new UsbApiRegistry();
        registry.Register("native", () => new RecordingProvider("native", calls));
        registry.Register("libusb", () => new RecordingProvider("libusb", calls));
        var layer = new UsbCommunicationLayer(registry, new() { UsbBackend = UsbApiKind.LibUsbDotNet });

        layer.EnumerateDevices(UsbApiKind.Native, null, TestContext.Current.CancellationToken);

        Assert.Equal(["native"], calls);
    }

    [Theory]
    [InlineData("{}", UsbApiKind.Auto)]
    [InlineData("{\"deviceCommunication\":null}", UsbApiKind.Auto)]
    [InlineData("{\"deviceCommunication\":{\"usbBackend\":999}}", UsbApiKind.Auto)]
    [InlineData("{\"deviceCommunication\":{\"usbBackend\":2}}", UsbApiKind.LibUsbDotNet)]
    public void Deserialize_OldNullOrInvalidSettings_UsesCompatiblePolicy(string json, UsbApiKind expected)
    {
        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(expected, settings.DeviceCommunication.UsbBackend);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings), AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(expected, restored.DeviceCommunication.UsbBackend);
    }

    [Fact]
    public async Task IsListeningAsync_RefusedThenListening_DoesNotCacheAbsence()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.False(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
        using var restarted = new TcpListener(IPAddress.Loopback, port);
        restarted.Start();
        Assert.True(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsListeningAsync_Canceled_PropagatesCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DebugBridgeRoute.IsListeningAsync("127.0.0.1", 5037, cancellation.Token));
    }

    [Fact]
    public async Task IsListeningAsync_InvalidEndpoint_DoesNotReportAbsence()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            DebugBridgeRoute.IsListeningAsync("127.0.0.1", -1, TestContext.Current.CancellationToken));
    }

    private sealed class RecordingProvider(string name, List<string> calls) : IUsbApiProvider
    {
        public string ApiName => name;
        public UsbApiKind ApiKind => name == "native" ? UsbApiKind.Native : UsbApiKind.LibUsbDotNet;
        public bool IsSupportedOnCurrentPlatform => true;
        public IReadOnlyList<UsbDeviceInfo> EnumerateDeviceInfos(UsbDeviceFilter? filter = null)
        {
            calls.Add(name);
            return [];
        }
        public IReadOnlyList<IUsbDeviceSession> EnumerateDeviceSessions(UsbDeviceFilter? filter = null) => [];
        public UsbApiCapabilities GetCapabilities() => new() { ApiName = name, ApiKind = ApiKind, IsSupportedOnCurrentPlatform = true };
    }
}