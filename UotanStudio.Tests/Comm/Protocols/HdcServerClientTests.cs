using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public sealed class HdcServerClientTests
{
    private static async Task<byte[]> ReadPacket(NetworkStream stream, CancellationToken ct)
    {
        var size = new byte[4];
        await stream.ReadExactlyAsync(size, ct);
        var data = new byte[BinaryPrimitives.ReadInt32BigEndian(size)];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    private static async Task SendPacket(NetworkStream stream, byte[] bytes, CancellationToken ct)
    {
        var packet = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet, bytes.Length);
        bytes.CopyTo(packet, 4);
        // Split even the length header: a TCP Read is not a protocol frame.
        foreach (var value in packet) await stream.WriteAsync(new byte[] { value }, ct);
    }

    private static async Task AcceptHandshake(NetworkStream stream, string expectedTarget, CancellationToken ct)
    {
        var hello = new byte[108];
        "OHOS HDC"u8.CopyTo(hello);
        hello[10] = (byte)'H';
        BinaryPrimitives.WriteUInt32BigEndian(hello.AsSpan(12), 0x12345678);
        await SendPacket(stream, hello, ct);
        var reply = await ReadPacket(stream, ct);
        Assert.Equal(44, reply.Length);
        Assert.Equal((byte)'H', reply[10]);
        Assert.Equal(expectedTarget, Encoding.UTF8.GetString(reply, 12, 32).TrimEnd('\0'));
    }

    [Fact]
    public async Task HostHandshake_SelectsTarget_AndReadsDelayedFragmentedUtf8UntilEof()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct);
            var stream = peer.GetStream();
            await AcceptHandshake(stream, "DEVICE-A", ct);
            Assert.Equal("shell echo test\0", Encoding.UTF8.GetString(await ReadPacket(stream, ct)));
            var bytes = Encoding.UTF8.GetBytes("首帧\n尾帧");
            await SendPacket(stream, bytes[..2], ct);
            await Task.Delay(100, ct);
            await SendPacket(stream, bytes[2..], ct);
        }, ct);
        using var client = new HdcServerClient(port: ((IPEndPoint)listener.LocalEndpoint).Port);
        var output = await client.ExecuteAsync("DEVICE-A", "shell echo test", cancellationToken: ct);
        await server;
        Assert.Equal("首帧\n尾帧", Encoding.UTF8.GetString(output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4194305)]
    public async Task InvalidFrameLength_FailsBeforeAllocation(int size)
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct);
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, size);
            await peer.GetStream().WriteAsync(bytes, ct);
        }, ct);
        using var client = new HdcServerClient(port: ((IPEndPoint)listener.LocalEndpoint).Port);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ExecuteAsync("", "list targets", cancellationToken: ct));
        await server;
    }

    [Fact]
    public async Task TruncatedResponse_IsNotSuccessfulEof()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct);
            await AcceptHandshake(peer.GetStream(), "test", ct);
            await ReadPacket(peer.GetStream(), ct);
            await peer.GetStream().WriteAsync(new byte[] { 0, 0, 0, 4, 1 }, ct);
        }, ct);
        using var client = new HdcServerClient(port: ((IPEndPoint)listener.LocalEndpoint).Port);
        await Assert.ThrowsAsync<EndOfStreamException>(() => client.ExecuteAsync("test", "shell true", cancellationToken: ct));
        await server;
    }

    [Fact]
    public async Task Timeout_IsFailure_AndClosesClientChannel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct);
            await AcceptHandshake(peer.GetStream(), "test", ct);
            await ReadPacket(peer.GetStream(), ct);
            Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], ct));
        }, ct);
        using var client = new HdcServerClient(port: ((IPEndPoint)listener.LocalEndpoint).Port);
        await Assert.ThrowsAsync<TimeoutException>(() => client.ExecuteAsync("test", "shell sleep 10", 500, ct));
        await server.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Theory]
    [InlineData("[Empty]\r\n", 0)]
    [InlineData("one\ntwo\n", 2)]
    public async Task Enumeration_DoesNotInventEmptyMarkerDevice(string reply, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct);
            await AcceptHandshake(peer.GetStream(), "", ct);
            Assert.Equal("list targets\0", Encoding.UTF8.GetString(await ReadPacket(peer.GetStream(), ct)));
            await SendPacket(peer.GetStream(), Encoding.UTF8.GetBytes(reply), ct);
        }, ct);
        using var client = new HdcServerClient(port: ((IPEndPoint)listener.LocalEndpoint).Port);
        var targets = await Task.Run(() => client.ListTargets(), ct);
        await server;
        Assert.Equal(count, targets.Count);
    }

    [Theory]
    [InlineData("system", "")]
    [InlineData("bootloader", "-bootloader")]
    [InlineData("recovery", "-recovery")]
    public void RebootMapping_FollowsUpstream(string mode, string expected)
        => Assert.Equal(expected, HdcDeviceServices.RebootArgument(mode));

    [Fact]
    public void UnsupportedReboot_DoesNotSilentlyBecomeSystemReboot()
        => Assert.Throws<NotSupportedException>(() => HdcDeviceServices.RebootArgument("edl"));

    [Fact]
    public void ProcessArguments_AreLiteral_AndFailureTextIsNotSuccess()
    {
        var info = HdcCommandClient.CreateStartInfo("hdc", ["-t", "device", "file", "send", "C:\\some folder\\a.hap", "/data/a;echo bad"]);
        Assert.False(info.UseShellExecute);
        Assert.Equal("C:\\some folder\\a.hap", info.ArgumentList[4]);
        Assert.Equal("/data/a;echo bad", info.ArgumentList[5]);
        Assert.Throws<IOException>(() => HdcServerClient.ThrowOnFailure("\n[Fail]Permission denied\n"));
        Assert.Equal("'a'\"'\"'b'", HdcDeviceServices.QuoteShellArgument("a'b"));
    }
}
