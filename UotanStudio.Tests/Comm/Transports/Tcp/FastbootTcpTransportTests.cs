using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Fastboot.Transport;
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

    [Fact]
    public void Constructor_ConnectionRefused_ThrowsUnwrappedSocketException()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var exception = Assert.Throws<SocketException>(() => new FastbootTcpTransport("127.0.0.1", port, connectTimeoutMs: 10000));

        Assert.Equal(SocketError.ConnectionRefused, exception.SocketErrorCode);
    }

    [Fact]
    public async Task Constructor_PeerAcceptsButNeverHandshakes_ReportsHandshakeFailureNotConnectFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var hello = new byte[4];
            await peer.GetStream().ReadExactlyAsync(hello, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        Assert.Throws<IOException>(() => new FastbootTcpTransport("127.0.0.1", port, connectTimeoutMs: 10000));

        await server;
    }

    [Fact]
    public void Constructor_HostDoesNotAnswer_TimesOutWithoutReportingRefusal()
    {
        var unreachable = IPAddress.Parse("10.255.255.1");

        var exception = Assert.ThrowsAny<Exception>(() =>
            new FastbootTcpTransport(unreachable.ToString(), 1, connectTimeoutMs: 200));

        Assert.IsNotType<AggregateException>(exception);
        if (exception is SocketException socket)
        {
            Assert.NotEqual(SocketError.ConnectionRefused, socket.SocketErrorCode);
        }
        else
        {
            Assert.IsType<TimeoutException>(exception);
        }
    }

    [Fact]
    public async Task NoDelay_IsEnabledOnTheConnectedAndTheReconnectedSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peers = new List<Task>();
        for (var i = 0; i < 2; i++)
        {
            peers.Add(Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                var stream = peer.GetStream();
                var hello = new byte[4];
                await stream.ReadExactlyAsync(hello, TestContext.Current.CancellationToken);
                await stream.WriteAsync("FB01"u8.ToArray(), TestContext.Current.CancellationToken);
                await released.Task;
            }, TestContext.Current.CancellationToken));
        }

        try
        {
            using var transport = new FastbootTcpTransport("127.0.0.1", port);
            Assert.True(transport.NoDelay);
            Assert.True(transport.IsConnected);

            transport.Disconnect();
            Assert.False(transport.IsConnected);

            transport.Connect();
            Assert.True(transport.NoDelay);
            Assert.True(transport.IsConnected);
        }
        finally
        {
            released.SetResult();
        }

        Assert.All(peers, peer => Assert.False(peer.IsFaulted));
    }

    [Fact]
    public async Task IsConnected_PeerClosedConnection_ReportsDisconnected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = peer.GetStream();
            var hello = new byte[4];
            await stream.ReadExactlyAsync(hello, TestContext.Current.CancellationToken);
            await stream.WriteAsync("FB01"u8.ToArray(), TestContext.Current.CancellationToken);
            await closed.Task;
        }, TestContext.Current.CancellationToken);

        using var transport = new FastbootTcpTransport("127.0.0.1", port);
        Assert.True(transport.IsConnected);

        closed.SetResult();
        await server;

        var deadline = Environment.TickCount64 + 5000;
        while (transport.IsConnected && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Read_PeerClosedMidFrame_ReturnsZeroWithoutWaitingOutTheDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = peer.GetStream();
            var hello = new byte[4];
            await stream.ReadExactlyAsync(hello, TestContext.Current.CancellationToken);
            await stream.WriteAsync("FB01"u8.ToArray(), TestContext.Current.CancellationToken);
            var prefix = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(prefix, 64);
            await stream.WriteAsync(prefix, TestContext.Current.CancellationToken);
            await stream.WriteAsync("OKAY"u8.ToArray(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        using var transport = new FastbootTcpTransport("127.0.0.1", port);
        await server;

        var buffer = new byte[64];
        var started = Environment.TickCount64;
        var first = transport.Read(buffer, 0, 4, 10000);
        var second = transport.Read(buffer, 4, 60, 10000);
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal(4, first);
        Assert.Equal(0, second);
        Assert.True(elapsed < 9000, $"a closed peer must not be polled for the whole deadline (took {elapsed} ms)");
    }

    [Fact]
    public async Task ReceiveResponse_PeerClosed_FailsFastInsteadOfWaitingForTheDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = peer.GetStream();
            var hello = new byte[4];
            await stream.ReadExactlyAsync(hello, TestContext.Current.CancellationToken);
            await stream.WriteAsync("FB01"u8.ToArray(), TestContext.Current.CancellationToken);
            await closed.Task;
        }, TestContext.Current.CancellationToken);

        var transport = new FastbootTcpTransport("127.0.0.1", port);
        using var session = new FastbootSession(transport, timeoutMs: 8000);
        closed.SetResult();
        await server;

        var deadline = Environment.TickCount64 + 5000;
        while (transport.IsConnected && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.False(transport.IsConnected);

        var started = Environment.TickCount64;
        var response = session.ReceiveResponse();
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal(FastbootResponseState.Fail, response.State);
        Assert.Contains("disconnected", response.Text, StringComparison.Ordinal);
        Assert.True(elapsed < 5000, $"a closed peer must fail fast, not wait out the deadline (took {elapsed} ms)");
    }
}