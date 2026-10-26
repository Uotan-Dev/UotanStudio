using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Fastboot.Transport;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class FastbootUdpHandshakeTests
{
    private const byte PacketIdError = 0;
    private const byte PacketIdDeviceQuery = 1;
    private const byte PacketIdInitialization = 2;
    private const byte PacketIdFastboot = 3;

    [Fact]
    public async Task Handshake_DeviceQuerySequence_AdoptedForSubsequentPackets()
    {
        using var server = new UdpClient(0);
        var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.Client.LocalEndPoint!).Port);
        using var client = new UdpClient();
        client.Client.ReceiveTimeout = 5000;
        client.Client.SendTimeout = 5000;
        var packets = new FastbootUdpPackets(endpoint, maxAttempts: 3);

        var serverTask = Task.Run(async () =>
        {
            var query = await server.ReceiveAsync(TestContext.Current.CancellationToken);
            Assert.Equal(PacketIdDeviceQuery, query.Buffer[0]);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.Buffer.AsSpan(2, 2)));
            SendReply(server, query.RemoteEndPoint, BuildPacket(PacketIdDeviceQuery, 0, 0, [0x00, 0x02]));

            var init = await server.ReceiveAsync(TestContext.Current.CancellationToken);
            Assert.Equal(PacketIdInitialization, init.Buffer[0]);
            Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(init.Buffer.AsSpan(2, 2)));
            SendReply(server, init.RemoteEndPoint, BuildPacket(PacketIdInitialization, 0, 2, [0x00, 0x01, 0x04, 0x00]));

            var data = await server.ReceiveAsync(TestContext.Current.CancellationToken);
            Assert.Equal(PacketIdFastboot, data.Buffer[0]);
            Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(data.Buffer.AsSpan(2, 2)));
            Assert.Equal((byte)'h', data.Buffer[4]);
            Assert.Equal((byte)'i', data.Buffer[5]);
            SendReply(server, data.RemoteEndPoint, BuildPacket(PacketIdFastboot, 0, 3, []));
        }, TestContext.Current.CancellationToken);

        try
        {
            packets.Handshake(client);
            packets.Write(client, "hi"u8.ToArray(), 0, 2);
            await serverTask;
        }
        catch
        {
            await UnblockServerAsync(server, serverTask);
            throw;
        }
    }

    [Fact]
    public async Task Handshake_ErrorResponsePayload_SurfacedInException()
    {
        using var server = new UdpClient(0);
        var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.Client.LocalEndPoint!).Port);
        using var client = new UdpClient();
        client.Client.ReceiveTimeout = 5000;
        client.Client.SendTimeout = 5000;
        var packets = new FastbootUdpPackets(endpoint, maxAttempts: 3);

        var serverTask = Task.Run(async () =>
        {
            var query = await server.ReceiveAsync(TestContext.Current.CancellationToken);
            Assert.Equal(PacketIdDeviceQuery, query.Buffer[0]);
            SendReply(server, query.RemoteEndPoint, BuildPacket(PacketIdError, 0, 0, "boom!"u8));
        }, TestContext.Current.CancellationToken);

        try
        {
            var failure = Assert.Throws<IOException>(() => packets.Handshake(client));
            Assert.Contains("boom!", failure.Message);
            await serverTask;
        }
        catch
        {
            await UnblockServerAsync(server, serverTask);
            throw;
        }
    }

    private static byte[] BuildPacket(byte id, byte flag, ushort sequence, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[4 + payload.Length];
        packet[0] = id;
        packet[1] = flag;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), sequence);
        payload.CopyTo(packet.AsSpan(4));
        return packet;
    }

    private static void SendReply(UdpClient server, IPEndPoint destination, byte[] packet)
    {
        _ = server.Send(packet, packet.Length, destination);
    }

    private static async Task UnblockServerAsync(UdpClient server, Task serverTask)
    {
        server.Client.Close();
        try
        {
            await serverTask;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}