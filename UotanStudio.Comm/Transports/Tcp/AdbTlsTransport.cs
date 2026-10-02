using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// ADB transport to adbd's Android 11+ secure service (<c>_adb-tls-connect</c>). The
/// connection starts in plaintext: the first 24-byte ADB header read from the device
/// must be the A_STLS upgrade request; the transport echoes A_STLS, performs the TLS
/// 1.3 handshake presenting the certificate registered during pairing, and only then
/// forwards bytes (starting with the device's in-TLS CNXN) to the ADB protocol layer.
/// Per AOSP trust-on-first-use semantics the device's self-signed server certificate
/// is accepted without validation.
/// <para>直连 adbd Android 11+ 安全服务（<c>_adb-tls-connect</c>）的 ADB 传输。连接以
/// 明文开始：从设备读到的首个 24 字节 ADB 头必须是 A_STLS 升级请求；传输层回送
/// A_STLS，出示配对阶段登记的证书完成 TLS 1.3 握手，之后才把字节（从设备 TLS 内的
/// CNXN 开始）交给 ADB 协议层。按 AOSP trust-on-first-use 语义接受设备的自签服务端
/// 证书，不做校验。</para>
/// </summary>
internal sealed class AdbTlsTransport : ICommTransport
{
    private const uint StlsCommand = 0x534C5453;
    private const int AdbHeaderSize = 24;

    private readonly X509Certificate2 _clientCertificate;
    private readonly int _connectTimeoutMs;

    private TcpClient? _client;
    private Stream? _stream;
    private byte[]? _pendingHeader;
    private bool _upgraded;
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
    /// Initializes a new TLS ADB transport. The TCP connection is established eagerly;
    /// the TLS upgrade happens on the first read, after the peer sends A_STLS.
    /// <para>初始化新的 TLS ADB 传输。构造时立即建立 TCP 连接；TLS 升级在第一次读取、
    /// 收到对端的 A_STLS 之后进行。</para>
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
        _upgraded = false;
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
    /// Reads exactly <paramref name="length"/> bytes, handling the plaintext A_STLS
    /// upgrade when the device requests it.
    /// <para>精确读取 <paramref name="length"/> 个字节；设备请求升级时处理明文
    /// A_STLS 握手。</para>
    /// </summary>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
        => ReadAsync(buffer, offset, length, timeoutMs).GetAwaiter().GetResult();

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes asynchronously, handling the
    /// plaintext A_STLS upgrade when the device requests it.
    /// <para>异步精确读取 <paramref name="length"/> 个字节；设备请求升级时处理明文
    /// A_STLS 握手。</para>
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
            if (!_upgraded)
            {
                await EnsureUpgradeAsync(cancellationToken).ConfigureAwait(false);
            }

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
    /// Reads the first plaintext header and upgrades the stream when the device requests TLS.
    /// A device that does not send A_STLS is not a TLS-upgrading device, so the header is handed
    /// to the protocol layer untouched.
    /// <para>读取首个明文头，设备请求 TLS 时完成升级；未发送 A_STLS 的设备不是支持升级的设备，
    /// 该头原样交给协议层。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the upgrade handshake.
    /// <para>取消升级握手。</para></param>
    private async Task EnsureUpgradeAsync(CancellationToken cancellationToken)
    {
        var header = new byte[AdbHeaderSize];
        await ReadFromStreamAsync(header, 0, AdbHeaderSize, 0, cancellationToken).ConfigureAwait(false);
        var command = BinaryPrimitives.ReadUInt32LittleEndian(header);

        if (command != StlsCommand)
        {
            _upgraded = true;
            _pendingHeader = header;
            return;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        await WriteAsync(BuildStlsHeader(version), 0, AdbHeaderSize, 5000, cancellationToken).ConfigureAwait(false);

        var plaintext = GetStream();
        var ssl = new SslStream(plaintext, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = Host,
                ClientCertificates = [_clientCertificate],
                RemoteCertificateValidationCallback = AcceptAnyServerCertificate,
            }).ConfigureAwait(false);
        }
        catch
        {
            ssl.Dispose();
            throw;
        }

        _stream = ssl;
        _upgraded = true;
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