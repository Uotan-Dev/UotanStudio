using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public sealed class DebugBridgeRouteTests
{
    [Theory]
    [InlineData("device offline")]
    [InlineData("device unauthorized")]
    [InlineData("device not found")]
    public async Task Adb_ServerRejectsTarget_NeverOpensUsb(string failure)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var opens = 0;
        var server = Task.Run(async () =>
        {
            using var probe = await listener.AcceptTcpClientAsync(ct);
            using var target = await listener.AcceptTcpClientAsync(ct);
            var stream = target.GetStream();
            Assert.Equal("host:transport:TARGET", await ReadRequest(stream, ct));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"FAIL{failure.Length:X4}{failure}"), ct);
        }, ct);
        using var session = DebugBridgeRoute.OpenAdb("TARGET", () => { opens++; throw new InvalidOperationException("USB opened"); }, port: port);
        var error = Assert.Throws<IOException>(session.Connect);
        Assert.Contains(failure, error.Message);
        Assert.Equal(0, opens);
        await server;
    }

    [Fact]
    public void AbsenceIsNotCached_WhenServerStartsNextOperationUsesIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var opens = 0;
        Assert.Throws<DirectSelectedException>(() => DebugBridgeRoute.OpenAdb("A", () => { opens++; throw new DirectSelectedException(); }, port: port));
        // Use a new listener for the same endpoint; no cached negative may survive.
        using var running = new TcpListener(IPAddress.Loopback, port);
        running.Start();
        using var session = DebugBridgeRoute.OpenAdb("A", () => { opens++; throw new DirectSelectedException(); }, port: port);
        Assert.True(session.Connection.UsesServer);
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task Adb_ServerSession_PreservesFragmentedShellFramesAndSync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using (var target = await listener.AcceptTcpClientAsync(ct))
                await Select(target.GetStream(), ct);
            using (var shell = await listener.AcceptTcpClientAsync(ct))
            {
                var stream = shell.GetStream();
                await Select(stream, ct);
                Assert.Equal("shell,v2:echo test", await ReadRequest(stream, ct));
                await stream.WriteAsync("OKAY"u8.ToArray(), ct);
                byte[] frames = [1, 2, 0, 0, 0, (byte)'o', (byte)'k', 3, 1, 0, 0, 0, 7];
                foreach (var b in frames) await stream.WriteAsync(new byte[] { b }, ct);
            }
            using (var sync = await listener.AcceptTcpClientAsync(ct))
            {
                var stream = sync.GetStream();
                await Select(stream, ct);
                Assert.Equal("sync:", await ReadRequest(stream, ct));
                await stream.WriteAsync("OKAY"u8.ToArray(), ct);
                var header = new byte[8];
                await stream.ReadExactlyAsync(header, ct);
                Assert.Equal("STAT", Encoding.ASCII.GetString(header, 0, 4));
                var path = new byte[BitConverter.ToInt32(header, 4)];
                await stream.ReadExactlyAsync(path, ct);
                Assert.Equal("/test", Encoding.UTF8.GetString(path));
                byte[] result = [.. "STAT"u8.ToArray(), .. BitConverter.GetBytes(33188u), .. BitConverter.GetBytes(123u), .. BitConverter.GetBytes(42u)];
                foreach (var b in result) await stream.WriteAsync(new byte[] { b }, ct);
            }
        }, ct);
        using var session = new AdbSession(new AdbServerServices("TARGET", port: port));
        session.Connect();
        var output = session.ExecuteCommand("echo test");
        Assert.Equal("ok", Encoding.UTF8.GetString(output.Stdout));
        Assert.Equal(7, output.ExitCode);
        using var syncClient = session.CreateSyncClient();
        Assert.Equal(123u, syncClient.Stat("/test")!.Value.Size);
        await server;
    }

    [Fact]
    public async Task Adb_SyncListThenPull_ConsumesFullDirectoryDoneRecord()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using (var target = await listener.AcceptTcpClientAsync(ct)) await Select(target.GetStream(), ct);
            using var peer = await listener.AcceptTcpClientAsync(ct);
            var stream = peer.GetStream();
            await Select(stream, ct);
            Assert.Equal("sync:", await ReadRequest(stream, ct));
            await stream.WriteAsync("OKAY"u8.ToArray(), ct);
            async Task Expect(string id, string path)
            {
                var header = new byte[8];
                await stream.ReadExactlyAsync(header, ct);
                Assert.Equal(id, Encoding.ASCII.GetString(header, 0, 4));
                var body = new byte[BitConverter.ToInt32(header, 4)];
                await stream.ReadExactlyAsync(body, ct);
                Assert.Equal(path, Encoding.UTF8.GetString(body));
            }
            await Expect("LIST", "/system/bin");
            byte[] listing = [.. "DENT"u8.ToArray(), .. BitConverter.GetBytes(33261u),
                .. BitConverter.GetBytes(3u), .. BitConverter.GetBytes(42u), .. BitConverter.GetBytes(2u),
                (byte)'s', (byte)'h', .. "DONE"u8.ToArray(), .. new byte[16]];
            // Deliberately fragment the directory terminator across socket reads.
            foreach (var b in listing) await stream.WriteAsync(new byte[] { b }, ct);
            await Expect("RECV", "/system/bin/sh");
            await stream.WriteAsync(new byte[] { (byte)'D', (byte)'A', (byte)'T', (byte)'A',
                3, 0, 0, 0, 1, 2, 3, (byte)'D', (byte)'O', (byte)'N', (byte)'E', 0, 0, 0, 0 }, ct);
        }, ct);
        using var session = new AdbSession(new AdbServerServices("TARGET", port: port));
        session.Connect();
        using var sync = session.CreateSyncClient();
        Assert.Equal("sh", Assert.Single(sync.List("/system/bin")).Name);
        using var output = new MemoryStream();
        sync.PullStream("/system/bin/sh", output);
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
        await server;
    }

    [Fact]
    public async Task Hdc_StaleUsbKey_UsesServerAndPropagatesMalformedHandshake()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var probe = await listener.AcceptTcpClientAsync(ct);
            using var request = await listener.AcceptTcpClientAsync(ct);
            await request.GetStream().WriteAsync(new byte[] { 0, 0, 0, 0 }, ct);
        }, ct);
        var device = new DetectedDevice { Key = "old-usb-key", Serial = "TARGET", Protocol = DeviceProtocolKind.Hdc, Transport = DeviceTransportKind.Usb };
        var service = new HdcDeviceService(port: port);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ShellAsync(device, "echo test", ct));
        await server;
    }

    [Fact]
    public void Hdc_ServerRoute_DoesNotEvaluateDirectFactory()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var opens = 0;
        using var session = DebugBridgeRoute.OpenHdc("TARGET", () => { opens++; throw new DirectSelectedException(); },
            port: ((IPEndPoint)listener.LocalEndpoint).Port);
        Assert.Equal(0, opens);
        Assert.Throws<NotSupportedException>(() => session.Connection);
    }

    [Fact]
    public async Task Adb_ServerStream_TimeoutDoesNotLoseBytes_AndSessionDisposalClosesStream()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using (var target = await listener.AcceptTcpClientAsync(ct)) await Select(target.GetStream(), ct);
            using var peer = await listener.AcceptTcpClientAsync(ct);
            var stream = peer.GetStream();
            await Select(stream, ct);
            Assert.Equal("localabstract:test", await ReadRequest(stream, ct));
            await stream.WriteAsync("OKAY"u8.ToArray(), ct);
            await send.Task.WaitAsync(ct);
            await stream.WriteAsync("frame"u8.ToArray(), ct);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], ct));
        }, ct);
        using var session = new AdbSession(new AdbServerServices("TARGET", port: port));
        session.Connect();
        using var stream = session.OpenStream("localabstract:test");
        Assert.Null(stream.Read(10));
        Assert.False(stream.IsClosed);
        send.SetResult();
        using var output = new MemoryStream();
        while (output.Length < 5) output.Write(stream.Read(2000)!);
        Assert.Equal("frame", Encoding.ASCII.GetString(output.ToArray()));
        session.Dispose();
        Assert.True(stream.IsClosed);
        await server;
    }

    private sealed class DirectSelectedException : Exception;

    private static async Task Select(NetworkStream stream, CancellationToken ct)
    {
        Assert.Equal("host:transport:TARGET", await ReadRequest(stream, ct));
        await stream.WriteAsync("OKAY"u8.ToArray(), ct);
    }

    private static async Task<string> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var bytes = new byte[int.Parse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber, CultureInfo.InvariantCulture)];
        await stream.ReadExactlyAsync(bytes, ct);
        return Encoding.UTF8.GetString(bytes);
    }
}
