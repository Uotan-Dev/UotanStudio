using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// ADB transport to adbd's Android 11+ secure service (<c>_adb-tls-connect</c>). The
/// upgrade is negotiated while the connection is established, before any ADB message is
/// exchanged: the transport writes a 24-byte A_STLS header, reads the peer's 24-byte
/// header, and when that header is A_STLS it performs the TLS 1.3 handshake presenting the
/// certificate registered during pairing. Only then are bytes forwarded (starting with the
/// device's in-TLS CNXN) to the ADB protocol layer. Announcing before reading is what makes
/// the exchange safe whichever side the daemon speaks first, and it keeps the plaintext
/// A_CNXN off a wire the daemon is still reading as a handshake. A peer that answers with a
/// non-A_STLS header is not offering TLS, so that header is handed to the protocol layer
/// untouched. Per AOSP trust-on-first-use semantics the device's self-signed server
/// certificate is accepted without validation.
/// <para>直连 adbd Android 11+ 安全服务（<c>_adb-tls-connect</c>）的 ADB 传输。升级在建立
/// 连接时完成，先于任何 ADB 消息：传输层写出 24 字节 A_STLS 头，再读取对端的 24 字节头，
/// 若该头是 A_STLS 就出示配对阶段登记的证书完成 TLS 1.3 握手，之后才把字节（从设备 TLS 内的
/// CNXN 开始）交给 ADB 协议层。先宣告再读取，使无论设备先说还是我们先说都不会卡住，也让明文
/// A_CNXN 不会落在设备仍当作握手来读的线路上。用非 A_STLS 头应答的设备没有提供 TLS，该头原样
/// 交给协议层。按 AOSP trust-on-first-use 语义接受设备的自签服务端证书，不做校验。</para>
/// </summary>
internal sealed class AdbTlsTransport : ICommTransport
{
    private const uint StlsCommand = 0x534C5453;
    private const uint StlsVersion = 1;
    private const int AdbHeaderSize = 24;

    private readonly X509Certificate2 _clientCertificate;
    private readonly int _connectTimeoutMs;

    private TcpClient? _client;
    private Stream? _stream;
    private byte[]? _pendingHeader;
    private bool _disposed;

    /// <summary>
    /// Gets the remote host name or IP address.
    /// <para>获取远端主机名或 IP 地址。</para>
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the remote TCP port of the secure adbd service.
    /// <para>获取 adbd 安全服务的远端 TCP 端口。</para>
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets a value indicating whether the transport is currently connected.
    /// <para>获取传输当前是否已连接。</para>
    /// </summary>
    public bool IsConnected => !_disposed && _stream is not null;

