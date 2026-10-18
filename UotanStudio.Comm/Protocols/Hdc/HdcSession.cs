using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Represents an HDC protocol session over a device transport or server channel.
/// <para>表示基于设备传输或 server 通道的 HDC 协议会话。</para>
/// </summary>
public sealed class HdcSession : IProtocolSession, IHdcDeviceSession
{
    private bool _disposed;
    private bool _isLease;
    private Action? _releaseLease;
    private readonly HdcServerClient? _server;
    private readonly string? _connectKey;
    private readonly HdcConnection? _connection;
    private bool _serverConnected;

    internal HdcSession(HdcServerClient server, string connectKey)
    {
        _server = server;
        _connectKey = connectKey;
    }

    private HdcSession(HdcConnection connection, Action? releaseLease)
    {
        _connection = connection;
        _isLease = true;
        _releaseLease = releaseLease;
    }

    /// <summary>Creates a non-owning view of an authenticated direct connection. The owner must outlive its leases.
    /// <para>创建已认证直连的非拥有式视图。持有者必须比其租约存活更久。</para></summary>
    /// <param name="releaseLease">Optional callback invoked exactly once when the lease is disposed.
    /// <para>可选回调，在租约释放时恰好调用一次。</para></param>
    /// <returns>A lease session sharing this session's direct connection; disposing it never closes the connection.
    /// <para>共享本会话直连的租约会话；释放它不会关闭连接。</para></returns>
    /// <exception cref="ObjectDisposedException">This session is disposed.
    /// <para>本会话已释放。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not an authenticated direct connection.
    /// <para>会话不是已认证的直连。</para></exception>
    public HdcSession CreateLease(Action? releaseLease = null)
    {
        ThrowIfDisposed();
        if (!IsConnected || _connection is null) throw new InvalidOperationException("An authenticated direct connection is required.");
        return new HdcSession(_connection, releaseLease);
    }

    /// <summary>
    /// Constructs a caller-owned HDC session over an already-opened direct USB device session, so hosts need
    /// not name <c>UsbDeviceTransport</c> or the <see cref="HdcSession"/> constructor directly.
    /// <para>为已打开的直连 USB 设备会话构造一个由调用方持有的 Hdc 会话；封装传输包装与构造，使宿主无需直接命名
    /// <c>UsbDeviceTransport</c> 或 <see cref="HdcSession"/> 构造子。</para>
    /// </summary>
    internal static HdcSession ForDirectUsb(IUsbDeviceSession usb)
    {
        ArgumentNullException.ThrowIfNull(usb);
        return new HdcSession(new UsbDeviceTransport(usb));
    }

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    /// <value>Always <c>"hdc"</c>. <para>始终为 <c>"hdc"</c>。</para></value>
    public string ProtocolName => "hdc";

    /// <summary>
    /// Gets the underlying transport session.
    /// <para>获取底层传输会话。</para>
    /// </summary>
    /// <value>The transport carried by <see cref="Connection"/>. Server-channel sessions have no transport and access throws <see cref="NotSupportedException"/>.
    /// <para><see cref="Connection"/> 所承载的传输。server 通道会话没有传输，访问会抛出 <see cref="NotSupportedException"/>。</para></value>
    /// <exception cref="NotSupportedException">The session runs over an HDC server channel. <para>会话经由 HDC server 通道运行。</para></exception>
    public ICommTransport Transport => Connection.Transport;

    /// <summary>
    /// Gets whether the session is connected and authenticated.
    /// <para>获取会话是否已连接并通过认证。</para>
    /// </summary>
    /// <value><see langword="true"/> once a connect call has completed on a direct or server-channel session and the session is not disposed.
    /// <para>直连或 server 通道会话的某次连接调用完成且会话未释放时为 <see langword="true"/>。</para></value>
    public bool IsConnected => (_server is not null ? _serverConnected : Connection.IsConnected) && !_disposed;

