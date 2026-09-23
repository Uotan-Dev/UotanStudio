using System.Collections.Immutable;
using UotanStudio.Comm.Devices.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class HeadlessControllerGatewayTests
{
    private static readonly DeviceEndpoint Server = new()
    {
        Source = "adb-server@127.0.0.1:5037", Transport = "adb-server", Address = "serial",
        Serial = "serial", Protocol = "adb", Route = "server"
    };

    private static readonly DeviceEndpoint Usb = new()
    {
        Source = "usb", Transport = "Native", Address = "exact-key",
        Serial = "serial", Protocol = "adb", Route = "direct-usb"
    };

    [Fact]
    public void SelectAdb_ServerPresent_SelectsExactServerRoute()
    {
        var snapshot = Snapshot(Server, Usb);

        Assert.Equal(Server.Id, HeadlessDeviceManagerGateway.SelectAdb(snapshot, "serial", true).Id);
        Assert.Equal(Usb.Id, HeadlessDeviceManagerGateway.SelectAdb(snapshot, "serial", false).Id);
    }

    [Fact]
    public void SelectAdb_ServerPresentWithoutTarget_DoesNotFallBackToUsb()
    {
        Assert.Throws<IOException>(() => HeadlessDeviceManagerGateway.SelectAdb(Snapshot(Usb), "serial", true));
    }

    [Fact]
    public void SelectAdb_DuplicateSerial_RejectsAmbiguousUsbOwnership()
    {
        var other = Usb with { Transport = "LibUsbDotNet", Address = "other-key" };

        Assert.Throws<IOException>(() => HeadlessDeviceManagerGateway.SelectAdb(Snapshot(Usb, other), "serial", false));
        Assert.Equal(Usb.Id, HeadlessDeviceManagerGateway.SelectAdb(Snapshot(Usb, other), "exact-key", false).Id);
    }

    [Fact]
    public void RunAdbService_ServerMissingTarget_DoesNotOpenUsb()
    {
        var opener = new RecordingOpener();
        var gateway = Gateway([Usb], opener, serverAvailable: true);

        Assert.Throws<IOException>(() => gateway.RunAdbService("serial", "shell:id", _ => true));
        Assert.Equal(0, opener.Calls);
    }

    [Fact]
    public void RunAdbService_AmbiguousUsbSerial_DoesNotOpenEitherEndpoint()
    {
        var opener = new RecordingOpener();
        var gateway = Gateway([Usb, Usb with { Address = "other-key", Transport = "LibUsbDotNet" }],
            opener, serverAvailable: false);

        Assert.Throws<IOException>(() => gateway.RunAdbService("serial", "shell:id", _ => true));
        Assert.Equal(0, opener.Calls);
    }

    private static HeadlessDeviceManagerGateway Gateway(DeviceEndpoint[] endpoints, RecordingOpener opener, bool serverAvailable)
        => new(new UotanStudio.Comm.CommKit(),
            new DeviceController([new Source(endpoints)], opener), () => serverAvailable, () => false);

    private static DeviceSnapshot Snapshot(params DeviceEndpoint[] endpoints)
        => new(1, ImmutableArray.Create(endpoints));

    private sealed class Source(DeviceEndpoint[] endpoints) : IDeviceDiscoverySource
    {
        public string Name => "usb";
        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeviceEndpoint>>(endpoints);
    }

    private sealed class RecordingOpener : IDeviceTransportOpener
    {
        public int Calls { get; private set; }
        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("No endpoint should be opened.");
        }
    }
}
