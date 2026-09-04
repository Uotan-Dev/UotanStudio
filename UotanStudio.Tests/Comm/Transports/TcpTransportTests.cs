using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Transports.Tcp;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports;

/// <summary>
/// TCP transport tests over a loopback peer: ADB raw byte stream and fastboot length-prefixed
/// framing (8-byte big-endian) with the FB01 handshake.
/// <para>TCP 传输回环测试：ADB 原始字节流与 fastboot 长度前缀帧（8 字节大端）及 FB01 握手。</para>
/// </summary>
public class TcpTransportTests
{
    // ============== AdbTcpTransport ==============

    [Fact]
    public void AdbTcpTransport_Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentException>(() => new AdbTcpTransport("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdbTcpTransport("127.0.0.1", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdbTcpTransport("127.0.0.1", 65536));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdbTcpTransport("127.0.0.1", 5555, 0));
    }

    [Fact]
    public async Task AdbTcpTransport_WriteThenRead_EchoesOverLoopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // 必须先建立客户端连接再 accept：若先 await AcceptTcpClientAsync，客户端在
        // await 之后才创建，accept 永远等不到连接而死锁挂起（与 Fastboot 版测试一致）。
        // The client connection must be established BEFORE accepting: awaiting AcceptTcpClientAsync
        // first would deadlock because the client is only created after the await, so the accept
        // never gets a connection (matching the Fastboot tests' order).
        using var client = new AdbTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000);
        var server = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        using var serverStream = server.GetStream();

        Assert.True(client.IsConnected);
        Assert.Equal("127.0.0.1", client.Host);
        Assert.Equal(port, client.Port);

        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var written = client.Write(payload, 0, payload.Length, 2000);
        Assert.Equal(payload.Length, written);

        var serverRx = new byte[4];
        await serverStream.ReadExactlyAsync(serverRx, TestContext.Current.CancellationToken);
        Assert.Equal(payload, serverRx);

        var reply = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        await serverStream.WriteAsync(reply, TestContext.Current.CancellationToken);

        var clientRx = new byte[4];
        var read = client.Read(clientRx, 0, clientRx.Length, 2000);
        Assert.Equal(4, read);
        Assert.Equal(reply, clientRx);
    }

    [Fact]
    public async Task AdbTcpTransport_Read_BlocksUntilAllRequestedBytesArrive()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // 与 WriteThenRead 相同：先建客户端再 accept，避免 accept 死锁。
        // Same order as WriteThenRead: connect the client first, then accept, avoiding the deadlock.
        using var client = new AdbTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000);
        var server = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        using var serverStream = server.GetStream();

        // Send the 8 bytes in two writes; the exact-read must wait for both.
        // 分两次写入 8 字节；精确读取必须等两块都到达。
        await serverStream.WriteAsync(new byte[] { 1, 2, 3, 4 }, TestContext.Current.CancellationToken);
        await serverStream.WriteAsync(new byte[] { 5, 6, 7, 8 }, TestContext.Current.CancellationToken);

        var buffer = new byte[8];
        var read = client.Read(buffer, 0, 8, 2000);
        Assert.Equal(8, read);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, buffer);
    }

    [Fact]
    public async Task AdbTcpTransport_ReadAsync_ReturnsExactBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // 与 WriteThenRead 相同：先建客户端再 accept，避免 accept 死锁。
        // Same order as WriteThenRead: connect the client first, then accept, avoiding the deadlock.
        using var client = new AdbTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000);
        var server = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        using var serverStream = server.GetStream();

        await serverStream.WriteAsync("ping!"u8.ToArray(), TestContext.Current.CancellationToken);

        var buffer = new byte[5];
        var read = await client.ReadAsync(buffer, 0, 5, 2000, TestContext.Current.CancellationToken);
        Assert.Equal(5, read);
        Assert.Equal("ping!"u8.ToArray(), buffer);
    }

    // ============== FastbootTcpTransport ==============

    [Fact]
    public void FastbootTcpTransport_Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentException>(() => new FastbootTcpTransport("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FastbootTcpTransport("127.0.0.1", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FastbootTcpTransport("127.0.0.1", 5554, 0));
    }

    [Fact]
    public async Task FastbootTcpTransport_Handshake_CompletesWithFB01()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var handshakeTask = FastbootHandshakeAsync(listener);

        using var client = new FastbootTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000);
        Assert.True(client.IsConnected);
        await handshakeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void FastbootTcpTransport_Handshake_RejectsNonFbResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            using var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[4], TestContext.Current.CancellationToken);
            await stream.WriteAsync("ZZ01"u8.ToArray(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var exception = Assert.Throws<IOException>(() =>
            new FastbootTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000));
        Assert.Contains("handshake", exception.Message, StringComparison.OrdinalIgnoreCase);
        serverTask.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void FastbootTcpTransport_Handshake_RejectsUnsupportedVersion()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            using var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[4], TestContext.Current.CancellationToken);
            await stream.WriteAsync("FB00"u8.ToArray(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var exception = Assert.Throws<IOException>(() =>
            new FastbootTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000));
        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
        serverTask.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FastbootTcpTransport_Write_LengthPrefixesTheFrame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[]? received = null;
        var serverTask = Task.Run(async () =>
        {
            using var stream = await FastbootHandshakeAsync(listener);
            var lenBuffer = new byte[8];
            await stream.ReadExactlyAsync(lenBuffer, TestContext.Current.CancellationToken);
            var length = BinaryPrimitives.ReadUInt64BigEndian(lenBuffer);
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, TestContext.Current.CancellationToken);
            received = payload;
        }, TestContext.Current.CancellationToken);

        using var client = new FastbootTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000);
        var data = Encoding.ASCII.GetBytes("getvar:version");
        var written = client.Write(data, 0, data.Length, 2000);
        Assert.Equal(data.Length, written);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(data, received);
    }

    [Fact]
    public async Task FastbootTcpTransport_Read_ParsesLengthPrefixedFrame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var payload = Encoding.ASCII.GetBytes("OKAY");
        var serverTask = Task.Run(async () =>
        {
            using var stream = await FastbootHandshakeAsync(listener);
            var header = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)payload.Length);
            await stream.WriteAsync(header, TestContext.Current.CancellationToken);
            await stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        using var client = new FastbootTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000);
        var buffer = new byte[64];
        var read = client.Read(buffer, 0, buffer.Length, 2000);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer[..read]);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FastbootTcpTransport_Read_RejectsOversizedFrameLength()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var stream = await FastbootHandshakeAsync(listener);
            // A frame length beyond uint.MaxValue must be rejected.
            // 超过 uint.MaxValue 的帧长度必须被拒绝。
            var header = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(header, ulong.MaxValue);
            await stream.WriteAsync(header, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        using var client = new FastbootTcpTransport("127.0.0.1", port, connectTimeoutMs: 5000);
        var exception = Assert.Throws<IOException>(() => client.Read(new byte[8], 0, 8, 2000));
        Assert.Contains("frame length", exception.Message, StringComparison.OrdinalIgnoreCase);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    /// <summary>Accepts a fastboot TCP peer and completes the FB01 handshake, returning its stream.</summary>
    private static async Task<NetworkStream> FastbootHandshakeAsync(TcpListener listener)
    {
        var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        var handshake = new byte[4];
        await stream.ReadExactlyAsync(handshake, TestContext.Current.CancellationToken);
        Assert.Equal("FB01", Encoding.ASCII.GetString(handshake));
        await stream.WriteAsync("FB01"u8.ToArray(), TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);
        return stream;
    }
}