    /// <summary>
    /// Gets the HDC connection.
    /// <para>获取 HDC 连接。</para>
    /// </summary>
    /// <value>The daemon connection backing direct sessions; server-channel sessions do not expose one.
    /// <para>支撑直连会话的守护进程连接；server 通道会话不暴露该连接。</para></value>
    /// <exception cref="NotSupportedException">The session runs over an HDC server channel. Use session command clients instead.
    /// <para>会话经由 HDC server 通道运行。请改用会话命令客户端。</para></exception>
    public HdcConnection Connection => _connection ?? throw new NotSupportedException("Server channels do not expose a daemon connection. Use session command clients.");

    /// <summary>
    /// Gets the peer protocol version reported during handshake.
    /// <para>获取握手中报告的对端协议版本。</para>
    /// </summary>
    /// <value>The version negotiated by the direct RSA handshake; stays 0 on server-channel sessions, which perform no handshake.
    /// <para>直连 RSA 握手中协商的版本；不执行握手的 server 通道会话保持为 0。</para></value>
    public uint PeerVersion { get; private set; }

    /// <summary>
    /// Gets the peer banner string reported during handshake (e.g. "OHOS HDC").
    /// <para>获取握手中报告的对端横幅字符串（例如 "OHOS HDC"）。</para>
    /// </summary>
    /// <value>The banner learned from the peer; server-channel sessions report the fixed <c>"OHOS HDC"</c> string once connected.
    /// <para>从对端获知的横幅；server 通道会话连接后报告固定的 <c>"OHOS HDC"</c> 字符串。</para></value>
    public string? PeerBanner { get; private set; }

    /// <summary>
    /// Creates a new HDC session over the specified device transport, using the
    /// persistent default key (the upstream host identity at ~/.harmony/hdckey, generated when absent).
    /// <para>在指定设备传输上创建新的 HDC 会话，使用持久化的默认密钥
    /// （上游主机身份 ~/.harmony/hdckey，不存在时生成）。</para>
    /// </summary>
    /// <param name="transport">The device transport.<para>设备传输。</para></param>
    public HdcSession(ICommTransport transport)
        : this(transport, HdcAuthentication.LoadOrCreate())
    {
    }

    /// <summary>
    /// Creates a new HDC session over the specified device transport with the specified
    /// authentication identity. The session (its connection) owns and disposes the
    /// identity.
    /// <para>在指定设备传输上创建使用指定认证身份的新 HDC 会话。会话（其连接）
    /// 拥有并释放该身份。</para>
    /// </summary>
    /// <param name="transport">The device transport.<para>设备传输。</para></param>
    /// <param name="authentication">The HDC host authentication identity.<para>HDC 主机认证身份。</para></param>
    public HdcSession(ICommTransport transport, HdcAuthentication authentication)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        if (authentication is null) throw new ArgumentNullException(nameof(authentication));

