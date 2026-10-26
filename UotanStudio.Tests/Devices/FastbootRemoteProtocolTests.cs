using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UotanStudio.Comm.Protocols.Fastboot.Transport;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class FastbootRemoteProtocolTests
{
    [Fact]
    public void TryNormalizeNetworkAddress_FastbootBareHost_AppliesDefault5554()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Fastboot, "192.168.1.5", out var address);

        Assert.True(accepted);
        Assert.Equal("192.168.1.5:5554", address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_FastbootExplicitPort_KeepsPort()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Fastboot, "192.168.1.5:4040", out var address);

        Assert.True(accepted);
        Assert.Equal("192.168.1.5:4040", address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_FastbootWhitespaceHost_IsRejected()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Fastboot, "  ", out var address);

        Assert.False(accepted);
        Assert.Equal(string.Empty, address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_ProtocolWithoutNetworkTransport_IsRejected()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Edl, "192.168.1.5", out var address);

        Assert.False(accepted);
        Assert.Equal(string.Empty, address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_FastbootUdpBareIp_AppliesDefault5554()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Fastboot, "192.168.1.5", out var address, DeviceTransportKind.Udp);

        Assert.True(accepted);
        Assert.Equal("192.168.1.5:5554", address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_FastbootUdpExplicitPort_KeepsPort()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Fastboot, "192.168.1.5:4040", out var address, DeviceTransportKind.Udp);

        Assert.True(accepted);
        Assert.Equal("192.168.1.5:4040", address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_FastbootUdpHostName_IsRejected()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Fastboot, "phone.local:5554", out _, DeviceTransportKind.Udp);

        Assert.False(accepted);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_FastbootUdpBracketedIpv6_IsAccepted()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Fastboot, "[::1]:5554", out var address, DeviceTransportKind.Udp);

        Assert.True(accepted);
        Assert.Equal("[::1]:5554", address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_UdpTransportWithNonFastbootProtocol_IsRejected()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Adb, "192.168.1.5", out _, DeviceTransportKind.Udp);

        Assert.False(accepted);
    }

    [Fact(Timeout = 20000)]
    public async Task FastbootTcpTransport_HandshakeAndFramedRoundTrip_Succeed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            var connect = Task.Run(() => new FastbootTcpTransport("127.0.0.1", port, 5000), cancellationToken);
            using var server = await accepted;

            var hello = new byte[4];
            await ReadFullyAsync(server, hello, cancellationToken);
            Assert.Equal("FB01"u8.ToArray(), hello);
            server.GetStream().Write("FB01"u8);

            using var transport = await connect;
            var sent = "OKAY0.4"u8.ToArray();
            var frame = new byte[8 + sent.Length];
            BinaryPrimitives.WriteUInt64BigEndian(frame, (ulong)sent.Length);
            sent.CopyTo(frame, 8);
            await server.GetStream().WriteAsync(frame, cancellationToken);

            var received = new byte[sent.Length];
            Assert.Equal(sent.Length, transport.Read(received, 0, received.Length, 5000));
            Assert.Equal(sent, received);

            var command = "getvar:product"u8.ToArray();
            transport.Write(command, 0, command.Length, 5000);
            var prefix = new byte[8];
            await ReadFullyAsync(server, prefix, cancellationToken);
            Assert.Equal((ulong)command.Length, BinaryPrimitives.ReadUInt64BigEndian(prefix));
            var receivedCommand = new byte[command.Length];
            await ReadFullyAsync(server, receivedCommand, cancellationToken);
            Assert.Equal(command, receivedCommand);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact(Timeout = 20000)]
    public async Task FastbootTcpTransport_PeerWithoutProtocol_ThrowsDuringConstruction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            var connect = Task.Run(() => new FastbootTcpTransport("127.0.0.1", port, 5000), cancellationToken);
            using var server = await accepted;

            var hello = new byte[4];
            await ReadFullyAsync(server, hello, cancellationToken);
            server.GetStream().Write("HTTP"u8);

            await Assert.ThrowsAsync<IOException>(async () => await connect);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void AppSettings_OldAdbHdcPayload_LeavesFastbootTargetsEmpty()
    {
        const string json = """
            {
              "pairedNetworkTargets": [
                { "address": "192.168.1.5:37177", "requiresTls": true }
              ],
              "pairedHdcNetworkTargets": ["192.168.1.6:8710"]
            }
            """;

        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Single(settings.PairedHdcNetworkTargets);
        Assert.Empty(settings.PairedFastbootNetworkTargets);
        Assert.Empty(settings.PairedFastbootUdpNetworkTargets);
    }

    [Fact]
    public void AppSettings_FastbootTargets_RoundTripThroughJson()
    {
        const string json = """
            { "pairedFastbootNetworkTargets": ["192.168.1.7:5554", "192.168.1.8:4040"] }
            """;

        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Equal(["192.168.1.7:5554", "192.168.1.8:4040"], settings.PairedFastbootNetworkTargets);
        var serialized = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"pairedFastbootNetworkTargets\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSettings_FastbootUdpTargets_RoundTripThroughJson()
    {
        const string json = """
            { "pairedFastbootUdpNetworkTargets": ["192.168.1.9:5554"] }
            """;

        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Equal(["192.168.1.9:5554"], settings.PairedFastbootUdpNetworkTargets);
        Assert.Empty(settings.PairedFastbootNetworkTargets);
        var serialized = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"pairedFastbootUdpNetworkTargets\"", serialized, StringComparison.Ordinal);
    }

    [Fact(Timeout = 20000)]
    public async Task FastbootUdpTransport_HandshakeAndExchangeRoundTrip_Succeed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var serverTask = Task.Run(async () =>
        {
            var query = await server.ReceiveAsync(cancellationToken);
            Assert.Equal(1, query.Buffer[0]);
            Reply(server, query.RemoteEndPoint, 1, 0, [0x00, 0x01]);

            var init = await server.ReceiveAsync(cancellationToken);
            Assert.Equal(2, init.Buffer[0]);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(init.Buffer.AsSpan(2, 2)));
            Reply(server, init.RemoteEndPoint, 2, 1, [0x00, 0x01, 0x20, 0x00]);

            var command = await server.ReceiveAsync(cancellationToken);
            Assert.Equal(3, command.Buffer[0]);
            Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(command.Buffer.AsSpan(2, 2)));
            Assert.Equal("getvar:product"u8.ToArray(), command.Buffer[4..]);
            Reply(server, command.RemoteEndPoint, 3, 2, []);

            var poll = await server.ReceiveAsync(cancellationToken);
            Assert.Equal(3, poll.Buffer[0]);
            Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(poll.Buffer.AsSpan(2, 2)));
            Reply(server, poll.RemoteEndPoint, 3, 3, "OKAYpixel"u8.ToArray());
        }, cancellationToken);

        var connect = Task.Run(
            () => new FastbootUdpTransport("127.0.0.1", port, timeoutMs: 5000, maxTransmissionAttempts: 3),
            cancellationToken);
        try
        {
            using var transport = await connect;
            Assert.True(transport.IsConnected);

            var command = "getvar:product"u8.ToArray();
            transport.Write(command, 0, command.Length, 5000);
            var expected = "OKAYpixel"u8;
            var response = new byte[16];
            Assert.Equal(expected.Length, transport.Read(response, 0, response.Length, 5000));
            Assert.Equal(expected.ToArray(), response[..expected.Length]);
            await serverTask;
        }
        catch
        {
            server.Client.Close();
            try
            {
                await serverTask;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }

            throw;
        }
    }

    [Fact(Timeout = 20000)]
    public async Task FastbootUdpTransport_SilentPeer_ThrowsDuringConstruction()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var connect = Task.Run(
            () => new FastbootUdpTransport("127.0.0.1", port, timeoutMs: 200, maxTransmissionAttempts: 2),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(async () => await connect);
    }

    private static void Reply(UdpClient server, IPEndPoint destination, byte id, ushort sequence, byte[] payload)
    {
        var packet = new byte[4 + payload.Length];
        packet[0] = id;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), sequence);
        payload.CopyTo(packet, 4);
        _ = server.Send(packet, packet.Length, destination);
    }

    private static async Task ReadFullyAsync(TcpClient client, byte[] buffer, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read <= 0)
            {
                throw new IOException("The test peer closed the connection early.");
            }

            total += read;
        }
    }
}