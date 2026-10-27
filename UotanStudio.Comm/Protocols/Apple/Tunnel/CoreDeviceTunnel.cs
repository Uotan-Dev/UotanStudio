using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// The iOS 17.4+ CoreDevice tunnel carried by the lockdown service
/// <c>com.apple.internal.devicecompute.CoreDeviceProxy</c>: the CDTunnel handshake negotiates the
/// client IPv6 address, MTU, device address and RSD port, then the same stream carries raw IPv6
/// packets in both directions without further framing, pumped between the stream and a TUN device
/// by two threads.
/// <para>iOS 17.4+ 的 CoreDevice 隧道，承载于 lockdown 服务
/// <c>com.apple.internal.devicecompute.CoreDeviceProxy</c>：CDTunnel 握手协商本端 IPv6 地址、MTU、
/// 设备地址与 RSD 端口；此后同一流双向承载原始 IPv6 报文（无额外分帧），由两个线程在流与 TUN
/// 设备之间搬运。</para>
/// </summary>
internal sealed class CoreDeviceTunnel : IDisposable
{
    /// <summary>Gets the MTU requested from the device in the CDTunnel handshake.
    /// <para>获取在 CDTunnel 握手中向设备请求的 MTU。</para></summary>
    public const int RequestedMtu = 16000;

    /// <summary>Gets the on-link prefix length applied to the negotiated client address.
    /// <para>获取应用于协商所得本端地址的链路前缀长度。</para></summary>
    public const int DefaultPrefixLength = 64;

    private const int Ipv6HeaderSize = 40;
    private const string Magic = "CDTunnel";
    private const int FrameHeaderSize = 10;

    private readonly Stream _stream;
    private readonly Lock _gate = new();
    private Thread? _streamToTunThread;
    private Thread? _tunToStreamThread;
    private ITunDevice? _device;
    private volatile bool _disposed;

