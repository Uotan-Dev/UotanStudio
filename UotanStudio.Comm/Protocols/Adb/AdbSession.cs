using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents an ADB protocol session over a device transport.
/// <para>表示基于设备传输的 ADB 协议会话。</para>
/// </summary>
internal sealed class AdbSession : IProtocolSession, IAdbDeviceSession
{
    private bool _disposed;

    /// <summary>
    /// Creates a session whose services are opened through the selected server target.
    /// <para>创建会话，其服务经由所选 adb server 目标开启。</para>
    /// </summary>
    /// <param name="server">The server target services.<para>Server 目标的服务句柄。</para></param>
    public AdbSession(AdbServerServices server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Connection = new AdbConnection(server);
    }

    /// <summary>
    /// Creates a session bound to a freshly opened direct-USB device, encapsulating the native transport wrapper.
    /// <para>基于新打开的 USB 直连设备创建会话，封装原生传输包装。</para>
    /// </summary>
    /// <param name="usb">The freshly opened direct-USB device session.<para>新打开的 USB 直连设备会话。</para></param>
    /// <returns>A session bound to the device transport.<para>绑定到该设备传输的会话。</para></returns>
    internal static AdbSession ForDirectUsb(IUsbDeviceSession usb)
    {
        ArgumentNullException.ThrowIfNull(usb);
        return new AdbSession(new UsbDeviceTransport(usb));
    }

    /// <summary>
    /// Creates a session routed through the adb server target identified by <paramref name="serial"/>.
    /// <para>基于 adb server 中由 <paramref name="serial"/> 标识的目标创建会话。</para>
    /// </summary>
    /// <param name="serial">The server-reported serial of the target.<para>server 上报的目标序列号。</para></param>
    /// <param name="host">The server host name.<para>Server 主机名。</para></param>
    /// <param name="port">The server TCP port.<para>Server TCP 端口。</para></param>
    /// <returns>A session routed through the adb server.<para>经由 adb server 路由的会话。</para></returns>
    public static AdbSession ForServer(string serial, string host, int port)
        => new(new AdbServerServices(serial, host, port));


    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    /// <value>The fixed tag <c>adb</c>.<para>固定标记 <c>adb</c>。</para></value>
    public string ProtocolName => "adb";

    /// <summary>
    /// Gets the underlying transport session.
    /// <para>获取底层传输会话。</para>
    /// </summary>
    /// <value>The transport backing the connection.<para>支撑该连接的传输。</para></value>
    public ICommTransport Transport => Connection.Transport;

    /// <summary>
    /// Gets whether the session is connected and authenticated.
    /// <para>获取会话是否已连接并通过认证。</para>
    /// </summary>
    /// <value><see langword="true"/> when connected, authenticated and not disposed.
    /// <para>已连接、已认证且未释放时为 <see langword="true"/>。</para></value>
    public bool IsConnected => Connection.IsConnected && !_disposed;

    /// <summary>
    /// Gets the ADB connection.
    /// <para>获取 ADB 连接。</para>
    /// </summary>
    /// <value>The connection owning the message loop.<para>持有消息循环的连接。</para></value>
    internal AdbConnection Connection { get; }

    /// <summary>
    /// Gets the peer protocol version.
    /// <para>获取对端协议版本。</para>
    /// </summary>
    /// <value>The version reported by the peer handshake.<para>对端握手报告的版本。</para></value>
    public uint PeerVersion => Connection.PeerVersion;

    /// <summary>
    /// Gets whether this session is mediated by a local adb server rather than raw USB.
    /// <para>获取本会话是否经本机 adb server 交互，而非原生 USB 直连。</para>
    /// </summary>
    /// <value><see langword="true"/> when an adb server channel backs the session.
    /// <para>会话由 adb server 通道支撑时为 <see langword="true"/>。</para></value>
    public bool UsesServer => Connection.UsesServer;

    /// <summary>
    /// Gets the peer max payload size.
    /// <para>获取对端最大负载大小。</para>
    /// </summary>
    /// <value>The payload ceiling negotiated with the peer.<para>与对端商定的负载上限。</para></value>
    public uint PeerMaxPayload => Connection.PeerMaxPayload;

    /// <summary>
    /// Gets the peer banner.
    /// <para>获取对端横幅。</para>
    /// </summary>
    /// <value>The banner text sent by the peer.<para>对端发送的横幅文本。</para></value>
    public string? PeerBanner => Connection.PeerBanner;

