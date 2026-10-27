using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot.Transport;

/// <summary>
/// Fastboot over UDP transport, fully implementing the Fastboot over Network
/// wire protocol (packet headers, sequence numbers, handshake, timeout-based
/// retransmission).
/// <para>基于 UDP 的 Fastboot 传输，完整实现 Fastboot over Network 线协议
/// （包头、序号、握手、基于超时的重传）。</para>
/// </summary>
internal sealed class FastbootUdpTransport : ICommTransport
{
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly FastbootUdpPackets _packets;
    private readonly int _defaultTimeoutMs;

    private UdpClient? _client;
    private bool _connected;
    private bool _disposed;


    /// <summary>
    /// Gets the host address of the UDP connection.
    /// <para>获取 UDP 连接的主机地址。</para>
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the port number of the UDP connection.
    /// <para>获取 UDP 连接的端口号。</para>
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets a value indicating whether the transport is currently connected.
    /// <para>获取传输当前是否已连接。</para>
    /// </summary>
    public bool IsConnected => !_disposed && _connected && _client is not null;

    /// <summary>
    /// Initializes a new <see cref="FastbootUdpTransport"/> and performs the fastboot UDP handshake.
    /// <para>初始化新的 <see cref="FastbootUdpTransport"/> 并执行 fastboot UDP 握手。</para>
    /// </summary>
    /// <param name="host">IP address of the device. 设备的 IP 地址。</param>
    /// <param name="port">UDP port of the fastboot service; defaults to 5554. fastboot 服务的 UDP 端口，默认 5554。</param>
    /// <param name="timeoutMs">Per-transmission timeout in milliseconds; defaults to 1000. 单次传输超时（毫秒），默认 1000。</param>
    /// <param name="maxTransmissionAttempts">Maximum transmission attempts per packet; defaults to 10. 每包最大传输尝试次数，默认 10。</param>
    public FastbootUdpTransport(string host, int port = 5554, int timeoutMs = 1000, int maxTransmissionAttempts = 10)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host must not be empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (timeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        }

        if (maxTransmissionAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTransmissionAttempts));
        }

        Host = host;
        Port = port;
        _defaultTimeoutMs = timeoutMs;
        _packets = new FastbootUdpPackets(new IPEndPoint(IPAddress.Parse(host), port), maxTransmissionAttempts);
        _client = new UdpClient();

        try
        {
            InitializeProtocol(timeoutMs);
        }
        catch
        {
            _client.Dispose();
            _client = null;
            throw;
        }
    }

    /// <summary>
    /// Connects the transport, re-establishing the UDP session and handshake if it was disconnected.
    /// <para>连接传输，若已断开则重新建立 UDP 会话并执行握手。</para>
    /// </summary>
    public void Connect()
    {
        ThrowIfDisposed();
        _ioLock.Wait();
        try
        {
            if (_connected)
            {
                return;
            }

            _client = new UdpClient();
            try
            {
                InitializeProtocol(_defaultTimeoutMs);
            }
            catch
            {
                _client.Dispose();
                _client = null;
                throw;
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Disconnects the transport, closing the underlying UDP client.
    /// <para>断开传输，关闭底层 UDP 客户端。</para>
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
            _client?.Dispose();
            _client = null;
            _packets.Reset();
            _connected = false;
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
    /// Sends a fastboot UDP request and reads the response data directly into the
    /// specified buffer, returning the number of bytes read.
    /// <para>发送 fastboot UDP 请求并将响应数据直接读入指定缓冲区，返回实际读取的字节数。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Per-transmission timeout in milliseconds. 单次传输超时（毫秒）。</param>
    /// <returns>The number of bytes read. <para>实际读取的字节数。</para></returns>
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
            var client = GetClient();
            client.Client.ReceiveTimeout = timeoutMs;
            return _packets.Read(client, buffer, offset, length);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Sends a fastboot UDP request and reads the response data asynchronously.
    /// <para>异步发送 fastboot UDP 请求并读取响应数据。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Per-transmission timeout in milliseconds. 单次传输超时（毫秒）。</param>
    /// <param name="cancellationToken">A cancellation token. 取消令牌。</param>
    /// <returns>A task that resolves to the number of bytes read. <para>返回实际读取字节数的任务。</para></returns>
    public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => Task.Run(() => Read(buffer, offset, length, timeoutMs), cancellationToken);

    /// <summary>
    /// Sends fastboot data as UDP packets, waiting for the acknowledgements after each packet.
    /// <para>以 UDP 包形式发送 fastboot 数据，并在每包之后等待确认。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Per-transmission timeout in milliseconds. 单次传输超时（毫秒）。</param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        ValidateBuffer(data, offset, length);
        _ioLock.Wait();
        try
        {
            var client = GetClient();
            client.Client.ReceiveTimeout = timeoutMs;
            _packets.Write(client, data, offset, length);
            return length;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Sends fastboot data as UDP packets asynchronously.
    /// <para>异步以 UDP 包形式发送 fastboot 数据。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Per-transmission timeout in milliseconds. 单次传输超时（毫秒）。</param>
    /// <param name="cancellationToken">A cancellation token. 取消令牌。</param>
    /// <returns>A task that resolves to the number of bytes written. <para>返回实际写入字节数的任务。</para></returns>
    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => Task.Run(() => Write(data, offset, length, timeoutMs), cancellationToken);

    /// <summary>
    /// The UDP transport has no byte stream; a <see cref="Stream"/> adapter is not supported.
    /// <para>UDP 传输没有字节流，不支持 <see cref="Stream"/> 适配器。</para>
    /// </summary>
    /// <returns>A stream adapter over the transport. 传输上的流适配器。</returns>
    /// <exception cref="NotSupportedException">UDP transports cannot expose a stream. UDP 传输无法提供流。</exception>
    public Stream GetStream() => throw new NotSupportedException("Fastboot UDP transport does not support a byte stream.");

    /// <summary>
    /// Disposes the UDP client and IO lock resources.
    /// <para>释放 UDP 客户端及 IO 锁资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _client = null;
        _ioLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private void InitializeProtocol(int timeoutMs)
    {
        var client = GetClient();
        client.Client.ReceiveTimeout = timeoutMs;
        client.Client.SendTimeout = timeoutMs;

        _packets.Handshake(client);
        _connected = true;
    }

    private UdpClient GetClient()
    {
        ThrowIfDisposed();
        return _client ?? throw new InvalidOperationException("Fastboot UDP transport is not connected.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FastbootUdpTransport));
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