using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// Owns one loopback TCP forward into an HDC target and returns the connected stream to the caller.
/// The upstream may be a TCP port or a virtual socket schema such as <c>localabstract</c>; the forward is
/// registered on a direct HDC session through its channel manager, or through the hdc server's
/// <c>fport</c> command when the session is server-backed. The loopback socket, the free-port choice and
/// the teardown all stay inside Comm.
/// <para>持有一条指向 HDC 目标的回环 TCP 转发，并把连接后的流返回给调用方。上游可以是 TCP 端口，也可以是
/// <c>localabstract</c> 等虚拟套接字模式；直连 HDC 会话经其通道管理器登记转发，server 会话则经 hdc server
/// 的 <c>fport</c> 命令登记。回环套接字、空闲端口选择与清理全部留在 Comm 内。</para>
/// </summary>
public sealed class HdcForwardTunnel : IDisposable
{
    private readonly HdcSession _session;
    private readonly string _connectKey;
    private readonly string _remoteEndpoint;
    private readonly int _requestedLocalPort;
    private readonly int _timeoutMs;
    private readonly Lock _gate = new();
    private TcpClient? _client;
    private int _localPort;
    private string? _serverHost;
    private int? _serverPort;
    private bool _mapped;
    private bool _directMapping;
    private int _opening;
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="remotePort"/> is outside 1..65535.
    /// <para><paramref name="remotePort"/> 不在 1..65535 范围内。</para></exception>
    public HdcForwardTunnel(HdcSession session, string connectKey, int remotePort,
        int requestedLocalPort = 0, int timeoutMs = 20_000)
        : this(session, connectKey, PortEndpoint(remotePort), requestedLocalPort, timeoutMs)
    {
    }

    private static string PortEndpoint(int remotePort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(remotePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remotePort, 65535);
        return $"tcp:{remotePort}";
    }

