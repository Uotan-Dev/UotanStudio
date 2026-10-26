using System.Net.Sockets;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// ADB transport over a TCP connection (adb connect host:port), wrapping a direct
/// <see cref="TcpClient"/> connection to adbd with bounded reads that return the bytes already arrived and full writes.
/// <para>基于 TCP 连接的 ADB 传输（adb connect host:port），封装与 adbd 的直接
/// <see cref="TcpClient"/> 连接：读取返回已到达的字节，写入为全量写入。</para>
/// </summary>
internal sealed class AdbTcpTransport : ICommTransport
{
    private const int DefaultReadTimeoutMs = 60_000;

    private TcpClient _client;
    private NetworkStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Gets the remote host name or IP address.
    /// <para>获取远端主机名或 IP 地址。</para>
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the remote TCP port of the adbd service.
    /// <para>获取 adbd 服务的远端 TCP 端口。</para>
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets a value indicating whether the transport is currently connected.
    /// <para>获取传输当前是否已连接。</para>
    /// </summary>
    public bool IsConnected => !_disposed && _stream is not null;

    /// <summary>
    /// Initializes a new TCP ADB transport and connects to adbd eagerly.
    /// <para>初始化新的 TCP ADB 传输并立即连接 adbd。</para>
    /// </summary>
    /// <param name="host">Host name or IP address of the device. 设备的主机名或 IP 地址。</param>
    /// <param name="port">TCP port of the adbd service; defaults to 5555. adbd 服务的 TCP 端口，默认 5555。</param>
    /// <param name="connectTimeoutMs">Connection timeout in milliseconds; defaults to 5000. 连接超时（毫秒），默认 5000。</param>
    public AdbTcpTransport(string host, int port = 5555, int connectTimeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host must not be empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (connectTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs));
        }

        Host = host;
        Port = port;
        _client = CreateAndConnect(connectTimeoutMs);
    }

    /// <summary>
    /// Connects a new client within <paramref name="connectTimeoutMs"/>. The wait is cancelled
    /// rather than awaited with a timeout, because blocking on the task would surface the socket
    /// failure wrapped in an <see cref="AggregateException"/> and callers could not catch the
    /// transport error they asked about.
    /// <para>在 <paramref name="connectTimeoutMs"/> 内连接新的客户端。等待通过取消实现而非带超时的
    /// 阻塞，因为阻塞等待会把套接字失败包进 <see cref="AggregateException"/>，调用方就无法捕获它
    /// 真正关心的传输错误。</para>
    /// </summary>
    /// <param name="connectTimeoutMs">Connection timeout in milliseconds.
    /// <para>连接超时（毫秒）。</para></param>
    /// <returns>A connected client. <para>已连接的客户端。</para></returns>
    private TcpClient CreateAndConnect(int connectTimeoutMs)
    {
        TcpClient client = new() { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(connectTimeoutMs);
            try
            {
                client.ConnectAsync(Host, Port, timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out connecting to ADB device at {Host}:{Port}.");
            }

            var stream = client.GetStream();
            stream.ReadTimeout = DefaultReadTimeoutMs;
            _stream = stream;
            TcpKeepAlive.Enable(client.Client);
            return client;
        }
        catch
        {
            client.Dispose();
            _stream = null;
            throw;
        }
    }

    /// <summary>
    /// Connects the transport, re-establishing the TCP connection if it was disconnected.
    /// <para>连接传输，若已断开则重新建立 TCP 连接。</para>
    /// </summary>
    public void Connect()
    {
        ThrowIfDisposed();
        if (_stream is not null)
        {
            return;
        }

        _client = CreateAndConnect(connectTimeoutMs: 5000);
    }

    /// <summary>
    /// Disconnects the transport, closing the underlying TCP connection.
    /// <para>断开传输，关闭底层 TCP 连接。</para>
    /// </summary>
    public void Disconnect()
    {
        if (_disposed)
        {
            return;
        }

        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null!;
    }

    /// <summary>
    /// Resets the transport by disconnecting and reconnecting.
    /// <para>通过断开并重新连接来重置传输。</para>
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        Disconnect();
        _client = CreateAndConnect(connectTimeoutMs: 5000);
    }

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes that have already arrived, returning as soon as at least one byte
    /// is available. Zero means the peer closed the connection; a timeout before any byte arrived raises
    /// <see cref="TimeoutException"/> instead of losing a partial frame.
    /// <para>读取已经到达的最多 <paramref name="length"/> 个字节，只要有至少一个字节可用就立即返回。
    /// 返回 0 表示对端关闭连接；一个字节都未到达即超时会抛出 <see cref="TimeoutException"/>，不会丢失半帧。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes read, or zero at end of stream. <para>读取的字节数；流结束时为 0。</para></returns>
    /// <exception cref="TimeoutException">No byte arrived within <paramref name="timeoutMs"/>.
    /// <para>在 <paramref name="timeoutMs"/> 内没有字节到达。</para></exception>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        ValidateBuffer(buffer, offset, length);
        if (length == 0)
        {
            return 0;
        }

        var stream = GetConnectedStream();
        var previousTimeout = stream.ReadTimeout;
        stream.ReadTimeout = timeoutMs > 0 ? timeoutMs : Timeout.Infinite;
        try
        {
            return stream.Read(buffer, offset, length);
        }
        catch (IOException ex) when (IsSocketTimeout(ex))
        {
            throw new TimeoutException($"ADB TCP read timed out after {timeoutMs} ms.", ex);
        }
        finally
        {
            stream.ReadTimeout = previousTimeout;
        }
    }

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes asynchronously, returning as soon as at least one byte is
    /// available. Zero means the peer closed the connection; a timeout before any byte arrived raises
    /// <see cref="TimeoutException"/> instead of losing a partial frame.
    /// <para>异步读取已经到达的最多 <paramref name="length"/> 个字节，只要有至少一个字节可用就立即返回。
    /// 返回 0 表示对端关闭连接；一个字节都未到达即超时会抛出 <see cref="TimeoutException"/>，不会丢失半帧。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes read, or zero at end of stream.
    /// <para>返回读取字节数的任务；流结束时为 0。</para></returns>
    /// <exception cref="TimeoutException">No byte arrived within <paramref name="timeoutMs"/>.
    /// <para>在 <paramref name="timeoutMs"/> 内没有字节到达。</para></exception>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ValidateBuffer(buffer, offset, length);
        if (length == 0)
        {
            return 0;
        }

        var stream = GetConnectedStream();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
        {
            timeoutCts.CancelAfter(timeoutMs);
        }

        try
        {
            return await stream.ReadAsync(buffer.AsMemory(offset, length), timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ADB TCP read timed out after {timeoutMs} ms.");
        }
        catch (IOException ex) when (IsSocketTimeout(ex))
        {
            throw new TimeoutException($"ADB TCP read timed out after {timeoutMs} ms.", ex);
        }
    }

    /// <summary>
    /// Writes all <paramref name="length"/> bytes, returning the count written.
    /// <para>写入全部 <paramref name="length"/> 个字节，返回实际写入数。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        ValidateBuffer(data, offset, length);
        var stream = GetConnectedStream();
        var previousTimeout = stream.WriteTimeout;
        stream.WriteTimeout = timeoutMs;
        try
        {
            stream.Write(data, offset, length);
            return length;
        }
        finally
        {
            stream.WriteTimeout = previousTimeout;
        }
    }

    /// <summary>
    /// Writes all <paramref name="length"/> bytes asynchronously.
    /// <para>异步写入全部 <paramref name="length"/> 个字节。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes written. <para>返回实际写入字节数的任务。</para></returns>
    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ValidateBuffer(data, offset, length);
        var stream = GetConnectedStream();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
        {
            timeoutCts.CancelAfter(timeoutMs);
        }

        try
        {
            await stream.WriteAsync(data.AsMemory(offset, length), timeoutCts.Token).ConfigureAwait(false);
            return length;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ADB TCP write timed out after {timeoutMs} ms.");
        }
    }

    /// <summary>
    /// Returns the underlying <see cref="NetworkStream"/> of the TCP connection.
    /// <para>返回 TCP 连接底层的 <see cref="NetworkStream"/>。</para>
    /// </summary>
    /// <returns>The live network stream. 活动的网络流。</returns>
    public Stream GetStream() => GetConnectedStream();

    /// <summary>
    /// Releases the underlying TCP connection.
    /// <para>释放底层 TCP 连接。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        GC.SuppressFinalize(this);
    }

    private NetworkStream GetConnectedStream()
    {
        ThrowIfDisposed();
        return _stream ?? throw new InvalidOperationException("ADB TCP transport is not connected.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AdbTcpTransport));
        }
    }

    private static bool IsSocketTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                socket.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || length < 0 || offset + length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and length must be within the buffer bounds.");
        }
    }
}