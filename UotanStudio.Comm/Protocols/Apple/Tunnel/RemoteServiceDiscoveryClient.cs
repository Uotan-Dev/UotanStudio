using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// The iOS 17+ RemoteServiceDiscovery (RSD) client: connects plain TCP to the device RSD endpoint
/// over the tunnel, performs the frame-order-sensitive RemoteXPC HTTP/2 handshake, sends the device
/// handshake and decodes the <c>peer_info</c> reply into a service name to port/properties table.
/// Service streams are then opened as direct TCP connections the OS routes through the tunnel.
/// <para>iOS 17+ 的 RemoteServiceDiscovery（RSD）客户端：经隧道以普通 TCP 连接设备 RSD 端点，执行
/// 帧顺序敏感的 RemoteXPC HTTP/2 握手，发送设备握手，并把 <c>peer_info</c> 回复解码为服务名到
/// 端口/属性的表。随后以 OS 经隧道路由的直连 TCP 打开服务流。</para>
/// </summary>
public sealed class RemoteServiceDiscoveryClient : IDisposable
{
    /// <summary>Gets the RSD TCP port remoted exposes on the device (see the upstream RSDPort).
    /// <para>获取设备上 remoted 暴露的 RSD TCP 端口（见上游 RSDPort）。</para></summary>
    public const int DefaultPort = 58783;

    private const int RootChannel = 1;
    private const int ReplyChannel = 3;
    private const uint TerminateFlags = 0x0201;
    private const ulong MessagingProtocolVersion = 7;
    private const ulong RemoteXpcVersionFlags = 0x0100000000000006;

    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new Dictionary<string, object?>();

    private readonly Http2Client _http2;
    private readonly string _serverAddress;
    private readonly MemoryStream _reassembly = new();
    private ulong _rootMessageId;
    private bool _disposed;

    private RemoteServiceDiscoveryClient(Http2Client http2, string serverAddress)
    {
        _http2 = http2;
        _serverAddress = serverAddress;
        Services = new Dictionary<string, (int, IReadOnlyDictionary<string, object?>)>();
    }

    /// <summary>Gets the raw <c>peer_info</c> dictionary from the handshake.
    /// <para>获取握手返回的原始 <c>peer_info</c> 字典。</para></summary>
    public IReadOnlyDictionary<string, object?>? PeerInfo { get; private set; }

    /// <summary>Gets the advertised services keyed by name with their TCP port and properties.
    /// <para>获取按名称索引的服务表，含 TCP 端口与属性。</para></summary>
    public IReadOnlyDictionary<string, (int Port, IReadOnlyDictionary<string, object?> Properties)> Services
    {
        get;
        private set;
    }