    /// <summary>
    /// Gets the features advertised by the peer.
    /// <para>获取对端宣告的特性。</para>
    /// </summary>
    /// <value>The feature names advertised in the peer banner.<para>对端横幅中宣告的特性名称。</para></value>
    public IReadOnlyList<string> PeerFeatures => Connection.PeerFeatures;

    /// <summary>
    /// Creates a new ADB session over the specified device transport, using the
    /// persistent default key (loaded from %USERPROFILE%/.android/adbkey, generated
    /// when absent).
    /// <para>在指定设备传输上创建新的 ADB 会话，使用持久化的默认密钥（从
    /// %USERPROFILE%/.android/adbkey 加载，不存在时生成）。</para>
    /// </summary>
    /// <param name="transport">The device transport.<para>设备传输。</para></param>
    public AdbSession(ICommTransport transport)
        : this(transport, AdbAuthentication.LoadOrCreate())
    {
    }

    /// <summary>
    /// Creates a new ADB session over the specified device transport with a single
    /// authentication identity. The session (its connection) owns and disposes the
    /// identity.
    /// <para>在指定设备传输上创建使用单个认证身份的新 ADB 会话。会话（其连接）
    /// 拥有并释放该身份。</para>
    /// </summary>
    /// <param name="transport">The device transport.<para>设备传输。</para></param>
    /// <param name="authentication">The RSA authentication identity.<para>RSA 认证身份。</para></param>
    public AdbSession(ICommTransport transport, AdbAuthentication authentication)
    {
        Connection = new AdbConnection(
            transport ?? throw new ArgumentNullException(nameof(transport)),
            authentication ?? throw new ArgumentNullException(nameof(authentication)));
    }

    /// <summary>
    /// Creates a new ADB session over the specified device transport with an ordered
    /// list of authentication identities (key rotation). The session (its
    /// connection) owns and disposes every identity.
    /// <para>在指定设备传输上创建使用有序认证身份列表（密钥轮换）的新 ADB 会话。
    /// 会话（其连接）拥有并释放所有身份。</para>
    /// </summary>
    /// <param name="transport">The device transport.<para>设备传输。</para></param>
    /// <param name="authentications">The ordered RSA authentication identities.<para>有序 RSA 认证身份列表。</para></param>
    public AdbSession(ICommTransport transport, IReadOnlyList<AdbAuthentication> authentications)
    {
        Connection = new AdbConnection(
            transport ?? throw new ArgumentNullException(nameof(transport)),
            authentications ?? throw new ArgumentNullException(nameof(authentications)));
    }

    /// <summary>
    /// Connects and authenticates with the ADB peer.
    /// <para>连接并认证 ADB 对端。</para>
    /// </summary>
    /// <exception cref="TimeoutException">The peer did not answer the handshake; when the message
    /// loop already stopped, the loop's own failure is the inner cause.
    /// <para>对端未完成握手；消息循环已经停止时，循环自身的失败作为内部原因。</para></exception>
    public void Connect()
    {
        ThrowIfDisposed();
        Connection.Connect();
        if (!Connection.WaitForPeer(10000))
            throw HandshakeTimeout();
    }

