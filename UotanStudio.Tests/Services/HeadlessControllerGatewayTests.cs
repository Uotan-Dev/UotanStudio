using System.Collections.Immutable;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class HeadlessControllerGatewayTests
{
    private static readonly DeviceEndpoint Server = new()
    {
        Source = "adb-server@127.0.0.1:5037",
        Transport = "adb-server",
        Address = "serial",
        Serial = "serial",
        Protocol = "adb",
        Route = "server"
    };

    private static readonly DeviceEndpoint Usb = new()
    {
        Source = "usb",
        Transport = "Native",
        Address = "exact-key",
        Serial = "serial",
        Protocol = "adb",
        Route = "direct-usb",
        UsbInfo = new UsbDeviceInfo { SourceApiKind = UsbApiKind.Native, DeviceKey = "exact-key", SerialNumber = "serial" }
    };

    [Fact]
    public void SelectAdb_ServerPresent_SelectsExactServerRoute()
    {
        var snapshot = Snapshot(Server, Usb);

        Assert.Equal(Server.Id, HeadlessDeviceManagerGateway.SelectAdb(snapshot, "serial").Id);
    }

    [Fact]
    public void SelectAdb_ServerOwnsA_UsbBRemainsDirect()
    {
        var other = OtherUsb();
        var snapshot = Snapshot(Server, other);

        Assert.Equal(other.Id, HeadlessDeviceManagerGateway.SelectAdb(snapshot, "other").Id);
        Assert.Equal(Server.Id, HeadlessDeviceManagerGateway.SelectAdb(snapshot, "serial").Id);
        var gateway = Gateway([Server, other], new RecordingOpener());
        Assert.True(gateway.IsRoutableUsb(snapshot, other));
        Assert.False(gateway.IsRoutableUsb(Snapshot(Server, Usb), Usb));
    }

    [Fact]
    public void SelectAdb_DuplicateSerial_RejectsAmbiguousUsbOwnership()
    {
        var other = Usb with { Transport = "LibUsbDotNet", Address = "other-key" };

        Assert.Throws<IOException>(() => HeadlessDeviceManagerGateway.SelectAdb(Snapshot(Usb, other), "serial"));
        Assert.Equal(Usb.Id, HeadlessDeviceManagerGateway.SelectAdb(Snapshot(Usb, other), "exact-key").Id);
    }

    [Fact]
    public void SelectAdb_DifferentSerialCasing_DoesNotTransferServerOwnership()
    {
        var snapshot = Snapshot(Server with { Serial = "SERIAL", Address = "SERIAL" }, Usb);

        Assert.Equal(Usb.Id, HeadlessDeviceManagerGateway.SelectAdb(snapshot, "serial").Id);
    }

    [Fact]
    public void RunAdbService_ServerOwnsA_OpensUsbBExactly()
    {
        var opener = new RecordingOpener { ReturnTransport = true };
        var other = OtherUsb();
        var gateway = Gateway([Server, other], opener);

        Assert.Throws<InvalidOperationException>(() => gateway.RunAdbService("other", "shell:id", _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(other.Id, opener.LastEndpoint?.Id);
        Assert.True(opener.Transport.Disposed);
    }

    [Fact]
    public void RunAdbService_ServerOwnsSameSerial_OpensServerExactly()
    {
        var opener = new RecordingOpener { ReturnTransport = true };
        var gateway = Gateway([Usb, Server with { State = "unauthorized" }], opener);

        Assert.Throws<InvalidOperationException>(() => gateway.RunAdbService("serial", "shell:id", _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(Server.Id, opener.LastEndpoint?.Id);
        Assert.True(opener.Transport.Disposed);
    }

    [Fact]
    public void RunAdbService_ServerDiscoveryFails_DoesNotOpenUsb()
    {
        var opener = new RecordingOpener();
        var controller = new DeviceController(
            [new Source("usb", [Usb]), new FailingSource()], opener);
        var gateway = new HeadlessDeviceManagerGateway(controller, () => false);

        Assert.Throws<IOException>(() => gateway.RunAdbService("serial", "shell:id", _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(0, opener.Calls);
    }

    [Fact]
    public void ListUsbDevices_MixedRoutes_UsesSnapshotAndReturnsDefensiveCopies()
    {
        var other = OtherUsb();
        var gateway = Gateway([Server, Usb, other], new RecordingOpener());

        var info = Assert.Single(gateway.ListUsbDevices());
        Assert.Equal("other-key", info.DeviceKey);
        info.DeviceKey = "mutated";
        Assert.Equal("other-key", Assert.Single(gateway.ListUsbDevices()).DeviceKey);
    }

    [Fact]
    public void RunAdbService_AmbiguousUsbSerial_DoesNotOpenEitherEndpoint()
    {
        var opener = new RecordingOpener();
        var gateway = Gateway([Usb, Usb with { Address = "other-key", Transport = "LibUsbDotNet" }], opener);

        Assert.Throws<IOException>(() => gateway.RunAdbService("serial", "shell:id", _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(0, opener.Calls);
    }

    [Fact]
    public void RunAdbService_UnsupportedOpenedTransport_ReleasesLease()
    {
        var opener = new RecordingOpener { ReturnTransport = true };
        var gateway = new HeadlessDeviceManagerGateway(
            new DeviceController([new Source("adb-server@127.0.0.1:5037", [Server])], opener),
            () => false);

        Assert.Throws<InvalidOperationException>(() => gateway.RunAdbService("serial", "shell:id", _ => true, TestContext.Current.CancellationToken));
        Assert.True(opener.Transport.Disposed);
        Assert.Equal(1, opener.Calls);
    }

    private static HeadlessDeviceManagerGateway Gateway(DeviceEndpoint[] endpoints, RecordingOpener opener)
        => new(new DeviceController([
                new Source("usb", endpoints.Where(endpoint => endpoint.Source == "usb").ToArray()),
                new Source(Server.Source, endpoints.Where(endpoint => endpoint.Source == Server.Source).ToArray())
            ], opener), () => false);

    private static DeviceSnapshot Snapshot(params ReadOnlySpan<DeviceEndpoint> endpoints)
        => new(1, ImmutableArray.Create(endpoints));

    private static DeviceEndpoint OtherUsb()
        => Usb with
        {
            Serial = "other",
            Address = "other-key",
            UsbInfo = new UsbDeviceInfo { SourceApiKind = UsbApiKind.Native, DeviceKey = "other-key", SerialNumber = "other" }
        };

    private sealed class Source(string name, DeviceEndpoint[] endpoints) : IDeviceDiscoverySource
    {
        public string Name => name;
        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeviceEndpoint>>(endpoints);
    }

    private sealed class FailingSource : IDeviceDiscoverySource
    {
        public string Name => Server.Source;
        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
            => throw new IOException("ADB server protocol failure.");
    }

    private sealed class RecordingOpener : IDeviceTransportOpener
    {
        public int Calls { get; private set; }
        public bool ReturnTransport { get; init; }
        public DisposableTransport Transport { get; } = new();
        public DeviceEndpoint? LastEndpoint { get; private set; }
        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls++;
            LastEndpoint = endpoint;
            if (ReturnTransport)
            {
                return Task.FromResult<IAsyncDisposable>(Transport);
            }

            throw new InvalidOperationException("No endpoint should be opened.");
        }
    }

    private sealed class DisposableTransport : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}