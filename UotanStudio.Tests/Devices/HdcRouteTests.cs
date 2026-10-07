using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class HdcRouteTests
{
    [Fact]
    public async Task RunTaskAsync_ServerPresent_SendsTargetBoundHostCommandWithoutExecutable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var received = Task.Run(async () =>
        {
            using (var probe = await listener.AcceptTcpClientAsync(ct)) { }
            using var peer = await listener.AcceptTcpClientAsync(ct);
            var stream = peer.GetStream();
            var banner = new byte[44];
            Encoding.ASCII.GetBytes("OHOS HDC", banner);
            await WriteFrameAsync(stream, banner, ct);
            var hello = await ReadFrameAsync(stream, ct);
            var command = await ReadFrameAsync(stream, ct);
            await WriteFrameAsync(stream, Encoding.UTF8.GetBytes("Forwardport result:OK"), ct);
            return (Key: Encoding.UTF8.GetString(hello.AsSpan(12, 32)).TrimEnd('\0'),
                Command: Encoding.UTF8.GetString(command).TrimEnd('\0'));
        }, ct);

        var service = new HdcDeviceService(port: port);
        var result = await service.RunTaskAsync(Target(DeviceTransportKind.Usb),
            ["fport", "tcp:1234", "tcp:4321"], ct);
        var request = await received;
        Assert.Equal("target", request.Key);
        Assert.Equal("fport tcp:1234 tcp:4321", request.Command);
        Assert.Equal("Forwardport result:OK", result);
    }

    [Fact]
    public async Task SendFileAsync_ServerPresent_QuotesLocalPathOnHostChannel()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var received = Task.Run(async () =>
        {
            using (var probe = await listener.AcceptTcpClientAsync(ct)) { }
            using var peer = await listener.AcceptTcpClientAsync(ct);
            var stream = peer.GetStream();
            var banner = new byte[44];
            Encoding.ASCII.GetBytes("OHOS HDC", banner);
            await WriteFrameAsync(stream, banner, ct);
            var hello = await ReadFrameAsync(stream, ct);
            var command = await ReadFrameAsync(stream, ct);
            await WriteFrameAsync(stream, Encoding.UTF8.GetBytes("[Info]transfer complete"), ct);
            return (Key: Encoding.UTF8.GetString(hello.AsSpan(12, 32)).TrimEnd('\0'),
                Command: Encoding.UTF8.GetString(command).TrimEnd('\0'));
        }, ct);
        var folder = Path.Combine(Path.GetTempPath(), "uotan hdc " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var local = Path.Combine(folder, "source file.txt");
            await File.WriteAllTextAsync(local, "test", ct);
            var service = new HdcDeviceService(port: port);
            Assert.Contains("transfer complete",
                await service.SendFileAsync(Target(DeviceTransportKind.Usb), local, "/data/local/tmp/test", ct));
            var request = await received;
            Assert.Equal("target", request.Key);
            Assert.Equal($"file send \"{local}\" /data/local/tmp/test", request.Command);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ReceiveFileAsync_ServerPresent_PublishesStagedHostFile()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var folder = Path.Combine(Path.GetTempPath(), "uotan hdc " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var destination = Path.Combine(folder, "received file.txt");
            var received = Task.Run(async () =>
            {
                using (var probe = await listener.AcceptTcpClientAsync(ct)) { }
                using var peer = await listener.AcceptTcpClientAsync(ct);
                var stream = peer.GetStream();
                var banner = new byte[44];
                Encoding.ASCII.GetBytes("OHOS HDC", banner);
                await WriteFrameAsync(stream, banner, ct);
                var hello = await ReadFrameAsync(stream, ct);
                var command = Encoding.UTF8.GetString(await ReadFrameAsync(stream, ct)).TrimEnd('\0');
                var prefix = "file recv /system/bin/toybox \"";
                Assert.StartsWith(prefix, command);
                Assert.EndsWith("\"", command);
                var staged = command[prefix.Length..^1];
                Assert.StartsWith(Path.GetFullPath(folder), staged, StringComparison.Ordinal);
                await File.WriteAllTextAsync(staged, "received", ct);
                await WriteFrameAsync(stream, Encoding.UTF8.GetBytes("[Info]transfer complete"), ct);
                return Encoding.UTF8.GetString(hello.AsSpan(12, 32)).TrimEnd('\0');
            }, ct);
            var service = new HdcDeviceService(port: port);
            await service.ReceiveFileAsync(Target(DeviceTransportKind.Usb), "/system/bin/toybox", destination, ct);
            Assert.Equal("target", await received);
            Assert.Equal("received", await File.ReadAllTextAsync(destination, ct));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ReceiveFileAsync_ServerFailure_PreservesExistingDestination()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var folder = Path.Combine(Path.GetTempPath(), "uotan-hdc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var destination = Path.Combine(folder, "existing.bin");
            await File.WriteAllTextAsync(destination, "original", ct);
            var server = Task.Run(async () =>
            {
                using (var probe = await listener.AcceptTcpClientAsync(ct)) { }
                using var peer = await listener.AcceptTcpClientAsync(ct);
                var stream = peer.GetStream();
                var banner = new byte[44];
                Encoding.ASCII.GetBytes("OHOS HDC", banner);
                await WriteFrameAsync(stream, banner, ct);
                _ = await ReadFrameAsync(stream, ct);
                _ = await ReadFrameAsync(stream, ct);
                await WriteFrameAsync(stream, Encoding.UTF8.GetBytes("[Fail]remote file not found"), ct);
            }, ct);
            var service = new HdcDeviceService(port: port);
            await Assert.ThrowsAsync<IOException>(() =>
                service.ReceiveFileAsync(Target(DeviceTransportKind.Usb), "/missing", destination, ct));
            await server;
            Assert.Equal("original", await File.ReadAllTextAsync(destination, ct));
            Assert.Single(Directory.GetFiles(folder));
            Assert.Empty(Directory.GetDirectories(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task RunInteractiveShellAsync_ServerPresent_RelaysInputAndOutputFrames()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var received = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct);
            var stream = peer.GetStream();
            var banner = new byte[44];
            Encoding.ASCII.GetBytes("OHOS HDC", banner);
            await WriteFrameAsync(stream, banner, ct);
            var hello = await ReadFrameAsync(stream, ct);
            var command = await ReadFrameAsync(stream, ct);
            var input = await ReadFrameAsync(stream, ct);
            await WriteFrameAsync(stream, Encoding.UTF8.GetBytes("ready\r\n"), ct);
            return (Key: Encoding.UTF8.GetString(hello.AsSpan(12, 32)).TrimEnd('\0'),
                Command: Encoding.UTF8.GetString(command),
                Input: Encoding.UTF8.GetString(input));
        }, ct);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("echo ready\n"));
        using var output = new MemoryStream();
        using var client = new HdcServerClient(port: port);
        await client.RunInteractiveShellAsync("target", input, output, ct);
        var request = await received;
        Assert.Equal("target", request.Key);
        Assert.Equal("shell\0", request.Command);
        Assert.Equal("echo ready\n", request.Input);
        Assert.Equal("ready\r\n", Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length));
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    private static DetectedDevice Target(DeviceTransportKind transport) => new()
    {
        Key = "hdc:target",
        Serial = "target",
        Protocol = DeviceProtocolKind.Hdc,
        Transport = transport
    };

    [Fact]
    public void UseHdcServerFor_ServerPresent_RoutesUsbAndNetworkThroughServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(DeviceMonitorService.UseHdcServerFor(Target(DeviceTransportKind.Usb), "127.0.0.1", port));
        Assert.True(DeviceMonitorService.UseHdcServerFor(Target(DeviceTransportKind.Tcp), "127.0.0.1", port));
    }

    [Fact]
    public void UseHdcServerFor_ServerAbsent_OnlyUsbCanUseDirectTransport()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.False(DeviceMonitorService.UseHdcServerFor(Target(DeviceTransportKind.Usb), "127.0.0.1", port));
        Assert.Throws<IOException>(() =>
            DeviceMonitorService.UseHdcServerFor(Target(DeviceTransportKind.Tcp), "127.0.0.1", port));
    }
}