    /// <summary>
    /// Creates the tunnel over one opened CoreDeviceProxy service stream.
    /// <para>在一条已打开的 CoreDeviceProxy 服务流上创建隧道。</para>
    /// </summary>
    /// <param name="stream">The stream returned by
    /// <c>IAppleDeviceSession.OpenService</c>. <para>由 <c>IAppleDeviceSession.OpenService</c>
    /// 返回的流。</para></param>
    public CoreDeviceTunnel(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// One established tunnel: the negotiated endpoints plus the TUN device carrying the packets.
    /// <para>一条已建立的隧道：协商出的端点信息与承载报文的 TUN 设备。</para>
    /// </summary>
    /// <param name="ClientAddress">The host-side IPv6 address to configure on the TUN device.
    /// <para>需要在 TUN 设备上配置的主机侧 IPv6 地址。</para></param>
    /// <param name="ServerAddress">The device-side IPv6 address hosting the RSD endpoint.
    /// <para>承载 RSD 端点的设备侧 IPv6 地址。</para></param>
    /// <param name="ServerRsdPort">The TCP port of the RemoteServiceDiscovery endpoint.
    /// <para>RemoteServiceDiscovery 端点的 TCP 端口。</para></param>
    /// <param name="Mtu">The negotiated tunnel MTU; the TUN device is configured with it and it is
    /// never below the IPv6 minimum of 1280. <para>协商的隧道 MTU；TUN 设备按其配置，且不会低于
    /// IPv6 最小值 1280。</para></param>
    /// <param name="Device">The opened TUN device; owned by this tunnel and released on
    /// <see cref="Dispose"/>. <para>已打开的 TUN 设备；由本隧道持有，<see cref="Dispose"/> 时释放。</para></param>
    public sealed record TunnelResult(
        string ClientAddress,
        string ServerAddress,
        int ServerRsdPort,
        int Mtu,
        ITunDevice Device);

    /// <summary>
    /// Runs the CDTunnel handshake, opens the platform TUN device for the negotiated client address
    /// and MTU and starts the packet pumps.
    /// <para>执行 CDTunnel 握手，按协商的本端地址与 MTU 打开平台 TUN 设备并启动报文泵。</para>
    /// </summary>
    /// <param name="interfaceName">The requested TUN interface name. <para>请求的 TUN 接口名。</para></param>
    /// <param name="cancellationToken">Cancels the handshake and configuration.
    /// <para>取消握手与配置。</para></param>
    /// <returns>The established tunnel. <para>已建立的隧道。</para></returns>
    /// <exception cref="IOException">The handshake or the TUN configuration failed.
    /// <para>握手或 TUN 配置失败时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public TunnelResult Establish(string interfaceName, CancellationToken cancellationToken)
    {
        var handshake = Handshake(cancellationToken);
        var device = TunDeviceFactory.Open(
            interfaceName, handshake.ClientAddress, DefaultPrefixLength, cancellationToken, handshake.Mtu);
        try
        {
            StartPumps(device, cancellationToken);
        }
        catch
        {
            device.Dispose();
            throw;
        }

        return new TunnelResult(
            handshake.ClientAddress, handshake.ServerAddress, handshake.ServerRsdPort, handshake.Mtu, device);
    }

    /// <summary>
    /// Runs the CDTunnel handshake and starts the packet pumps over an already opened TUN device;
    /// the device's ownership transfers to this tunnel.
    /// <para>执行 CDTunnel 握手并在已打开的 TUN 设备上启动报文泵；设备所有权转移给本隧道。</para>
    /// </summary>
    /// <param name="device">The TUN device already configured with the negotiated client address.
    /// <para>已按协商本端地址配置好的 TUN 设备。</para></param>
    /// <param name="cancellationToken">Cancels the handshake. <para>取消握手。</para></param>
    /// <returns>The established tunnel. <para>已建立的隧道。</para></returns>
    /// <exception cref="IOException">The handshake failed. <para>握手失败时抛出。</para></exception>
    public TunnelResult Establish(ITunDevice device, CancellationToken cancellationToken)
    {
        var handshake = Handshake(cancellationToken);
        StartPumps(device, cancellationToken);
        return new TunnelResult(
            handshake.ClientAddress, handshake.ServerAddress, handshake.ServerRsdPort, handshake.Mtu, device);
    }

    /// <summary>
    /// Exchanges the CDTunnel handshake request and response over the service stream.
    /// <para>在服务流上交换 CDTunnel 握手请求与响应。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交换。</para></param>
    /// <returns>The negotiated parameters. <para>协商出的参数。</para></returns>
    /// <exception cref="IOException">The device sent a malformed or missing response.
    /// <para>设备返回畸形或缺失的响应时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public HandshakeResponse Handshake(CancellationToken cancellationToken)
    {
        var request = RemotePairingJson.Encode(new Dictionary<string, object?>
        {
            ["type"] = "clientHandshakeRequest",
            ["mtu"] = (long)RequestedMtu,
        });
        WriteCdtunnelFrame(request, cancellationToken);

        var header = new byte[FrameHeaderSize];
        if (!TryReadExactly(header, cancellationToken))
        {
            throw new IOException("The CoreDeviceProxy stream ended before the CDTunnel handshake response.");
        }

        if (Encoding.ASCII.GetString(header, 0, Magic.Length) != Magic)
        {
            throw new IOException("The CoreDeviceProxy handshake response lacks the CDTunnel magic.");
        }

        var bodyLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(Magic.Length, 2));
        if (bodyLength == 0)
        {
            throw new IOException("The CoreDeviceProxy handshake response carries an empty body.");
        }

        var body = new byte[bodyLength];
        if (!TryReadExactly(body, cancellationToken))
        {
            throw new IOException("The CoreDeviceProxy stream ended inside the CDTunnel handshake response.");
        }
        if (RemotePairingJson.Decode(body) is not IReadOnlyDictionary<string, object?> response)
        {
            throw new IOException("The CDTunnel handshake response is not a JSON object.");
        }

        var clientParameters = response.TryGetValue("clientParameters", out var parameters) &&
            parameters is IReadOnlyDictionary<string, object?> parameterDictionary
                ? parameterDictionary
                : throw new IOException("The CDTunnel handshake response lacks clientParameters.");
        var clientAddress = ReadText(clientParameters, "address");
        var mtu = ReadInteger(clientParameters, "mtu");
        var serverAddress = ReadText(response, "serverAddress");
        var serverRsdPort = ReadInteger(response, "serverRSDPort");
        return new HandshakeResponse(clientAddress, (int)mtu, serverAddress, (int)serverRsdPort);
    }

