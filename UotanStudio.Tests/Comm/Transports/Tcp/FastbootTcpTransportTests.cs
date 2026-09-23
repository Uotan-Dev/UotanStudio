using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Transports.Tcp;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Tcp;

public class FastbootTcpTransportTests
{
    [Fact]
    public async Task ReadAndWriteAsync_FragmentedFrame_PreservesPayloadAndLength()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = peer.GetStream();
            var hello = new byte[4];
            await stream.ReadExactlyAsync(hello);
            Assert.Equal("FB01", Encoding.ASCII.GetString(hello));
            await stream.WriteAsync("FB01"u8.ToArray());
            var prefix = new byte[8];
            await stream.ReadExactlyAsync(prefix);
            Assert.Equal(3UL, BinaryPrimitives.ReadUInt64BigEndian(prefix));
            var request = new byte[3];
            await stream.ReadExactlyAsync(request);
            Assert.Equal("abc", Encoding.ASCII.GetString(request));
            BinaryPrimitives.WriteUInt64BigEndian(prefix, 5);
            await stream.WriteAsync(prefix.AsMemory(0, 3));
            await stream.WriteAsync(prefix.AsMemory(3));
            await stream.WriteAsync("hello"u8.ToArray());
        }, TestContext.Current.CancellationToken);

        using (var transport = new FastbootTcpTransport("127.0.0.1", port))
        {
            Assert.Equal(3, await transport.WriteAsync("abc"u8.ToArray(), 0, 3, 5000, TestContext.Current.CancellationToken));
            var buffer = new byte[5];
            Assert.Equal(2, await transport.ReadAsync(buffer, 0, 2, 5000, TestContext.Current.CancellationToken));
            Assert.Equal(3, await transport.ReadAsync(buffer, 2, 3, 5000, TestContext.Current.CancellationToken));
            Assert.Equal("hello", Encoding.ASCII.GetString(buffer));
        }

        await server;
    }
}
