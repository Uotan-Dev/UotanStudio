using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents an ADB protocol session over a USB transport.
/// <para>表示基于 USB 传输的 ADB 协议会话。</para>
/// </summary>
public sealed class AdbSession : IProtocolSession
{
    private readonly AdbConnection _connection;
    private bool _disposed;

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    public string ProtocolName => "adb";

    /// <summary>
    /// Gets the underlying transport session.
    /// <para>获取底层传输会话。</para>
    /// </summary>
    public IUsbDeviceSession Transport => _connection.Transport;

    /// <summary>
    /// Gets whether the session is connected and authenticated.
    /// <para>获取会话是否已连接并通过认证。</para>
    /// </summary>
    public bool IsConnected => _connection.IsConnected && !_disposed;

    /// <summary>
    /// Gets the ADB connection.
    /// <para>获取 ADB 连接。</para>
    /// </summary>
    public AdbConnection Connection => _connection;

    /// <summary>
    /// Gets the peer protocol version.
    /// <para>获取对端协议版本。</para>
    /// </summary>
    public uint PeerVersion => _connection.PeerVersion;

    /// <summary>
    /// Gets the peer max payload size.
    /// <para>获取对端最大负载大小。</para>
    /// </summary>
    public uint PeerMaxPayload => _connection.PeerMaxPayload;

    /// <summary>
    /// Gets the peer banner.
    /// <para>获取对端横幅。</para>
    /// </summary>
    public string? PeerBanner => _connection.PeerBanner;

    /// <summary>
    /// Gets the features advertised by the peer.
    /// <para>获取对端宣告的特性。</para>
    /// </summary>
    public IReadOnlyList<string> PeerFeatures => _connection.PeerFeatures;

    /// <summary>
    /// Creates a new ADB session over the specified USB transport.
    /// <para>在指定 USB 传输上创建新的 ADB 会话。</para>
    /// </summary>
    /// <param name="transport">The USB transport session. USB 传输会话。</param>
    public AdbSession(IUsbDeviceSession transport)
    {
        _connection = new AdbConnection(transport ?? throw new ArgumentNullException(nameof(transport)));
    }

    /// <summary>
    /// Connects and authenticates with the ADB peer.
    /// <para>连接并认证 ADB 对端。</para>
    /// </summary>
    public void Connect()
    {
        ThrowIfDisposed();
        _connection.Connect();
        _connection.WaitForPeer(10000);
    }

    /// <summary>
    /// Asynchronously connects and authenticates.
    /// <para>异步连接并认证。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _connection.WaitForPeer(10000);
    }

    /// <summary>
    /// Opens a stream to a service.
    /// <para>打开到服务的流。</para>
    /// </summary>
    /// <param name="service">The service destination string. 服务目标字符串。</param>
    /// <returns>An ADB stream. ADB 流。</returns>
    public AdbStream OpenStream(string service)
    {
        ThrowIfDisposed();
        return _connection.OpenStream(service);
    }

    /// <summary>
    /// Creates a shell client for the given command.
    /// <para>为给定命令创建 shell 客户端。</para>
    /// </summary>
    /// <param name="command">The command to execute. 要执行的命令。</param>
    /// <param name="term">Terminal type, or null for default. 终端类型，null 表示默认。</param>
    /// <param name="pty">Whether to use a pseudo-terminal. 是否使用伪终端。</param>
    /// <returns>A shell client. Shell 客户端。</returns>
    public AdbShellClient CreateShellClient(string command, string? term = null, bool pty = false)
    {
        ThrowIfDisposed();
        return new AdbShellClient(_connection, command, term, pty);
    }

    /// <summary>
    /// Creates a sync client for file transfer.
    /// <para>创建用于文件传输的 sync 客户端。</para>
    /// </summary>
    /// <param name="maxChunk">Maximum chunk size in bytes. 最大块大小（字节）。</param>
    /// <returns>A sync client. Sync 客户端。</returns>
    public AdbSyncClient CreateSyncClient(uint maxChunk = 64 * 1024)
    {
        ThrowIfDisposed();
        return new AdbSyncClient(_connection, maxChunk);
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
            throw new ObjectDisposedException(nameof(AdbSession));
    }

    /// <summary>
    /// Releases all resources used by the session.
    /// <para>释放会话使用的所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}