    /// <summary>
    /// Starts the two pump threads moving raw IPv6 packets between the service stream and the TUN
    /// device; the stream→TUN pump forwards only IPv6 packets, the TUN→stream pump forwards packets
    /// whose IP version nibble is 6.
    /// <para>启动两个泵线程，在服务流与 TUN 设备之间搬运原始 IPv6 报文；stream→TUN 泵只转发 IPv6
    /// 报文，TUN→stream 泵只转发 IP 版本号为 6 的报文。</para>
    /// </summary>
    /// <param name="device">The TUN device; ownership transfers to this tunnel.
    /// <para>TUN 设备；所有权转移给本隧道。</para></param>
    /// <param name="cancellationToken">Cancels both pumps cooperatively; disposing the tunnel or
    /// closing the stream forces them to stop. <para>协作式取消两个泵；释放隧道或关闭流会强制停止。</para></param>
    /// <exception cref="InvalidOperationException">The pumps are already running.
    /// <para>泵已在运行时抛出。</para></exception>
    public void StartPumps(ITunDevice device, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_streamToTunThread is not null)
            {
                throw new InvalidOperationException("The CoreDevice tunnel pumps are already running.");
            }

            _device = device;
            _streamToTunThread = new Thread(() => PumpStreamToTun(device, cancellationToken))
            {
                IsBackground = true,
                Name = "uotan-apple-tunnel-stream-to-tun",
            };
            _tunToStreamThread = new Thread(() => PumpTunToStream(device, cancellationToken))
            {
                IsBackground = true,
                Name = "uotan-apple-tunnel-tun-to-stream",
            };
            _streamToTunThread.Start();
            _tunToStreamThread.Start();
        }
    }

    /// <summary>
    /// Stops the pumps, releases the TUN device and closes the service stream; safe to call twice.
    /// <para>停止泵、释放 TUN 设备并关闭服务流；可安全重复调用。</para>
    /// </summary>
    public void Dispose()
    {
        Thread? streamToTun;
        Thread? tunToStream;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            streamToTun = _streamToTunThread;
            tunToStream = _tunToStreamThread;
        }

        _stream.Dispose();
        streamToTun?.Join(TimeSpan.FromSeconds(2));
        tunToStream?.Join(TimeSpan.FromSeconds(2));
        _device?.Dispose();
    }

    private void PumpStreamToTun(ITunDevice device, CancellationToken cancellationToken)
    {
        var packet = new byte[Ipv6HeaderSize + ushort.MaxValue];
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                if (!TryReadExactly(packet.AsMemory(0, Ipv6HeaderSize), cancellationToken))
                {
                    return;
                }

                var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
                var total = Ipv6HeaderSize + payloadLength;
                if (payloadLength > 0 &&
                    !TryReadExactly(packet.AsMemory(Ipv6HeaderSize, payloadLength), cancellationToken))
                {
                    return;
                }

                device.SendPacket(packet.AsSpan(0, total));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
            or OperationCanceledException)
        {
        }
    }

    private void PumpTunToStream(ITunDevice device, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                var packet = device.TryReceivePacket(50);
                if (packet is null || packet.Length == 0 || (packet[0] >> 4) != 6)
                {
                    continue;
                }

                _stream.WriteAsync(packet, cancellationToken).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
            or OperationCanceledException)
        {
        }
    }

    private void WriteCdtunnelFrame(byte[] body, CancellationToken cancellationToken)
    {
        var frame = new byte[FrameHeaderSize + body.Length];
        Encoding.ASCII.GetBytes(Magic).CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Magic.Length, 2), (ushort)body.Length);
        body.CopyTo(frame, FrameHeaderSize);
        _stream.WriteAsync(frame, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    private bool TryReadExactly(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = _stream.ReadAsync(buffer[offset..], cancellationToken).AsTask().GetAwaiter().GetResult();
            if (count <= 0)
            {
                return false;
            }

            offset += count;
        }

        return true;
    }

    private static string ReadText(IReadOnlyDictionary<string, object?> dictionary, string key) =>
        dictionary.TryGetValue(key, out var value) && value is string text
            ? text
            : throw new IOException($"The CDTunnel handshake response lacks the {key} text.");

    private static long ReadInteger(IReadOnlyDictionary<string, object?> dictionary, string key) =>
        dictionary.TryGetValue(key, out var value) && value is long integer
            ? integer
            : throw new IOException($"The CDTunnel handshake response lacks the {key} number.");

    /// <summary>The negotiated tunnel parameters. <para>协商出的隧道参数。</para></summary>
    /// <param name="ClientAddress">The host-side IPv6 address. <para>主机侧 IPv6 地址。</para></param>
    /// <param name="Mtu">The negotiated MTU. <para>协商的 MTU。</para></param>
    /// <param name="ServerAddress">The device-side IPv6 address. <para>设备侧 IPv6 地址。</para></param>
    /// <param name="ServerRsdPort">The device RSD TCP port. <para>设备 RSD TCP 端口。</para></param>
    internal sealed record HandshakeResponse(
        string ClientAddress,
        int Mtu,
        string ServerAddress,
        int ServerRsdPort);
}