    /// <summary>
    /// Connects to the RSD endpoint and completes the RemoteXPC handshake.
    /// <para>连接 RSD 端点并完成 RemoteXPC 握手。</para>
    /// </summary>
    /// <param name="serverAddress">The device IPv6 address reported by the tunnel handshake.
    /// <para>隧道握手上报的设备 IPv6 地址。</para></param>
    /// <param name="port">The RSD TCP port; <see cref="DefaultPort"/> when zero.
    /// <para>RSD TCP 端口；为零时用 <see cref="DefaultPort"/>。</para></param>
    /// <param name="cancellationToken">Cancels the connect and handshake. <para>取消连接与握手。</para></param>
    /// <returns>The connected client. <para>已连接的客户端。</para></returns>
    /// <exception cref="IOException">The endpoint is unreachable or the handshake failed.
    /// <para>端点不可达或握手失败时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public static RemoteServiceDiscoveryClient Connect(string serverAddress, int port,
        CancellationToken cancellationToken)
    {
        var endpointPort = port == 0 ? DefaultPort : port;
        var socket = OpenSocket(serverAddress, endpointPort, cancellationToken);
        var stream = new NetworkStream(socket, ownsSocket: true);
        Http2Client? http2 = null;
        try
        {
            http2 = new Http2Client(stream, completeSettingsExchange: false);
            var client = new RemoteServiceDiscoveryClient(http2, serverAddress);
            client.PerformHandshake(cancellationToken);
            return client;
        }
        catch
        {
            if (http2 is null)
            {
                stream.Dispose();
            }
            else
            {
                http2.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves the TCP port a service listens on.
    /// <para>解析服务监听的 TCP 端口。</para>
    /// </summary>
    /// <param name="serviceName">The service name. <para>服务名。</para></param>
    /// <returns>The device-side TCP port. <para>设备侧 TCP 端口。</para></returns>
    /// <exception cref="KeyNotFoundException">The device does not offer the service.
    /// <para>设备未提供该服务时抛出。</para></exception>
    public int GetServicePort(string serviceName) =>
        Services.TryGetValue(serviceName, out var entry)
            ? entry.Port
            : throw new KeyNotFoundException($"The device does not offer the {serviceName} service.");

    /// <summary>
    /// Opens a raw TCP stream to the service port; the operating system routes it through the tunnel.
    /// <para>打开到服务端口的原始 TCP 流；操作系统会经隧道路由。</para>
    /// </summary>
    /// <param name="serviceName">The service name. <para>服务名。</para></param>
    /// <param name="cancellationToken">Cancels the connect. <para>取消连接。</para></param>
    /// <returns>The connected stream. <para>已连接的流。</para></returns>
    /// <exception cref="KeyNotFoundException">The device does not offer the service.
    /// <para>设备未提供该服务时抛出。</para></exception>
    /// <exception cref="IOException">The service port refused the connection.
    /// <para>服务端口拒绝连接时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public Stream OpenServiceStream(string serviceName, CancellationToken cancellationToken)
    {
        var port = GetServicePort(serviceName);
        var socket = OpenSocket(_serverAddress, port, cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http2.Dispose();
        _reassembly.Dispose();
    }

    private void PerformHandshake(CancellationToken cancellationToken)
    {
        _http2.SendEndHeaders(RootChannel);
        SendRootRequest(new Dictionary<string, object?>(), wantingReply: false, cancellationToken);
        _http2.SendEndHeaders(ReplyChannel);
        _http2.SendData(RootChannel, XpcCodec.BuildHeaderWrapper((XpcFlags)TerminateFlags));
        _http2.SendData(ReplyChannel,
            XpcCodec.BuildHeaderWrapper(XpcFlags.AlwaysSet | XpcFlags.InitHandshake));
        _http2.CompleteSettingsExchange(cancellationToken);

        var handshake = new Dictionary<string, object?>
        {
            ["MessageType"] = "Handshake",
            ["MessagingProtocolVersion"] = XpcCodec.UInt64(MessagingProtocolVersion),
            ["UUID"] = Guid.NewGuid(),
            ["Properties"] = new Dictionary<string, object?>
            {
                ["RemoteXpcVersionFlags"] = XpcCodec.UInt64(RemoteXpcVersionFlags),
                ["SensitivePropertiesVisible"] = true,
            },
            ["Services"] = new Dictionary<string, object?>(),
        };
        SendRootRequest(handshake, wantingReply: true, cancellationToken);
        PeerInfo = ReceivePeerInfo(cancellationToken);
        Services = BuildServices(PeerInfo);
    }

    private void SendRootRequest(IReadOnlyDictionary<string, object?> dictionary, bool wantingReply,
        CancellationToken cancellationToken)
    {
        _http2.SendData(RootChannel, XpcCodec.BuildWrapper(dictionary, _rootMessageId, wantingReply));
        _rootMessageId = (_rootMessageId + 1) | 1;
    }

    private IReadOnlyDictionary<string, object?> ReceivePeerInfo(CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = _http2.ReadFrame(cancellationToken);
            if (frame.Type != Http2Frame.TypeData)
            {
                continue;
            }

            _reassembly.Write(frame.Payload);
            IReadOnlyDictionary<string, object?>? dictionary;
            try
            {
                dictionary = XpcCodec.ParseWrapper(_reassembly.GetBuffer().AsSpan(0, (int)_reassembly.Length),
                    out var consumed);
                if (consumed > 0)
                {
                    RemoveConsumed(consumed);
                }
            }
            catch (InvalidDataException)
            {
                throw new IOException("The RSD endpoint sent a malformed RemoteXPC frame.");
            }

            if (dictionary is null || dictionary.Count == 0)
            {
                continue;
            }

            return dictionary;
        }
    }

    private void RemoveConsumed(int consumed)
    {
        var remaining = (int)_reassembly.Length - consumed;
        if (remaining > 0)
        {
            Buffer.BlockCopy(_reassembly.GetBuffer(), consumed, _reassembly.GetBuffer(), 0, remaining);
        }

        _reassembly.SetLength(remaining);
    }

    private static IReadOnlyDictionary<string, (int Port, IReadOnlyDictionary<string, object?> Properties)>
        BuildServices(IReadOnlyDictionary<string, object?> peerInfo)
    {
        if (!peerInfo.TryGetValue("Services", out var servicesValue) ||
            servicesValue is not IReadOnlyDictionary<string, object?> services)
        {
            throw new IOException("The RSD peer info lacks the Services dictionary.");
        }

        var result = new Dictionary<string, (int, IReadOnlyDictionary<string, object?>)>(services.Count);
        foreach (var (name, entry) in services)
        {
            if (entry is not IReadOnlyDictionary<string, object?> entryDictionary ||
                !entryDictionary.TryGetValue("Port", out var portValue))
            {
                throw new IOException($"The RSD peer info entry for {name} lacks a port.");
            }

            var properties = entryDictionary.TryGetValue("Properties", out var propertiesValue) &&
                propertiesValue is IReadOnlyDictionary<string, object?> propertyDictionary
                    ? propertyDictionary
                    : EmptyProperties;
            result[name] = (ReadPort(name, portValue), properties);
        }

        return result;
    }

    private static int ReadPort(string name, object? value) => value switch
    {
        XpcCodec.XpcUInt64 unsigned => checked((int)unsigned.Value),
        XpcCodec.XpcInt64 signed => checked((int)signed.Value),
        long integer => checked((int)integer),
        int integer => integer,
        _ => throw new IOException($"The RSD peer info entry for {name} carries a non-numeric port."),
    };

    private static Socket OpenSocket(string serverAddress, int port, CancellationToken cancellationToken)
    {
        var address = IPAddress.Parse(serverAddress);
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new IOException($"The tunnel server address {serverAddress} is not an IPv6 address.");
        }

        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}