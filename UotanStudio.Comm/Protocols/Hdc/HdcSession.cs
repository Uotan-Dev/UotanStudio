using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Represents an HDC protocol session over a device transport.
/// <para>表示基于设备传输的 HDC 协议会话。</para>
/// </summary>
public sealed class HdcSession : IProtocolSession
{
    private bool _disposed;

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    public string ProtocolName => "hdc";

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
    /// Gets the HDC connection.
    /// <para>获取 HDC 连接。</para>
    /// </summary>
    public HdcConnection Connection { get; }

    /// <summary>
    /// Gets the peer protocol version reported during handshake.
    /// <para>获取握手中报告的对端协议版本。</para>
    /// </summary>
    public uint PeerVersion { get; private set; }

    /// <summary>
    /// Gets the peer banner string reported during handshake (e.g. "OHOS HDC").
    /// <para>获取握手中报告的对端横幅字符串（例如 "OHOS HDC"）。</para>
    /// </summary>
    public string? PeerBanner { get; private set; }

    /// <summary>
    /// Creates a new HDC session over the specified device transport, using the
    /// persistent default key (loaded from ~/.hdc/keys, generated when absent).
    /// <para>在指定设备传输上创建新的 HDC 会话，使用持久化的默认密钥
    /// （从 ~/.hdc/keys 加载，不存在时生成）。</para>
    /// </summary>
    /// <param name="transport">The device transport. 设备传输。</param>
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
    /// <param name="transport">The device transport. 设备传输。</param>
    /// <param name="authentication">The RSA-3072 authentication identity. RSA-3072 认证身份。</param>
    public HdcSession(ICommTransport transport, HdcAuthentication authentication)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        if (authentication is null) throw new ArgumentNullException(nameof(authentication));

        Connection = new HdcConnection(transport, authentication);
    }

    /// <summary>
    /// Connects and authenticates with the HDC peer.
    /// <para>连接并认证 HDC 对端。</para>
    /// </summary>
    public void Connect()
    {
        ThrowIfDisposed();
        Connection.Connect();
        PeerVersion = Connection.PeerVersion;
        PeerBanner = Connection.PeerBanner;
    }

    /// <summary>
    /// Asynchronously connects and authenticates with the HDC peer.
    /// <para>异步连接并认证 HDC 对端。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await Connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        PeerVersion = Connection.PeerVersion;
        PeerBanner = Connection.PeerBanner;
    }

    /// <summary>
    /// Opens a command channel to the HDC device for the specified command.
    /// <para>为指定命令打开到 HDC 设备的命令通道。</para>
    /// </summary>
    /// <param name="command">The command string to send. 要发送的命令字符串。</param>
    /// <returns>An HDC stream for the opened channel. 已打开通道的 HDC 流。</returns>
    public HdcStream OpenChannel(string command)
    {
        ThrowIfDisposed();
        return Connection.OpenChannel(command);
    }

    /// <summary>
    /// Creates a shell client for the given command.
    /// <para>为给定命令创建 shell 客户端。</para>
    /// </summary>
    /// <param name="command">The command to execute. 要执行的命令。</param>
    /// <returns>An HDC shell client. HDC shell 客户端。</returns>
    public HdcShellClient CreateShellClient(string command)
    {
        ThrowIfDisposed();
        return new HdcShellClient(Connection, command);
    }

    /// <summary>
    /// Creates a device-services client for one-shot services (reboot, getprop, etc.).
    /// <para>创建一次性设备服务（reboot、getprop 等）客户端。</para>
    /// </summary>
    /// <returns>An HDC device services client. HDC 设备服务客户端。</returns>
    public HdcDeviceServices CreateDeviceServices()
    {
        ThrowIfDisposed();
        return new HdcDeviceServices(Connection);
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
        Connection.Dispose();
    }
}
