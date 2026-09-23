using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Transports.Udp;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Udp;

public class FastbootUdpTransportTests
{
    [Fact]
    public async Task ReadAndWrite_HandshakeAndSequence_PreservePayload()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
        var server = Task.Run(async () =>
        {
            var query = await peer.ReceiveAsync(TestContext.Current.CancellationToken);
            Assert.Equal((byte)1, query.Buffer[0]);
            using (var unrelated = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                var forged = new byte[] { 1, 0, 0, 0, 0, 99 };
                await unrelated.SendAsync(forged, query.RemoteEndPoint, TestContext.Current.CancellationToken);
            }
            await Task.Delay(25, TestContext.Current.CancellationToken);
            await Reply(1, 0, 0, [0, 7], query.RemoteEndPoint);
            var init = await peer.ReceiveAsync(TestContext.Current.CancellationToken);
            Assert.Equal((byte)2, init.Buffer[0]);
            Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16BigEndian(init.Buffer.AsSpan(2)));
            await Reply(2, 0, 7, [0, 1, 2, 0], init.RemoteEndPoint);
            var write = await peer.ReceiveAsync(TestContext.Current.CancellationToken);
            Assert.Equal("abc", Encoding.ASCII.GetString(write.Buffer.AsSpan(4)));
            await Reply(3, 0, 8, [], write.RemoteEndPoint);
            var read = await peer.ReceiveAsync(TestContext.Current.CancellationToken);
            Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16BigEndian(read.Buffer.AsSpan(2)));
            await Reply(3, 0, 9, "OKAY"u8.ToArray(), read.RemoteEndPoint);
        }, TestContext.Current.CancellationToken);

        using (var transport = new FastbootUdpTransport("127.0.0.1", port))
        {
            Assert.Equal(3, transport.Write("abc"u8.ToArray(), 0, 3, 1000));
            var buffer = new byte[4];
            Assert.Equal(4, transport.Read(buffer, 0, 4, 1000));
            Assert.Equal("OKAY", Encoding.ASCII.GetString(buffer));
        }

        await server;

        async Task Reply(byte id, byte flag, ushort sequence, byte[] payload, IPEndPoint destination)
        {
            var packet = new byte[4 + payload.Length];
            packet[0] = id;
            packet[1] = flag;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
            payload.CopyTo(packet, 4);
            await peer.SendAsync(packet, destination);
        }
    }
}