    /// <summary>
    /// Creates a tunnel whose upstream is any endpoint schema the HDC daemon supports, such as a virtual
    /// Unix socket or a TCP port.
    /// <para>创建上游可为 HDC 守护进程支持的任意端点模式（例如虚拟 Unix 套接字或 TCP 端口）的通道。</para>
    /// </summary>
    /// <param name="session">Connected HDC session owning the forward or the host channel.
    /// <para>持有转发或主机通道的已连接 HDC 会话。</para></param>
    /// <param name="connectKey">Target connect key used by the server-backed route.
    /// <para>server 路由使用的目标连接键。</para></param>
    /// <param name="remoteEndpoint">Upstream endpoint in <c>schema:content</c> form, for example
    /// <c>tcp:5000</c> or <c>localabstract:uitest_socket</c>.
    /// <para>采用 <c>schema:content</c> 形式的上游端点，例如 <c>tcp:5000</c> 或
    /// <c>localabstract:uitest_socket</c>。</para></param>
    /// <param name="requestedLocalPort">Preferred loopback port; zero picks a free port.
    /// <para>首选回环端口；为零时自动选择空闲端口。</para></param>
    /// <param name="timeoutMs">Budget for the forward and the loopback connect.
    /// <para>建立转发与连接回环端口的预算。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="connectKey"/> is blank or
    /// <paramref name="remoteEndpoint"/> is not a supported forward endpoint.
    /// <para><paramref name="connectKey"/> 为空，或 <paramref name="remoteEndpoint"/> 不是受支持的转发端点。</para></exception>
    public HdcForwardTunnel(HdcSession session, string connectKey, string remoteEndpoint,
        int requestedLocalPort = 0, int timeoutMs = 20_000)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        ArgumentOutOfRangeException.ThrowIfNegative(requestedLocalPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requestedLocalPort, 65535);
        HdcForwardManager.ValidateRemoteEndpoint(remoteEndpoint, reverse: false);
        _session = session;
        _connectKey = connectKey;
        _remoteEndpoint = remoteEndpoint;
        _requestedLocalPort = requestedLocalPort;
        _timeoutMs = Math.Max(timeoutMs, 1_000);
    }

    /// <summary>
    /// Registers the forward and returns its loopback port without opening a client connection, so a caller
    /// that owns a different client (for example an HTTP/2 stack) can connect to the mapped port itself.
    /// <para>登记转发并返回其回环端口，但不打开客户端连接，使自行持有其它客户端（例如 HTTP/2 栈）的调用方可以
    /// 连接该映射端口。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the forward setup. <para>取消转发建立。</para></param>
    /// <returns>The loopback port that now forwards to the upstream endpoint.
    /// <para>当前转发到上游端点的回环端口。</para></returns>
    /// <exception cref="ObjectDisposedException">The tunnel is disposed. <para>通道已释放。</para></exception>
    /// <exception cref="IOException">The forward registration failed. <para>转发登记失败。</para></exception>
    public int Map(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            EnsureMapping(cancellationToken);
            return _localPort;
        }
    }

    /// <summary>
    /// Registers the forward when needed and returns one connected loopback stream to the upstream endpoint.
    /// <para>按需登记转发并返回一条连到上游端点的回环流。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the forward setup and the connect retries.
    /// <para>取消转发建立与连接重试。</para></param>
    /// <returns>The connected stream whose disposal leaves the forward registered until this tunnel is disposed.
    /// <para>已连接的流；释放该流不会移除转发，转发由本通道释放时移除。</para></returns>
    /// <exception cref="ObjectDisposedException">The tunnel is disposed. <para>通道已释放。</para></exception>
    /// <exception cref="InvalidOperationException">The tunnel is already open or opening.
    /// <para>通道已打开或正在打开。</para></exception>
    /// <exception cref="NotSupportedException">The session exposes neither a native forward manager nor a server endpoint.
    /// <para>会话既没有原生转发管理器，也没有可用的 server 端点。</para></exception>
    /// <exception cref="IOException">The forward or the loopback connect failed.
    /// <para>转发或回环连接失败。</para></exception>
    public Stream Open(CancellationToken cancellationToken = default)
    {
        int port;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_client is not null)
            {
                throw new InvalidOperationException("The HDC forward tunnel is already open.");
            }

            if (_opening != 0)
            {
                throw new InvalidOperationException("The HDC forward tunnel is already opening.");
            }

            _opening = 1;
            try
            {
                EnsureMapping(cancellationToken);
                port = _localPort;
            }
            catch
            {
                _opening = 0;
                throw;
            }
        }

        try
        {
            var client = ConnectLoopback(port, cancellationToken);
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    client.Dispose();
                    throw new ObjectDisposedException(nameof(HdcForwardTunnel));
                }

                _client = client;
                _opening = 0;
                return client.GetStream();
            }
        }
        catch
        {
            lock (_gate)
            {
                _opening = 0;
            }

            throw;
        }
    }

    private TcpClient ConnectLoopback(int port, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + _timeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(HdcForwardTunnel));
            }

            var client = new TcpClient { NoDelay = true };
            try
            {
                client.Connect(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException error)
            {
                client.Dispose();
                if (Environment.TickCount64 >= deadline)
                {
                    throw new IOException(
                        $"The HDC forward on 127.0.0.1:{port} did not accept the connection within {_timeoutMs} ms.",
                        error);
                }

                Thread.Sleep(250);
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
            _session.Forwards.Add(_localPort, _remoteEndpoint, cancellationToken: cancellationToken);
            _directMapping = true;
            _mapped = true;
            return;
        }
        catch (NotSupportedException)
        {
        }

        var serverHost = _session.ServerHost;
        var serverPort = _session.ServerPort;
        if (serverHost is null || serverPort is null)
        {
            throw new NotSupportedException(
                "The HDC session has no server endpoint for a forward fallback.");
        }

        _serverHost = serverHost;
        _serverPort = serverPort;
        using var server = new HdcServerClient(serverHost, serverPort.Value);
        var output = server.ExecuteOnDevice(_connectKey,
            $"fport tcp:{_localPort} {_remoteEndpoint}", _timeoutMs);
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

            if (_serverHost is null || _serverPort is null)
            {
                return;
            }

            using var server = new HdcServerClient(_serverHost, _serverPort.Value);
            server.ExecuteOnDevice(_connectKey, $"fport rm tcp:{port} {_remoteEndpoint}", 5_000);
        }
        catch
        {
        }
    }
}