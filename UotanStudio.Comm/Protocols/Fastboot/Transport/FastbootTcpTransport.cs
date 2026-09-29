using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot.Transport;

/// <summary>
/// Fastboot over TCP transport, implementing the AOSP TCP fastboot protocol
/// ("FB01" handshake and 8-byte big-endian length-prefixed frames).
/// <para>基于 TCP 的 Fastboot 传输，实现 AOSP TCP fastboot 协议
/// （"FB01" 握手与 8 字节大端长度前缀帧）。</para>
/// </summary>
internal sealed class FastbootTcpTransport : ICommTransport
{
    private const int DefaultIoTimeoutMs = 10000;
    private const int HandshakeTimeoutMs = 30000;

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly FastbootTcpFrames _frames = new();

    private TcpClient _client = CreateClient();
    private NetworkStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Gets the host address of the TCP connection.
    /// <para>获取 TCP 连接的主机地址。</para>
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the port number of the TCP connection.
    /// <para>获取 TCP 连接的端口号。</para>
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets a value indicating whether the transport is currently connected.
    /// <para>获取传输当前是否已连接。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> while the transport is not disposed, a network stream is held and the
    /// socket still reports its peer as connected. The socket probe matters because a fastboot target
    /// that reboots away closes its end without this side noticing until the next read, and callers use
    /// this property to decide whether a response can still arrive.
    /// <para>传输未释放、持有网络流且套接字仍报告对端已连接时为 <see langword="true"/>。套接字探测很关键：
    /// 重启离开的 fastboot 目标会关闭自己那一端，而本端要到下一次读取才会察觉；调用方用该属性判断是否
    /// 仍可能收到响应。</para>
    /// </value>
    public bool IsConnected
    {
        get
        {
            var stream = _stream;
            var client = _client;
            if (_disposed || stream is null || client is null)
            {
                return false;
            }

            try
            {
                var socket = client.Client;
                if (socket is null || !client.Connected)
                {
                    return false;
                }

                return !socket.Poll(0, SelectMode.SelectRead) || socket.Available > 0;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Gets whether Nagle's algorithm is disabled on the underlying socket.
    /// <para>获取底层套接字是否已禁用 Nagle 算法。</para>
    /// </summary>
    /// <value><see langword="true"/> while the socket sends every write immediately instead of coalescing
    /// small writes. Fastboot frames write an 8-byte length prefix and its payload as two separate stream
    /// writes, so a delayed second write costs one extra round trip per command.
    /// <para>套接字立即发送每次写入而不是合并小写入时为 <see langword="true"/>。fastboot 帧把 8 字节长度
    /// 前缀与其负载分两次流写入，因此第二次写入被延迟会让每条命令多付一次往返。</para></value>
    internal bool NoDelay => _client.NoDelay;

    /// <summary>
    /// Initializes a new <see cref="FastbootTcpTransport"/> and performs the fastboot TCP handshake.
    /// <para>初始化新的 <see cref="FastbootTcpTransport"/> 并执行 fastboot TCP 握手。</para>
    /// </summary>
    /// <param name="host">Host name or IP address of the device. 设备的主机名或 IP 地址。</param>
    /// <param name="port">TCP port of the fastboot service; defaults to 5554. fastboot 服务的 TCP 端口，默认 5554。</param>
    /// <param name="connectTimeoutMs">Connection timeout in milliseconds. 连接超时（毫秒）。</param>
    public FastbootTcpTransport(string host, int port = 5554, int connectTimeoutMs = HandshakeTimeoutMs)
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
        InitializeProtocol(connectTimeoutMs);
    }

    /// <summary>
    /// Connects the transport, re-establishing the TCP connection and handshake if it was disconnected.
    /// <para>连接传输，若已断开则重新建立 TCP 连接并执行握手。</para>
    /// </summary>
    public void Connect()
    {
        ThrowIfDisposed();
        _ioLock.Wait();
        try
        {
            if (_stream is not null)
            {
                return;
            }

            _client = CreateClient();
            InitializeProtocol(HandshakeTimeoutMs);
        }
        finally
        {
            _ioLock.Release();
        }
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

        _ioLock.Wait();
        try
        {
            _stream?.Dispose();
            _client.Dispose();
            _stream = null;
            _client = CreateClient();
            _frames.Reset();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Resets the transport by disconnecting and reconnecting.
    /// <para>通过断开并重新连接来重置传输。</para>
    /// </summary>
    public void Reset()
    {
        Disconnect();
        Connect();
    }

    /// <summary>
    /// Reads the next fastboot frame (8-byte big-endian length prefix) payload into the
    /// caller-provided buffer, returning at most <paramref name="length"/> bytes.
    /// <para>将下一个 fastboot 帧（8 字节大端长度前缀）的载荷读入调用方缓冲区，
    /// 返回最多 <paramref name="length"/> 个字节。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes read from the current frame. <para>从当前帧读取的字节数。</para></returns>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        ValidateBuffer(buffer, offset, length);
        if (length == 0)
        {
            return 0;
        }

        _ioLock.Wait();
        try
        {
            var stream = GetConnectedStream();
            var previousTimeout = stream.ReadTimeout;
            stream.ReadTimeout = timeoutMs;
            try
            {
                return _frames.Read(stream, buffer, offset, length);
            }
            finally
            {
                stream.ReadTimeout = previousTimeout;
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Reads the next fastboot frame payload asynchronously.
    /// <para>异步读取下一个 fastboot 帧载荷。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes read. <para>返回实际读取字节数的任务。</para></returns>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ValidateBuffer(buffer, offset, length);
        if (length == 0)
        {
            return 0;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
        {
            timeoutCts.CancelAfter(timeoutMs);
        }

        await _ioLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            var stream = GetConnectedStream();
            return await _frames.ReadAsync(stream, buffer, offset, length, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Fastboot TCP read timed out after {timeoutMs} ms.");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Writes <paramref name="length"/> bytes as a fastboot frame, prefixing an 8-byte
    /// big-endian length header.
    /// <para>将 <paramref name="length"/> 个字节作为 fastboot 帧写入，
    /// 前置 8 字节大端长度头。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        ValidateBuffer(data, offset, length);
        _ioLock.Wait();
        try
        {
            var stream = GetConnectedStream();
            var previousTimeout = stream.WriteTimeout;
            stream.WriteTimeout = timeoutMs;
            try
            {
                return _frames.Write(stream, data, offset, length);
            }
            finally
            {
                stream.WriteTimeout = previousTimeout;
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Writes <paramref name="length"/> bytes as a fastboot frame asynchronously.
    /// <para>异步将 <paramref name="length"/> 个字节作为 fastboot 帧写入。</para>
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
        {
            timeoutCts.CancelAfter(timeoutMs);
        }

        await _ioLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            var stream = GetConnectedStream();
            return await _frames.WriteAsync(stream, data, offset, length, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Fastboot TCP write timed out after {timeoutMs} ms.");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Returns the raw underlying <see cref="NetworkStream"/>. Note that frames written or
    /// read through this stream bypass the length-prefix framing.
    /// <para>返回底层原始 <see cref="NetworkStream"/>。注意通过该流读写会绕过长度前缀帧封装。</para>
    /// </summary>
    /// <returns>The live network stream. 活动的网络流。</returns>
    public Stream GetStream() => GetConnectedStream();

    /// <summary>
    /// Disposes the TCP client, stream, and IO lock resources.
    /// <para>释放 TCP 客户端、流及 IO 锁资源。</para>
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
        _client.Dispose();
        _ioLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private void InitializeProtocol(int connectTimeoutMs)
    {
        _client.ReceiveTimeout = DefaultIoTimeoutMs;
        _client.SendTimeout = DefaultIoTimeoutMs;
        try
        {
            ConnectSocket(connectTimeoutMs);

            var stream = _client.GetStream();
            _stream = stream;
            stream.ReadTimeout = DefaultIoTimeoutMs;
            stream.WriteTimeout = DefaultIoTimeoutMs;

            FastbootTcpFrames.Handshake(stream);
        }
        catch
        {
            _client.Dispose();
            _client = CreateClient();
            _stream = null;
            throw;
        }
    }

    /// <summary>
    /// Establishes the TCP connection within <paramref name="connectTimeoutMs"/>, reporting a timeout
    /// and a connect failure as two distinct outcomes. The failure is rethrown unwrapped so callers can
    /// inspect its type: a refused connection means nothing is listening on the port, which the debug
    /// bridge routing rules treat as "no server here", while a timeout means a host answered nothing and
    /// must not be read the same way. <c>Task.Wait(int)</c> cannot express that distinction because it
    /// rethrows the fault as an <see cref="AggregateException"/> and the faulted branch after it is
    /// unreachable.
    /// <para>在 <paramref name="connectTimeoutMs"/> 内建立 TCP 连接，并把超时与连接失败报告为两种不同的
    /// 结果。失败以未包裹的形式重抛，以便调用方检查其类型：连接被拒绝意味着端口上没有监听者，调试桥路由
    /// 规则据此判定“此处没有 server”；而超时意味着主机毫无应答，两者不可混同。<c>Task.Wait(int)</c> 无法
    /// 表达该区分，因为它把故障重抛为 <see cref="AggregateException"/>，其后判断故障的分支永远不可达。</para>
    /// </summary>
    /// <param name="connectTimeoutMs">Connect deadline in milliseconds. <para>连接截止时间（毫秒）。</para></param>
    /// <exception cref="TimeoutException">The peer did not complete the TCP handshake in time. <para>对端未在时限内完成 TCP 握手。</para></exception>
    /// <exception cref="SocketException">The connect failed, with <see cref="SocketException.SocketErrorCode"/> naming the cause.
    /// <para>连接失败，<see cref="SocketException.SocketErrorCode"/> 指出原因。</para></exception>
    private void ConnectSocket(int connectTimeoutMs)
    {
        var connectTask = _client.ConnectAsync(Host, Port);
        try
        {
            if (!connectTask.Wait(connectTimeoutMs))
            {
                _ = connectTask.ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw new TimeoutException($"Fastboot TCP handshake failed: connect timeout after {connectTimeoutMs} ms.");
            }
        }
        catch (AggregateException ex)
        {
            ExceptionDispatchInfo.Capture(ex.GetBaseException()).Throw();
        }
    }

    /// <summary>
    /// Creates a TCP client with Nagle's algorithm disabled. Fastboot frames write an 8-byte length
    /// prefix and its payload as two separate stream writes, so Nagle delays the second until the first
    /// is acknowledged and every command costs an extra round trip.
    /// <para>创建禁用 Nagle 算法的 TCP 客户端。fastboot 帧把 8 字节长度前缀与其负载分两次流写入，
    /// 因此 Nagle 会把第二次写入推迟到第一次被确认之后，每条命令都多付一次往返。</para>
    /// </summary>
    private static TcpClient CreateClient() => new() { NoDelay = true };

    private NetworkStream GetConnectedStream()
    {
        ThrowIfDisposed();
        return _stream ?? throw new InvalidOperationException("Fastboot TCP transport is not connected.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FastbootTcpTransport));
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

}