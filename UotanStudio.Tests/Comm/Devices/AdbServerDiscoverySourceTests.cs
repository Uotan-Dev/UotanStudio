using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Routing;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class AdbServerDiscoverySourceTests
{
    private const string MdnsListing =
        "192.168.1.169:37701      device product:LE2110 model:LE2110 device:OnePlus9 transport_id:5\n" +
        "adb-b845ebdf-lkQzC4._adb-tls-connect._tcp. device product:LE2110 model:LE2110 device:OnePlus9 transport_id:4\n";

    [Fact]
    public async Task DiscoverAsync_EmulatorAndUsbTargets_CarryServerLinkClass()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        var commands = new List<string>();
        var serving = ServeAsync(listener,
            "emulator-5554          device product:Amber model:ABR_AL80 device:Amber transport_id:5\n" +
            "0A1B2C3D4E5F6789       device usb:1-2 product:pantah model:Pixel_7 transport_id:3\n",
            null, null, 1, commands);

        var endpoints = await new AdbServerDiscoverySource("127.0.0.1", port)
            .DiscoverAsync(TestContext.Current.CancellationToken);
        await serving;

        Assert.Equal(["emulator-5554", "0A1B2C3D4E5F6789"], endpoints.Select(endpoint => endpoint.Address));
        Assert.Equal(["TCP", "USB"], endpoints.Select(endpoint => endpoint.TargetTransport));
        Assert.All(endpoints, endpoint => Assert.Equal("server", endpoint.Route));
        Assert.Equal(["host:devices-l"], commands);
    }

    [Fact]
    public async Task DiscoverAsync_ListingWithoutLinkEvidence_LeavesTargetTransportUnreported()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        var serving = ServeAsync(listener, "8fad1670             offline transport_id:7\n", null, null, 1, []);

        var endpoints = await new AdbServerDiscoverySource("127.0.0.1", port)
            .DiscoverAsync(TestContext.Current.CancellationToken);
        await serving;

        var endpoint = Assert.Single(endpoints);
        Assert.Null(endpoint.TargetTransport);
        Assert.Null(endpoint.AliasAddress);
        Assert.Equal("offline", endpoint.State);
    }

    [Fact]
    public async Task DiscoverAsync_MdnsKeyedTarget_CarriesResolvedAliasAndNetworkLink()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        var serving = ServeAsync(listener, MdnsListing,
            "adb-b845ebdf-lkQzC4._adb-tls-connect._tcp\t_adb-tls-connect._tcp\t192.168.1.169:37701\n",
            null, 2, []);

        var endpoints = await new AdbServerDiscoverySource("127.0.0.1", port)
            .DiscoverAsync(TestContext.Current.CancellationToken);
        await serving;

        var keyed = Assert.Single(endpoints,
            endpoint => endpoint.Address.StartsWith("adb-", StringComparison.Ordinal));
        Assert.Equal("192.168.1.169:37701", keyed.AliasAddress);
        Assert.Equal("TCP", keyed.TargetTransport);
        var dialled = Assert.Single(endpoints,
            endpoint => endpoint.Address == "192.168.1.169:37701");
        Assert.Null(dialled.AliasAddress);
    }

    [Fact]
    public async Task DiscoverAsync_UnsupportedMdnsTable_LeavesMdnsTargetUnresolved()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        var serving = ServeAsync(listener, MdnsListing, null, "unknown host service", 2, []);

        var endpoints = await new AdbServerDiscoverySource("127.0.0.1", port)
            .DiscoverAsync(TestContext.Current.CancellationToken);
        await serving;

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Null(endpoint.AliasAddress));
        var keyed = Assert.Single(endpoints,
            endpoint => endpoint.Address.StartsWith("adb-", StringComparison.Ordinal));
        Assert.Null(keyed.TargetTransport);
    }

    [Fact]
    public async Task DiscoverAsync_UsbOnlyListing_NeverQueriesMdnsTable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        var commands = new List<string>();
        var serving = ServeAsync(listener,
            "0A1B2C3D4E5F6789       device usb:1-2 product:pantah model:Pixel_7 transport_id:3\n",
            null, null, 1, commands);

        await new AdbServerDiscoverySource("127.0.0.1", port)
            .DiscoverAsync(TestContext.Current.CancellationToken);
        await serving;

        Assert.Equal(["host:devices-l"], commands);
    }

    private static async Task ServeAsync(TcpListener listener, string listing, string? mdnsTable,
        string? mdnsFailure, int commandConnections, List<string> commands)
    {
        var ct = TestContext.Current.CancellationToken;
        using var probe = await listener.AcceptTcpClientAsync(ct);
        for (var round = 0; round < commandConnections; round++)
        {
            using var connection = await listener.AcceptTcpClientAsync(ct);
            var stream = connection.GetStream();
            var command = await ReadRequestAsync(stream, ct);
            commands.Add(command);
            if (command == "host:mdns:services")
            {
                if (mdnsTable is not null)
                {
                    await WriteOkayAsync(stream, mdnsTable, ct);
                }
                else
                {
                    await WriteFailAsync(stream, mdnsFailure ?? "mdns disabled", ct);
                }

                continue;
            }

            Assert.Equal("host:devices-l", command);
            await WriteOkayAsync(stream, listing, ct);
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = Convert.ToInt32(Encoding.ASCII.GetString(header), 16);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task WriteOkayAsync(NetworkStream stream, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"OKAY{bytes.Length:X4}"), ct);
        await stream.WriteAsync(bytes, ct);
    }

    private static async Task WriteFailAsync(NetworkStream stream, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"FAIL{bytes.Length:X4}"), ct);
        await stream.WriteAsync(bytes, ct);
    }
}