        _connection = new HdcConnection(transport, authentication);
    }

    /// <summary>
    /// Connects and authenticates with the HDC peer.
    /// <para>连接并认证 HDC 对端。</para>
    /// </summary>
    public void Connect()
    {
        ThrowIfDisposed();
        if (_server is not null)
        {
            _server.Connect();
            _serverConnected = true;
            PeerBanner = "OHOS HDC";
            return;
        }
        Connection.Connect();
        PeerVersion = Connection.PeerVersion;
        PeerBanner = Connection.PeerBanner;
    }

    /// <summary>
    /// Asynchronously connects and authenticates with the HDC peer.
    /// <para>异步连接并认证 HDC 对端。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A task that completes once the session is connected and authenticated.
    /// <para>在会话完成连接与认证后结束的任务。</para></returns>
    /// <exception cref="ObjectDisposedException">The session is disposed. <para>会话已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_server is not null)
        {
            await _server.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _serverConnected = true;
            PeerBanner = "OHOS HDC";
            return;
        }
        await Connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        PeerVersion = Connection.PeerVersion;
        PeerBanner = Connection.PeerBanner;
    }

    /// <summary>
    /// Opens a command channel to the HDC device for the specified command.
    /// <para>为指定命令打开到 HDC 设备的命令通道。</para>
    /// </summary>
    /// <param name="command">The command string to send.<para>要发送的命令字符串。</para></param>
    /// <returns>An HDC stream for the opened channel.<para>已打开通道的 HDC 流。</para></returns>
    public HdcStream OpenChannel(string command)
    {
        ThrowIfDisposed();
        return Connection.OpenChannel(command);
    }

    /// <summary>
    /// Creates a shell client for the given command.
    /// <para>为给定命令创建 shell 客户端。</para>
    /// </summary>
    /// <param name="command">The command to execute.<para>要执行的命令。</para></param>
    /// <returns>An HDC shell client.<para>HDC shell 客户端。</para></returns>
    public IHdcShellClient CreateShellClient(string command)
    {
        ThrowIfDisposed();
        return _server is null ? new HdcShellClient(Connection, command) : new HdcShellClient(_server, _connectKey!, command);
    }

    /// <summary>
    /// Creates a device-services client for one-shot services (reboot, getprop, etc.).
    /// <para>创建一次性设备服务（reboot、getprop 等）客户端。</para>
    /// </summary>
    /// <returns>An HDC device services client.<para>HDC 设备服务客户端。</para></returns>
    public IHdcDeviceServices CreateDeviceServices()
    {
        ThrowIfDisposed();
        return _server is null ? new HdcDeviceServices(Connection) : new HdcDeviceServices(_server, _connectKey!);
    }

    /// <summary>
    /// Creates a sync (file transfer) client over the direct connection.
    /// <para>在直连通道上创建 sync（文件传输）客户端。</para>
    /// </summary>
    /// <returns>An HDC sync client.<para>HDC sync 客户端。</para></returns>
    public IHdcSyncClient CreateSyncClient()
    {
        ThrowIfDisposed();
        return new HdcSyncClient(Connection);
    }

    /// <summary>
    /// Creates an app install/uninstall client over the direct connection.
    /// <para>在直连通道上创建应用安装/卸载客户端。</para>
    /// </summary>
    /// <returns>An HDC app client.<para>HDC 应用客户端。</para></returns>
    public IHdcAppClient CreateAppClient()
    {
        ThrowIfDisposed();
        return new HdcAppClient(Connection);
    }

    /// <summary>
    /// Gets the port-forwarding manager over the direct connection.
    /// <para>获取直连通道上的端口转发管理器。</para>
    /// </summary>
    /// <value>The forward manager of <see cref="Connection"/>; server-channel sessions throw because they expose no daemon connection.
    /// <para><see cref="Connection"/> 的转发管理器；server 通道会话不暴露守护进程连接，因此会抛出异常。</para></value>
    /// <exception cref="NotSupportedException">The session runs over an HDC server channel. <para>会话经由 HDC server 通道运行。</para></exception>
    public IHdcForwardManager Forwards => Connection.Forwards;

    /// <summary>
    /// Executes a shell command and returns the result.
    /// <para>执行 shell 命令并返回结果。</para>
    /// </summary>
    /// <param name="command">The command to execute.<para>要执行的命令。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds.<para>超时（毫秒）。</para></param>
    /// <returns>The shell execution result.<para>Shell 执行结果。</para></returns>
    public ShellResult ExecuteCommand(string command, int timeoutMs = 30000)
        => CreateShellClient(command).Execute(timeoutMs);

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the session is disposed.
    /// <para>如果会话已释放则抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HdcSession));
        }
    }

    /// <summary>
    /// Releases all resources used by the session.
    /// <para>释放会话使用的所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_isLease) _connection?.Dispose();
        _server?.Dispose();
        Interlocked.Exchange(ref _releaseLease, null)?.Invoke();
    }
}