using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// Owns one loopback TCP forward into an HDC target and returns the connected stream to the caller.
/// The forward is registered on a direct HDC session through its channel manager, or through the hdc
/// server's <c>fport</c> command when the session is server-backed; the loopback socket, the free-port
/// choice and the teardown all stay inside Comm.
/// <para>持有一条指向 HDC 目标的回环 TCP 转发，并把连接后的流返回给调用方。直连 HDC 会话经其通道管理器登记转发，
/// server 会话则经 hdc server 的 <c>fport</c> 命令登记；回环套接字、空闲端口选择与清理全部留在 Comm 内。</para>
/// </summary>
public sealed class HdcForwardTunnel : IDisposable
{
    private readonly HdcSession _session;
    private readonly string _connectKey;
    private readonly int _remotePort;
    private readonly int _requestedLocalPort;
    private readonly int _timeoutMs;
    private readonly Lock _gate = new();
    private TcpClient? _client;
    private int _localPort;
    private bool _mapped;
    private bool _directMapping;
    private int _disposed;

    /// <summary>
    /// Creates a tunnel over an already connected HDC session.
    /// <para>在已连接的 HDC 会话上创建通道。</para>
    /// </summary>
    /// <param name="session">Connected HDC session owning the forward or the host channel.
    /// <para>持有转发或主机通道的已连接 HDC 会话。</para></param>
    /// <param name="connectKey">Target connect key used by the server-backed route.
    /// <para>server 路由使用的目标连接键。</para></param>
    /// <param name="remotePort">Device-side TCP port to reach through the forward.
    /// <para>经转发访问的设备侧 TCP 端口。</para></param>
    /// <param name="requestedLocalPort">Preferred loopback port; zero picks a free port.
    /// <para>首选回环端口；为零时自动选择空闲端口。</para></param>
    /// <param name="timeoutMs">Budget for the forward and the loopback connect.
    /// <para>建立转发与连接回环端口的预算。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    public HdcForwardTunnel(HdcSession session, string connectKey, int remotePort,
        int requestedLocalPort = 0, int timeoutMs = 20_000)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(remotePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remotePort, 65535);
        _session = session;
        _connectKey = connectKey;
        _remotePort = remotePort;
        _requestedLocalPort = requestedLocalPort;
        _timeoutMs = Math.Max(timeoutMs, 1_000);
    }

    /// <summary>
    /// Registers the forward when needed and returns one connected loopback stream.
    /// <para>按需登记转发并返回一条已连接的回环流。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the forward setup and the connect retries.
    /// <para>取消转发建立与连接重试。</para></param>
    /// <returns>The connected stream whose disposal leaves the forward registered until this tunnel is disposed.
    /// <para>已连接的流；释放该流不会移除转发，转发由本通道释放时移除。</para></returns>
    /// <exception cref="ObjectDisposedException">The tunnel is disposed. <para>通道已释放。</para></exception>
    /// <exception cref="IOException">The forward or the loopback connect failed.
    /// <para>转发或回环连接失败。</para></exception>
    public Stream Open(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_client is not null)
            {
                throw new InvalidOperationException("The HDC forward tunnel is already open.");
            }

            EnsureMapping(cancellationToken);
            var deadline = Environment.TickCount64 + _timeoutMs;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var client = new TcpClient { NoDelay = true };
                try
                {
                    client.Connect(IPAddress.Loopback, _localPort);
                    _client = client;
                    return client.GetStream();
                }
                catch (SocketException error)
                {
                    client.Dispose();
                    if (Environment.TickCount64 >= deadline)
                    {
                        throw new IOException(
                            $"The HDC forward on 127.0.0.1:{_localPort} did not accept the connection within {_timeoutMs} ms.",
                            error);
                    }

                    Thread.Sleep(250);
                }
            }
        }
    }

    private void EnsureMapping(CancellationToken cancellationToken)
    {
        if (_mapped)
        {
            return;
        }

        _localPort = _requestedLocalPort > 0 ? _requestedLocalPort : FindFreeLoopbackPort();
        try
        {
            _session.Forwards.Add(_localPort, _remotePort, cancellationToken: cancellationToken);
            _directMapping = true;
            _mapped = true;
            return;
        }
        catch (NotSupportedException)
        {
        }

        using var server = new HdcServerClient(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.HdcServerPort);
        var output = server.ExecuteOnDevice(_connectKey,
            $"fport tcp:{_localPort} tcp:{_remotePort}", _timeoutMs);
        var text = System.Text.Encoding.UTF8.GetString(output).TrimEnd('\0');
        if (!text.Contains("OK", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            HdcServerClient.ThrowOnFailure(text);
            throw new IOException($"The HDC server did not confirm the forward: {text.Trim()}.");
        }

        _mapped = true;
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Closes the loopback client and removes the forward it registered.
    /// <para>关闭回环客户端并移除其登记的转发。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TcpClient? client;
        int port;
        bool mapped;
        bool direct;
        lock (_gate)
        {
            client = _client;
            _client = null;
            port = _localPort;
            mapped = _mapped;
            direct = _directMapping;
            _mapped = false;
        }

        client?.Dispose();
        if (!mapped)
        {
            return;
        }

        try
        {
            if (direct)
            {
                _session.Forwards.Remove($"tcp:{port}");
                return;
            }

            using var server = new HdcServerClient(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.HdcServerPort);
            server.ExecuteOnDevice(_connectKey, $"fport rm tcp:{port} tcp:{_remotePort}", 5_000);
        }
        catch
        {
        }
    }
}