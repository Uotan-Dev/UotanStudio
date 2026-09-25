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

    /// <summary>Creates a session whose services are opened through the selected server target.</summary>
    public AdbSession(AdbServerServices server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Connection = new AdbConnection(server);
    }

    /// <summary>
    /// Creates a session bound to a freshly opened direct-USB device, encapsulating the native transport wrapper.
    /// <para>基于新打开的 USB 直连设备创建会话，封装原生传输包装。</para>
    /// </summary>
    internal static AdbSession ForDirectUsb(IUsbDeviceSession usb)
    {
        ArgumentNullException.ThrowIfNull(usb);
        return new AdbSession(new UsbDeviceTransport(usb));
    }

    /// <summary>
    /// Creates a session routed through the adb server target identified by <paramref name="serial"/>.
    /// <para>基于 adb server 中由 <paramref name="serial"/> 标识的目标创建会话。</para>
    /// </summary>
    public static AdbSession ForServer(string serial, string host, int port)
        => new(new AdbServerServices(serial, host, port));


    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    public string ProtocolName => "adb";

    /// <summary>
    /// Gets the underlying transport session.
    /// <para>获取底层传输会话。</para>
    /// </summary>
    public ICommTransport Transport => Connection.Transport;

    /// <summary>
    /// Gets whether the session is connected and authenticated.
    /// <para>获取会话是否已连接并通过认证。</para>
    /// </summary>
    public bool IsConnected => Connection.IsConnected && !_disposed;

    /// <summary>
    /// Gets the ADB connection.
    /// <para>获取 ADB 连接。</para>
    /// </summary>
    internal AdbConnection Connection { get; }

    /// <summary>
    /// Gets the peer protocol version.
    /// <para>获取对端协议版本。</para>
    /// </summary>
    public uint PeerVersion => Connection.PeerVersion;

    /// <summary>
    /// Gets whether this session is mediated by a local adb server rather than raw USB.
    /// <para>获取本会话是否经本机 adb server 交互，而非原生 USB 直连。</para>
    /// </summary>
    public bool UsesServer => Connection.UsesServer;

    /// <summary>
    /// Gets the peer max payload size.
    /// <para>获取对端最大负载大小。</para>
    /// </summary>
    public uint PeerMaxPayload => Connection.PeerMaxPayload;

    /// <summary>
    /// Gets the peer banner.
    /// <para>获取对端横幅。</para>
    /// </summary>
    public string? PeerBanner => Connection.PeerBanner;

    /// <summary>
    /// Gets the features advertised by the peer.
    /// <para>获取对端宣告的特性。</para>
    /// </summary>
    public IReadOnlyList<string> PeerFeatures => Connection.PeerFeatures;

    /// <summary>
    /// Creates a new ADB session over the specified device transport, using the
    /// persistent default key (loaded from %USERPROFILE%/.android/adbkey, generated
    /// when absent).
    /// <para>在指定设备传输上创建新的 ADB 会话，使用持久化的默认密钥（从
    /// %USERPROFILE%/.android/adbkey 加载，不存在时生成）。</para>
    /// </summary>
    /// <param name="transport">The device transport. 设备传输。</param>
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
    /// <param name="transport">The device transport. 设备传输。</param>
    /// <param name="authentication">The RSA authentication identity. RSA 认证身份。</param>
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
    /// <param name="transport">The device transport. 设备传输。</param>
    /// <param name="authentications">The ordered RSA authentication identities. 有序 RSA 认证身份列表。</param>
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
    public void Connect()
    {
        ThrowIfDisposed();
        Connection.Connect();
        if (!Connection.WaitForPeer(10000))
            throw new TimeoutException("ADB peer did not complete the connection handshake.");
    }

    /// <summary>
    /// Asynchronously connects and authenticates.
    /// <para>异步连接并认证。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await Connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!await Task.Run(() => Connection.WaitForPeer(10000, cancellationToken), cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("ADB peer did not complete the connection handshake.");
    }

    /// <summary>
    /// Opens a stream to a service.
    /// <para>打开到服务的流。</para>
    /// </summary>
    /// <param name="service">The service destination string. 服务目标字符串。</param>
    /// <returns>An ADB stream. ADB 流。</returns>
    public IAdbStream OpenStream(string service)
    {
        ThrowIfDisposed();
        return Connection.OpenStream(service);
    }

    /// <summary>
    /// Opens a byte-oriented stream to a service.
    /// <para>打开面向字节的服務流。</para>
    /// </summary>
    /// <param name="service">The service destination string. 服务目标字符串。</param>
    /// <returns>A byte stream. 字节流。</returns>
    public Stream OpenServiceStream(string service) => new AdbServiceStream(Connection.OpenStream(service));

    /// <summary>
    /// Creates a shell client for the given command.
    /// <para>为给定命令创建 shell 客户端。</para>
    /// </summary>
    /// <param name="command">The command to execute. 要执行的命令。</param>
    /// <param name="term">Terminal type, or null for default. 终端类型，null 表示默认。</param>
    /// <param name="pty">Whether to use a pseudo-terminal. 是否使用伪终端。</param>
    /// <returns>A shell client. Shell 客户端。</returns>
    public IAdbShellClient CreateShellClient(string command, string? term = null, bool pty = false)
    {
        ThrowIfDisposed();
        return new AdbShellClient(Connection, command, term, pty);
    }

    /// <summary>
    /// Creates a sync client for file transfer.
    /// <para>创建用于文件传输的 sync 客户端。</para>
    /// </summary>
    /// <param name="maxChunk">Maximum chunk size in bytes. 最大块大小（字节）。</param>
    /// <returns>A sync client. Sync 客户端。</returns>
    public IAdbSyncClient CreateSyncClient(uint maxChunk = 64 * 1024)
    {
        ThrowIfDisposed();
        return new AdbSyncClient(Connection, maxChunk);
    }

    /// <summary>
    /// Creates a device-services client for one-shot services (reboot, remount, getprop).
    /// <para>创建一次性设备服务（reboot、remount、getprop）客户端。</para>
    /// </summary>
    /// <returns>A device services client. 设备服务客户端。</returns>
    public IAdbDeviceServices CreateDeviceServices()
    {
        ThrowIfDisposed();
        return new AdbDeviceServices(Connection);
    }

    /// <summary>
    /// Executes a shell command and returns the result.
    /// <para>执行 shell 命令并返回结果。</para>
    /// </summary>
    /// <param name="command">The command to execute. 要执行的命令。</param>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns>The shell execution result. Shell 执行结果。</returns>
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
            throw new ObjectDisposedException(nameof(AdbSession));
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
        Connection.Dispose();
    }
}