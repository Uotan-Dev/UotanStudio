using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// Fastboot over TCP transport, implementing the AOSP TCP fastboot protocol
/// ("FB01" handshake and 8-byte big-endian length-prefixed frames).
/// <para>基于 TCP 的 Fastboot 传输，实现 AOSP TCP fastboot 协议
/// （"FB01" 握手与 8 字节大端长度前缀帧）。</para>
/// </summary>
public sealed class FastbootTcpTransport : ICommTransport
{
    private const int DefaultIoTimeoutMs = 10000;
    private const int HandshakeTimeoutMs = 30000;

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly byte[] _readLenBuffer = new byte[8];
    private readonly byte[] _writeLenBuffer = new byte[8];

    private TcpClient _client = new();
    private NetworkStream? _stream;
    private ulong _messageBytesLeft;
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
    public bool IsConnected => !_disposed && _stream is not null;

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

            _client = new TcpClient();
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
            _client = new TcpClient();
            _messageBytesLeft = 0;
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
                if (_messageBytesLeft == 0)
                {
                    if (ReadFully(stream, _readLenBuffer, 0, 8) != 8)
                    {
                        throw new IOException("Failed to read Message length from the fastboot TCP stream.");
                    }

                    var frameLength = BinaryPrimitives.ReadUInt64BigEndian(_readLenBuffer);
                    if (frameLength > uint.MaxValue)
                    {
                        throw new IOException($"Invalid TCP frame length: {frameLength} (exceeds uint.MaxValue).");
                    }

                    _messageBytesLeft = frameLength;
                }

                var toRead = (int)Math.Min((ulong)length, _messageBytesLeft);
                var actuallyRead = ReadFully(stream, buffer, offset, toRead);
                _messageBytesLeft -= (ulong)actuallyRead;
                return actuallyRead;
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
            if (_messageBytesLeft == 0)
            {
                if (await ReadFullyAsync(stream, _readLenBuffer, 0, 8, timeoutCts.Token).ConfigureAwait(false) != 8)
                {
                    throw new IOException("Failed to read Message length from the fastboot TCP stream.");
                }

                var frameLength = BinaryPrimitives.ReadUInt64BigEndian(_readLenBuffer);
                if (frameLength > uint.MaxValue)
                {
                    throw new IOException($"Invalid TCP frame length: {frameLength} (exceeds uint.MaxValue).");
                }

                _messageBytesLeft = frameLength;
            }

            var toRead = (int)Math.Min((ulong)length, _messageBytesLeft);
            var actuallyRead = await ReadFullyAsync(stream, buffer, offset, toRead, timeoutCts.Token).ConfigureAwait(false);
            _messageBytesLeft -= (ulong)actuallyRead;
            return actuallyRead;
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
                BinaryPrimitives.WriteUInt64BigEndian(_writeLenBuffer, (ulong)length);
                stream.Write(_writeLenBuffer, 0, 8);
                stream.Write(data, offset, length);
                stream.Flush();
                return length;
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
            BinaryPrimitives.WriteUInt64BigEndian(_writeLenBuffer, (ulong)length);
            await stream.WriteAsync(_writeLenBuffer.AsMemory(0, 8), timeoutCts.Token).ConfigureAwait(false);
            await stream.WriteAsync(data.AsMemory(offset, length), timeoutCts.Token).ConfigureAwait(false);
            return length;
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
            var connectTask = _client.ConnectAsync(Host, Port);
            if (!connectTask.Wait(connectTimeoutMs))
            {
                throw new TimeoutException($"Fastboot TCP handshake failed: connect timeout after {connectTimeoutMs} ms.");
            }

            if (connectTask.IsFaulted)
            {
                throw connectTask.Exception?.GetBaseException() ?? new SocketException((int)SocketError.ConnectionRefused);
            }

            var stream = _client.GetStream();
            _stream = stream;
            stream.ReadTimeout = DefaultIoTimeoutMs;
            stream.WriteTimeout = DefaultIoTimeoutMs;

            var handshake = Encoding.ASCII.GetBytes("FB01");
            stream.Write(handshake, 0, handshake.Length);

            var response = new byte[4];
            if (ReadFully(stream, response, 0, 4) != 4)
            {
                throw new IOException("Fastboot TCP handshake failed: unexpected response or timeout.");
            }

            var responseText = Encoding.ASCII.GetString(response);
            if (!responseText.StartsWith("FB", StringComparison.Ordinal))
            {
                throw new IOException("Fastboot TCP handshake failed: unrecognized initialization Message.");
            }

            var versionStr = responseText.Substring(2, 2);
            if (!int.TryParse(versionStr, out var version) || version < 1)
            {
                throw new IOException($"Fastboot TCP handshake failed: unknown TCP protocol version {versionStr} (host version 01).");
            }
        }
        catch
        {
            _client.Dispose();
            _client = new TcpClient();
            _stream = null;
            throw;
        }
    }

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

    private static int ReadFully(NetworkStream stream, byte[] buffer, int offset, int length)
    {
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = stream.Read(buffer, offset + totalRead, length - totalRead);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static async Task<int> ReadFullyAsync(NetworkStream stream, byte[] buffer, int offset, int length, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}