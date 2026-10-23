using System.Net.Sockets;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// HDC transport over a TCP connection to the device daemon, wrapping a direct
/// <see cref="TcpClient"/> and returning the bytes that have arrived instead of waiting for an exact length.
/// <para>基于 TCP 连接直连设备守护进程的 HDC 传输，封装 <see cref="TcpClient"/> 并返回已到达的字节，
/// 而不是等待凑满精确长度。</para>
/// </summary>
internal sealed class HdcTcpTransport : ICommTransport
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
    /// Gets the remote TCP port of the HDC daemon.
    /// <para>获取 HDC 守护进程的远端 TCP 端口。</para>
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets a value indicating whether the transport is currently connected.
    /// <para>获取传输当前是否已连接。</para>
    /// </summary>
    public bool IsConnected => !_disposed && _stream is not null;

    /// <summary>
    /// Initializes a new TCP HDC transport and connects to the HDC daemon eagerly.
    /// <para>初始化新的 TCP HDC 传输并立即连接 HDC 守护进程。</para>
    /// </summary>
    /// <param name="host">Host name or IP address of the HDC daemon. HDC 守护进程的主机名或 IP 地址。</param>
    /// <param name="port">TCP port of the HDC daemon. HDC 守护进程的 TCP 端口。</param>
    /// <param name="connectTimeoutMs">Connection timeout in milliseconds; defaults to 5000. 连接超时（毫秒），默认 5000。</param>
    public HdcTcpTransport(string host, int port = 8710, int connectTimeoutMs = 5000)
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

    private TcpClient CreateAndConnect(int connectTimeoutMs)
    {
        TcpClient client = new() { NoDelay = true };
        try
        {
            var connectTask = client.ConnectAsync(Host, Port);
            if (!connectTask.Wait(connectTimeoutMs))
            {
                throw new TimeoutException($"Timed out connecting to the HDC daemon at {Host}:{Port}.");
            }

            if (connectTask.IsFaulted)
            {
                throw connectTask.Exception?.GetBaseException() ?? new SocketException((int)SocketError.ConnectionRefused);
            }

            var stream = client.GetStream();
            stream.ReadTimeout = DefaultReadTimeoutMs;
            _stream = stream;
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
    /// Reads the bytes that have arrived, up to <paramref name="length"/>, blocking until at least one byte
    /// arrives, the peer closes the connection, or the read timeout elapses.
    /// <para>读取已到达的字节（最多 <paramref name="length"/> 个）；阻塞至至少一个字节到达、对端关闭连接或读超时。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds for the single read. <para>单次读取的超时（毫秒）。</para></param>
    /// <returns>The number of bytes read, or zero when the peer closed the connection.
    /// <para>读取的字节数；对端关闭连接时为零。</para></returns>
    /// <exception cref="TimeoutException">No byte arrived within <paramref name="timeoutMs"/>.
    /// <para>在 <paramref name="timeoutMs"/> 内没有任何字节到达。</para></exception>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        ValidateBuffer(buffer, offset, length);
        if (length == 0)
        {
            return 0;
        }

        var stream = GetConnectedStream();
        var previousTimeout = stream.ReadTimeout;
        var started = Environment.TickCount64;
        stream.ReadTimeout = Math.Max(1, timeoutMs);
        try
        {
            return stream.Read(buffer, offset, length);
        }
        catch (IOException ex) when (IsTimeout(ex) || Environment.TickCount64 - started >= timeoutMs)
        {
            throw new TimeoutException($"HDC daemon read timed out after {timeoutMs} ms.", ex);
        }
        finally
        {
            stream.ReadTimeout = previousTimeout;
        }
    }

    /// <summary>
    /// Reads the bytes that have arrived asynchronously, up to <paramref name="length"/>.
    /// <para>异步读取已到达的字节（最多 <paramref name="length"/> 个）。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds for the single read. <para>单次读取的超时（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes read, or zero at end of stream.
    /// <para>返回已读取字节数的任务；流结束时为零。</para></returns>
    /// <exception cref="TimeoutException">No byte arrived within <paramref name="timeoutMs"/>.
    /// <para>在 <paramref name="timeoutMs"/> 内没有任何字节到达。</para></exception>
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
            throw new TimeoutException($"HDC daemon read timed out after {timeoutMs} ms.");
        }
        catch (IOException ex) when (IsTimeout(ex) || timeoutCts.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"HDC daemon read timed out after {timeoutMs} ms.", ex);
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
            throw new TimeoutException($"HDC TCP write timed out after {timeoutMs} ms.");
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
        return _stream ?? throw new InvalidOperationException("HDC TCP transport is not connected.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HdcTcpTransport));
        }
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || length < 0 || offset + length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and length must be within the buffer bounds.");
        }
    }

    private static bool IsTimeout(IOException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.WouldBlock })
            {
                return true;
            }
        }

        return false;
    }
}