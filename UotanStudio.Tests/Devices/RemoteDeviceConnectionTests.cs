using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Tcp;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class RemoteDeviceConnectionTests
{
    [Theory]
    [InlineData("192.0.2.1:5555", "192.0.2.1:5555")]
    [InlineData(" device.example:05555 ", "device.example:5555")]
    [InlineData("[2001:db8::1]:5555", "[2001:db8::1]:5555")]
    [InlineData("localhost:1", "localhost:1")]
    [InlineData("localhost:65535", "localhost:65535")]
    public void TryNormalize_ExplicitEndpoint_PreservesHostAndNormalizesPort(string input, string expected)
    {
        Assert.True(DebugBridgeNetworkAddress.TryNormalize(input, out var address));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("localhost:-1")]
    [InlineData("localhost:+5555")]
    [InlineData("localhost:5555 -remove")]
    [InlineData("localhost:5555\n")]
    [InlineData("localhost:5555\0")]
    [InlineData("tcp://localhost:5555")]
    [InlineData("user@localhost:5555")]
    [InlineData("localhost/path:5555")]
    [InlineData("::1:5555")]
    [InlineData("[localhost]:5555")]
    public void TryNormalize_InvalidOrAmbiguousEndpoint_RejectsBeforeNetworkAccess(string? input)
    {
        Assert.False(DebugBridgeNetworkAddress.TryNormalize(input, out var address));
        Assert.Empty(address);
    }

    [Theory]
    [InlineData("2001:db8::1", "[2001:db8::1]")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]")]
    [InlineData("fe80::1%eth0", "[fe80::1%eth0]")]
    [InlineData("192.0.2.1", "192.0.2.1")]
    [InlineData("device.example", "device.example")]
    public void FormatHost_Ipv6Literal_BracketsForServerEndpointGrammar(string host, string expected)
        => Assert.Equal(expected, DebugBridgeNetworkAddress.FormatHost(host));

    [Theory]
    [InlineData("2001:db8::1", 5555, "[2001:db8::1]:5555")]
    [InlineData("192.0.2.1", 5555, "192.0.2.1:5555")]
    [InlineData("device.example", 43111, "device.example:43111")]
    public void FormatEndpoint_Ipv6Host_ProducesSplitableEndpointIdentity(
        string host, int port, string expected)
        => Assert.Equal(expected, DebugBridgeNetworkAddress.FormatEndpoint(host, port));

    [Theory]
    [InlineData("[2001:db8::1]:5555", "2001:db8::1", 5555)]
    [InlineData("[fe80::1%eth0]:45123", "fe80::1%eth0", 45123)]
    [InlineData("192.0.2.1:5555", "192.0.2.1", 5555)]
    [InlineData("device.example:43111", "device.example", 43111)]
    public void TrySplitEndpoint_BracketedOrPlainIdentity_ReturnsBareHostAndPort(
        string endpoint, string expectedHost, int expectedPort)
    {
        Assert.True(DebugBridgeNetworkAddress.TrySplitEndpoint(endpoint, out var host, out var port));
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("[]:5555")]
    [InlineData("2001:db8::1")]
    public void TrySplitEndpoint_MissingOrInvalidPort_Rejects(string? endpoint)
    {
        Assert.False(DebugBridgeNetworkAddress.TrySplitEndpoint(endpoint, out var host, out var port));
        Assert.Empty(host);
        Assert.Equal(0, port);
    }

    [Fact]
    public void FormatEndpointThenTrySplitEndpoint_Ipv6RoundTripsToDialableBareHost()
    {
        var endpoint = DebugBridgeNetworkAddress.FormatEndpoint("fd56:1de:1157:0:a034:6903:6060:7323", 37811);

        Assert.True(DebugBridgeNetworkAddress.TrySplitEndpoint(endpoint, out var host, out var port));
        Assert.Equal("fd56:1de:1157:0:a034:6903:6060:7323", host);
        Assert.Equal(37811, port);
    }

    [Theory]
    [InlineData(DeviceProtocolKind.Adb, true)]
    [InlineData(DeviceProtocolKind.Hdc, false)]
    [InlineData(DeviceProtocolKind.Fastboot, false)]
    public void TryNormalizeNetworkAddress_ProtocolLimits_RejectsUnsupportedTargets(DeviceProtocolKind protocol, bool valid)
        => Assert.Equal(valid, DeviceMonitorService.TryNormalizeNetworkAddress(
            protocol, "long-device-hostname.example:5555", out _));

    [Theory]
    [InlineData(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "usb:one", "ADB", false)]
    [InlineData(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "adb:one", "ADB server", false)]
    [InlineData(DeviceProtocolKind.Hdc, DeviceTransportKind.Usb, "usb:one", "HDC", false)]
    [InlineData(DeviceProtocolKind.Hdc, DeviceTransportKind.Usb, "hdc:one", "HDC server", false)]
    [InlineData(DeviceProtocolKind.Adb, DeviceTransportKind.Tcp, "adb:192.0.2.1:5555", "ADB server", true)]
    [InlineData(DeviceProtocolKind.Hdc, DeviceTransportKind.Tcp, "hdc:192.0.2.1:8710", "HDC server", true)]
    [InlineData(DeviceProtocolKind.Fastboot, DeviceTransportKind.Usb, "usb:one", "Fastboot", false)]
    [InlineData(DeviceProtocolKind.Edl, DeviceTransportKind.Serial, "COM1", "EDL", false)]
    public void DeviceInfo_EndpointRouteAndTransport_SeparatesLabelsFromConnectionPermission(
        DeviceProtocolKind protocol, DeviceTransportKind transport, string key, string label, bool manageable)
    {
        var endpoint = new DetectedDevice
        {
            Key = key,
            Serial = "192.0.2.1:5555",
            Protocol = protocol,
            Transport = transport,
            State = "offline"
        };
        var info = new DeviceInfo(endpoint);

        Assert.Equal(label, info.Mode);
        Assert.StartsWith(label + " ", endpoint.ConnectionLabel);
        Assert.Equal(manageable, info.CanManageConnection);
        if (transport == DeviceTransportKind.Tcp)
        {
            Assert.Equal(endpoint.Serial, info.LinkPort);
        }
    }

    [Theory]
    [InlineData("connected to 192.0.2.1:5555", true)]
    [InlineData("already connected to 192.0.2.1:5555", true)]
    [InlineData("failed to connect to 192.0.2.1:5555", false)]
    [InlineData("cannot connect to 192.0.2.1:5555: refused", false)]
    [InlineData("", false)]
    public async Task AdbConnectAsync_OkayFollowedByResult_RequiresSuccessfulResult(string reply, bool succeeds)
    {
        using var listener = Listen();
        var server = ServeAdbAsync(listener, "host:connect:192.0.2.1:5555", "OKAY", reply);
        var connect = AdbServerServices.ConnectAsync("192.0.2.1:5555", "127.0.0.1", Port(listener),
            TestContext.Current.CancellationToken);
        if (succeeds)
        {
            Assert.Equal(reply, await connect);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => connect);
        }
        await server;
    }

    [Fact]
    public async Task AdbConnectAsync_FailStatus_PropagatesServerRejection()
    {
        using var listener = Listen();
        var server = ServeAdbAsync(listener, "host:connect:192.0.2.1:5555", "FAIL", "denied");
        var error = await Assert.ThrowsAsync<IOException>(() => AdbServerServices.ConnectAsync(
            "192.0.2.1:5555", "127.0.0.1", Port(listener), TestContext.Current.CancellationToken));
        Assert.Contains("denied", error.Message);
        await server;
    }

    [Theory]
    [InlineData("disconnected 192.0.2.1:5555", true)]
    [InlineData("error: no such device '192.0.2.1:5555'", false)]
    public async Task AdbDisconnectAsync_OkayFollowedByResult_RequiresSuccessfulResult(string reply, bool succeeds)
    {
        using var listener = Listen();
        var server = ServeAdbAsync(listener, "host:disconnect:192.0.2.1:5555", "OKAY", reply);
        var disconnect = AdbServerServices.DisconnectAsync("192.0.2.1:5555", "127.0.0.1", Port(listener),
            TestContext.Current.CancellationToken);
        if (succeeds)
        {
            await disconnect;
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => disconnect);
        }
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdbNetworkOperation_CanceledWhileReadingResult_ClosesConnection(bool disconnect)
    {
        using var listener = Listen();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var command = disconnect ? "host:disconnect:192.0.2.1:5555" : "host:connect:192.0.2.1:5555";
        var server = ServeAdbAsync(listener, command, "OKAY", null, cancellation);
        var operation = disconnect
            ? AdbServerServices.DisconnectAsync("192.0.2.1:5555", "127.0.0.1", Port(listener), cancellation.Token)
            : AdbServerServices.ConnectAsync("192.0.2.1:5555", "127.0.0.1", Port(listener), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await server;
    }

    [Theory]
    [InlineData("[Info]Connect OK", true)]
    [InlineData("[Info]Target is connected, repeat operation", true)]
    [InlineData("[Fail]Connect failed", false)]
    [InlineData("", false)]
    [InlineData("[Info]pending", false)]
    public async Task HdcConnectTargetAsync_HostCommand_RequiresCompletion(string reply, bool succeeds)
    {
        using var listener = Listen();
        var server = ServeHdcAsync(listener, ("tconn 192.0.2.1:8710", reply));
        using var client = new HdcServerClient("127.0.0.1", Port(listener));
        var connect = client.ConnectTargetAsync("192.0.2.1:8710", cancellationToken: TestContext.Current.CancellationToken);
        if (succeeds)
        {
            Assert.Equal(reply, await connect);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => connect);
        }
        await server;
    }

    [Theory]
    [InlineData("[Empty]")]
    [InlineData("192.0.2.1:8710 TCP Offline phone")]
    public async Task HdcConnectTargetAsync_DisconnectWithoutOutput_VerifiesTargetState(string listing)
    {
        using var listener = Listen();
        var server = ServeHdcAsync(listener,
            ("tconn 192.0.2.1:8710 -remove", ""),
            ("list targets -v", "192.0.2.1:8710 TCP Connected phone"),
            ("list targets -v", listing));
        using var client = new HdcServerClient("127.0.0.1", Port(listener));

        await client.ConnectTargetAsync("192.0.2.1:8710", disconnect: true,
            cancellationToken: TestContext.Current.CancellationToken);
        await server;
    }

    [Fact]
    public async Task HdcDisconnectAsync_UsbTransportWithColonInSerial_RejectsBeforeServerAccess()
    {
        var device = new DetectedDevice
        {
            Key = "hdc:usb:serial",
            Serial = "usb:serial",
            Protocol = DeviceProtocolKind.Hdc,
            Transport = DeviceTransportKind.Usb
        };

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new HdcDeviceService().DisconnectAsync(device, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("192.0.2.1", DebugBridgeNetworkAddress.DefaultDevicePort, "192.0.2.1:5555")]
    [InlineData(" device.example ", DebugBridgeNetworkAddress.DefaultDevicePort, "device.example:5555")]
    [InlineData("[2001:db8::1]", 5555, "[2001:db8::1]:5555")]
    [InlineData("192.0.2.1:9000", 5555, "192.0.2.1:9000")]
    [InlineData("192.0.2.1", 0, null)]
    [InlineData("::1", 5555, null)]
    [InlineData("192.0.2.1:65536", 5555, null)]
    public void TryNormalize_DefaultPort_AppliesOnlyToBareHost(string input, int defaultPort, string? expected)
    {
        Assert.Equal(expected is not null, DebugBridgeNetworkAddress.TryNormalize(input, defaultPort, out var address));
        Assert.Equal(expected ?? string.Empty, address);
    }

    [Theory]
    [InlineData(DeviceProtocolKind.Adb, "192.0.2.1", "192.0.2.1:5555")]
    [InlineData(DeviceProtocolKind.Adb, "192.0.2.1:7000", "192.0.2.1:7000")]
    [InlineData(DeviceProtocolKind.Hdc, "192.0.2.1", "192.0.2.1:5555")]
    public void TryNormalizeNetworkAddress_BareHost_AppliesDefaultDevicePort(
        DeviceProtocolKind protocol, string input, string expected)
    {
        Assert.True(DeviceMonitorService.TryNormalizeNetworkAddress(protocol, input, out var address));
        Assert.Equal(expected, address);
    }

    [Fact]
    public async Task AdbDirectNetworkSession_WithoutServer_CompletesHandshakeOverTcp()
    {
        using var listener = Listen();
        using var keepDaemonAlive = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var daemon = ServeDaemonHandshakeAsync(listener, keepDaemonAlive.Token);
        using var rsa = RSA.Create(2048);
        using var authentication = new AdbAuthentication(rsa);
        using var session = new AdbSession(new AdbTcpTransport("127.0.0.1", Port(listener)), authentication);

        await Task.Run(session.Connect, TestContext.Current.CancellationToken);

        Assert.True(session.IsConnected);
        Assert.False(session.UsesServer);
        keepDaemonAlive.Cancel();
        Assert.Equal(nameof(AdbCommand.Cnxn), await daemon);
    }

    [Fact]
    public void AdbTcpTransport_UnreachablePort_ReportsTransportFailureNotAggregateWrapper()
    {
        var listener = Listen();
        var port = Port(listener);
        listener.Stop();

        var error = Assert.ThrowsAny<Exception>(() =>
            new AdbTcpTransport("127.0.0.1", port, connectTimeoutMs: 1000));

        Assert.IsNotType<AggregateException>(error);
        Assert.True(error is SocketException or TimeoutException,
            $"Expected a socket or timeout failure, observed {error.GetType().Name}.");
    }

    private static async Task<string> ServeDaemonHandshakeAsync(TcpListener listener, CancellationToken keepAlive)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var socket = await listener.AcceptTcpClientAsync(token);
        var stream = socket.GetStream();
        var header = new byte[AdbConstants.HeaderSize];
        await stream.ReadExactlyAsync(header, token);
        var request = AdbMessaging.ParseHeader(header);
        var payload = new byte[(int)request.PayloadLength];
        await stream.ReadExactlyAsync(payload, token);
        await stream.WriteAsync(AdbMessaging.Serialize(new AdbMessage(
            AdbCommand.Cnxn, AdbConstants.ProtocolVersion, AdbConstants.MaxPayload,
            "device::ro.product.name=fake;"u8.ToArray())), token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, keepAlive));
        return request.Command.ToString() ?? string.Empty;
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static async Task ServeAdbAsync(TcpListener listener, string expectedCommand, string status,
        string? reply, CancellationTokenSource? cancellation = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var socket = await listener.AcceptTcpClientAsync(token);
        var stream = socket.GetStream();
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, token);
        var length = int.Parse(Encoding.ASCII.GetString(lengthBytes), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var command = new byte[length];
        await stream.ReadExactlyAsync(command, token);
        Assert.Equal(expectedCommand, Encoding.UTF8.GetString(command));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(status), token);
        if (reply is null)
        {
            await cancellation!.CancelAsync();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(reply);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("X4", CultureInfo.InvariantCulture)), token);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task ServeHdcAsync(TcpListener listener, params (string Command, string Reply)[] exchanges)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        foreach (var (command, reply) in exchanges)
        {
            using var socket = await listener.AcceptTcpClientAsync(token);
            var stream = socket.GetStream();
            var greeting = new byte[44];
            "OHOS HDC"u8.CopyTo(greeting);
            await WriteFrameAsync(stream, greeting, token);
            var hello = await ReadFrameAsync(stream, token);
            Assert.All(hello[12..44], value => Assert.Equal((byte)0, value));
            Assert.Equal(command + '\0', Encoding.UTF8.GetString(await ReadFrameAsync(stream, token)));
            if (reply.Length != 0)
            {
                await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(reply + '\0'), token);
            }
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        Assert.InRange(length, 1, 4096);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }
}