    /// <summary>
    /// Initializes a new TLS ADB transport. The TCP connection and the A_STLS/TLS upgrade are
    /// both established here, so the protocol layer never writes an ADB message onto a wire that
    /// the daemon is still reading as a handshake.
    /// <para>初始化新的 TLS ADB 传输。TCP 连接与 A_STLS/TLS 升级都在此处完成，协议层不会把 ADB
    /// 消息写到设备仍当作握手在读的线路上。</para>
    /// </summary>
    /// <param name="host">Host name or IP address of the device. 设备的主机名或 IP 地址。</param>
    /// <param name="port">TCP port of the secure adbd service. adbd 安全服务的 TCP 端口。</param>
    /// <param name="clientCertificate">
    /// The certificate registered on the device during pairing.
    /// <para>配对阶段登记到设备上的证书。</para>
    /// </param>
    /// <param name="connectTimeoutMs">Connection timeout in milliseconds.
    /// 连接超时（毫秒）。</param>
    /// <exception cref="ArgumentException">The host is empty.
    /// <para>主机为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The port or timeout is out of range.
    /// <para>端口或超时超出范围。</para></exception>
    public AdbTlsTransport(string host, int port, X509Certificate2 clientCertificate, int connectTimeoutMs = 5000)
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
        _clientCertificate = clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate));
        _connectTimeoutMs = connectTimeoutMs;
        _client = CreateAndConnect();
    }

    /// <summary>
    /// Connects the transport, re-establishing the TCP connection if it was disconnected.
    /// <para>连接传输；若已断开则重新建立 TCP 连接。</para>
    /// </summary>
    public void Connect()
    {
        ThrowIfDisposed();
        if (_stream is not null)
        {
            return;
        }

        _client = CreateAndConnect();
    }

    /// <summary>
    /// Disconnects the transport, closing the TCP (and TLS, when upgraded) connection.
    /// <para>断开传输，关闭 TCP（已升级时含 TLS）连接。</para>
    /// </summary>
    public void Disconnect()
    {
        if (_disposed)
        {
            return;
        }

        _stream?.Dispose();
        _stream = null;
        _pendingHeader = null;
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
        _client = CreateAndConnect();
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes, draining the plaintext header a
    /// non-TLS peer answered the upgrade with first.
    /// <para>精确读取 <paramref name="length"/> 个字节，先排空非 TLS 设备对升级的明文应答头。</para>
    /// </summary>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
        => ReadAsync(buffer, offset, length, timeoutMs).GetAwaiter().GetResult();

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes asynchronously, draining the plaintext
    /// header a non-TLS peer answered the upgrade with first.
    /// <para>异步精确读取 <paramref name="length"/> 个字节，先排空非 TLS 设备对升级的明文应答头。</para>
    /// </summary>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ValidateBuffer(buffer, offset, length);
        if (length == 0)
        {
            return 0;
        }

        var totalRead = 0;
        while (totalRead < length)
        {
            if (_pendingHeader is { Length: > 0 } pending)
            {
                var consumed = Math.Min(pending.Length, length - totalRead);
                Array.Copy(pending, 0, buffer, offset + totalRead, consumed);
                _pendingHeader = consumed < pending.Length ? pending[consumed..] : null;
                totalRead += consumed;
                continue;
            }

            var read = await ReadFromStreamAsync(buffer, offset + totalRead, length - totalRead, timeoutMs,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (totalRead > 0)
                {
                    return totalRead;
                }
                throw new EndOfStreamException("ADB TLS connection closed by the peer.");
            }
            totalRead += read;
        }

        return totalRead;
    }

    /// <summary>
    /// Writes all <paramref name="length"/> bytes, returning the count written.
    /// <para>写入全部 <paramref name="length"/> 个字节，返回实际写入数。</para>
    /// </summary>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
        => WriteAsync(data, offset, length, timeoutMs).GetAwaiter().GetResult();

    /// <summary>
    /// Writes all <paramref name="length"/> bytes asynchronously.
    /// <para>异步写入全部 <paramref name="length"/> 个字节。</para>
    /// </summary>
    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ValidateBuffer(data, offset, length);
        var stream = GetStream();
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
            throw new TimeoutException($"ADB TLS write timed out after {timeoutMs} ms.");
        }
    }

    /// <summary>
    /// Returns the active stream: plaintext before the upgrade, <see cref="SslStream"/>
    /// afterwards.
    /// <para>返回当前流：升级前为明文流，升级后为 <see cref="SslStream"/>。</para>
    /// </summary>
    public Stream GetStream()
    {
        ThrowIfDisposed();
        return _stream ?? throw new InvalidOperationException("ADB TLS transport is not connected.");
    }

    /// <summary>
    /// Releases the underlying connection.
    /// <para>释放底层连接。</para>
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

    private TcpClient CreateAndConnect()
    {
        TcpClient client = new() { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(_connectTimeoutMs);
            try
            {
                client.ConnectAsync(Host, Port, timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out connecting to ADB device at {Host}:{Port}.");
            }

            _stream = client.GetStream();
            NegotiateUpgrade();
            return client;
        }
        catch
        {
            client.Dispose();
            _stream = null;
            _pendingHeader = null;
            throw;
        }
    }

    /// <summary>
    /// Announces the TLS upgrade, then reads the peer's header: A_STLS upgrades the stream,
    /// anything else is a plaintext daemon whose header is kept for the protocol layer.
    /// <para>先宣告 TLS 升级，再读取对端头：A_STLS 升级流；其它头说明对端是明文守护进程，该头保留给
    /// 协议层。</para>
    /// </summary>
    private void NegotiateUpgrade()
    {
        WriteAsync(BuildStlsHeader(StlsVersion), 0, AdbHeaderSize, _connectTimeoutMs).GetAwaiter().GetResult();

        var header = new byte[AdbHeaderSize];
        int read;
        try
        {
            read = ReadFromStreamAsync(header, 0, AdbHeaderSize, _connectTimeoutMs, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"The ADB device at {Host}:{Port} did not answer the TLS upgrade within {_connectTimeoutMs} ms.", ex);
        }

        if (read == 0)
        {
            throw new IOException(
                $"The ADB device at {Host}:{Port} closed the connection before the TLS upgrade.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != StlsCommand)
        {
            _pendingHeader = header;
            return;
        }

        var ssl = new SslStream(GetStream(), leaveInnerStreamOpen: false);
        try
        {
            using var timeout = new CancellationTokenSource(_connectTimeoutMs);
            ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = Host,
                ClientCertificates = [_clientCertificate],
                RemoteCertificateValidationCallback = AcceptAnyServerCertificate,
            }, timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            ssl.Dispose();
            throw new TimeoutException($"The TLS handshake with the ADB device at {Host}:{Port} timed out.");
        }
        catch (Exception ex) when (ex is not IOException and not TimeoutException)
        {
            ssl.Dispose();
            throw new IOException($"The TLS handshake with the ADB device at {Host}:{Port} failed: {ex.Message}",
                ex);
        }

        _stream = ssl;
    }

    private static bool AcceptAnyServerCertificate(object? sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors errors) => true;

    private static byte[] BuildStlsHeader(uint version)
    {
        var header = new byte[AdbHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, StlsCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), StlsCommand ^ 0xFFFFFFFF);
        return header;
    }

    private async Task<int> ReadFromStreamAsync(byte[] buffer, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken)
    {
        var stream = GetStream();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
        {
            timeoutCts.CancelAfter(timeoutMs);
        }

        try
        {
            var totalRead = 0;
            while (totalRead < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, length - totalRead),
                    timeoutCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }

            return totalRead;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ADB TLS read timed out after {timeoutMs} ms.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AdbTlsTransport));
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