    /// <summary>
    /// Asynchronously connects and authenticates.
    /// <para>异步连接并认证。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <exception cref="TimeoutException">The peer did not answer the handshake; when the message
    /// loop already stopped, the loop's own failure is the inner cause.
    /// <para>对端未完成握手；消息循环已经停止时，循环自身的失败作为内部原因。</para></exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await Connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!await Connection.WaitForPeerAsync(10000, cancellationToken).ConfigureAwait(false))
            throw HandshakeTimeout();
    }

    private TimeoutException HandshakeTimeout() =>
        Connection.LoopFault is { } fault
            ? new TimeoutException(
                $"ADB peer did not complete the connection handshake: {fault.Message}", fault)
            : new TimeoutException("ADB peer did not complete the connection handshake.");

    /// <summary>
    /// Opens a stream to a service.
    /// <para>打开到服务的流。</para>
    /// </summary>
    /// <param name="service">The service destination string.<para>服务目标字符串。</para></param>
    /// <returns>An ADB stream.<para>ADB 流。</para></returns>
    public IAdbStream OpenStream(string service)
    {
        ThrowIfDisposed();
        return Connection.OpenStream(service);
    }

    /// <summary>
    /// Opens a byte-oriented stream to a service.
    /// <para>打开面向字节的服务流。</para>
    /// </summary>
    /// <param name="service">The service destination string.<para>服务目标字符串。</para></param>
    /// <returns>A byte stream.<para>字节流。</para></returns>
    public Stream OpenServiceStream(string service) => new AdbServiceStream(Connection.OpenStream(service));

    /// <summary>
    /// Creates a shell client for the given command.
    /// <para>为给定命令创建 shell 客户端。</para>
    /// </summary>
    /// <param name="command">The command to execute.<para>要执行的命令。</para></param>
    /// <param name="term">Terminal type, or null for default.<para>终端类型，null 表示默认。</para></param>
    /// <param name="pty">Whether to use a pseudo-terminal.<para>是否使用伪终端。</para></param>
    /// <returns>A shell client.<para>Shell 客户端。</para></returns>
    public IAdbShellClient CreateShellClient(string command, string? term = null, bool pty = false)
    {
        ThrowIfDisposed();
        return new AdbShellClient(Connection, command, term, pty);
    }

    /// <summary>
    /// Creates a sync client for file transfer.
    /// <para>创建用于文件传输的 sync 客户端。</para>
    /// </summary>
    /// <param name="maxChunk">Maximum chunk size in bytes.<para>最大块大小（字节）。</para></param>
    /// <returns>A sync client.<para>Sync 客户端。</para></returns>
    public IAdbSyncClient CreateSyncClient(uint maxChunk = 64 * 1024)
    {
        ThrowIfDisposed();
        return new AdbSyncClient(Connection, maxChunk);
    }

    /// <summary>
    /// Creates a device-services client for one-shot services (reboot, remount, getprop).
    /// <para>创建一次性设备服务（reboot、remount、getprop）客户端。</para>
    /// </summary>
    /// <returns>A device services client.<para>设备服务客户端。</para></returns>
    public IAdbDeviceServices CreateDeviceServices()
    {
        ThrowIfDisposed();
        return new AdbDeviceServices(Connection);
    }

    /// <summary>
    /// Executes a shell command and returns the result.
    /// <para>执行 shell 命令并返回结果。</para>
    /// </summary>
    /// <param name="command">The command to execute.<para>要执行的命令。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds.<para>超时（毫秒）。</para></param>
    /// <returns>The shell execution result.<para>Shell 执行结果。</para></returns>
    /// <exception cref="TimeoutException">A read timed out while the stream was still open.
    /// <para>流仍打开时读取超时。</para></exception>
    public ShellResult ExecuteCommand(string command, int timeoutMs = 30000)
        => ExecuteCommandAsync(command, timeoutMs, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Executes a shell command asynchronously and returns the result.
    /// <para>异步执行 shell 命令并返回结果。</para>
    /// </summary>
    /// <param name="command">The command to execute.<para>要执行的命令。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The shell execution result.<para>Shell 执行结果。</para></returns>
    /// <exception cref="TimeoutException">A read timed out after 30 seconds while the stream was still open.
    /// <para>流仍打开时某次读取超过 30 秒。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public ValueTask<ShellResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
        => ExecuteCommandAsync(command, 30000, cancellationToken);

    /// <summary>
    /// Executes a shell command with an explicit per-read wait.
    /// <para>按显式的单次读取等待时长异步执行 shell 命令。</para>
    /// </summary>
    /// <param name="command">The command to execute.<para>要执行的命令。</para></param>
    /// <param name="timeoutMs">Per-read wait in milliseconds.<para>单次读取的等待毫秒数。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A task resolving to the shell execution result.<para>结果为 shell 执行结果的任务。</para></returns>
    /// <exception cref="TimeoutException">A read timed out while the stream was still open.
    /// <para>流仍打开时读取超时。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public ValueTask<ShellResult> ExecuteCommandAsync(string command, int timeoutMs,
        CancellationToken cancellationToken)
        => CreateShellClient(command).ExecuteAsync(timeoutMs, cancellationToken);

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the session is disposed.
    /// <para>如果会话已释放则抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AdbSession));
        }
    }

    /// <summary>
    /// Releases the resources held by the session.
    /// <para>释放会话持有的资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Connection.Dispose();
    }
}