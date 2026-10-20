using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// ADB transport to adbd's Android 11+ secure service (<c>_adb-tls-connect</c>). Constructing or
/// reconnecting it only establishes the plaintext TCP connection and never writes on the wire, because the
/// upgrade is driven by the ADB protocol layer in upstream order: the host sends its A_CNXN first, and only a
/// device that answers with A_STLS gets the 24-byte STLS reply followed by the TLS 1.3 client handshake
/// presenting the certificate registered during pairing. Plaintext devices therefore keep speaking CNXN/AUTH
/// on an untouched wire. Per AOSP trust-on-first-use semantics the device's self-signed server certificate is
/// accepted without validation.
/// <para>直连 adbd Android 11+ 安全服务（<c>_adb-tls-connect</c>）的 ADB 传输。构造或重连只建立明文 TCP
/// 连接、不在线路上写任何字节，因为升级由 ADB 协议层按上游顺序驱动：主机先发 A_CNXN，只有以 A_STLS
/// 应答的设备才会收到 24 字节 STLS 回复，随后出示配对阶段登记的证书完成 TLS 1.3 客户端握手。明文设备
/// 因此在未被触碰的线路上继续使用 CNXN/AUTH。按 AOSP trust-on-first-use 语义接受设备的自签服务端证书，
/// 不做校验。</para>
/// <para>On Windows the platform stack (Schannel) rejects the in-memory host identity certificate because its
/// private key is an ephemeral CNG key, so the handshake falls back to BouncyCastle TLS there. That fallback keeps
/// the connection working but degrades <see cref="ReadAsync(byte[], int, int, int, CancellationToken)"/> and
/// <see cref="WriteAsync(byte[], int, int, int, CancellationToken)"/> to async-over-sync, because the BouncyCastle
/// TLS stream exposes only synchronous reads and writes and therefore occupies a thread-pool thread per operation.
/// Disconnecting or disposing the transport sends the TLS close_notify alert on that fallback path just as the
/// platform stack does, ignoring teardown failures so that releasing the transport never fails.</para>
/// <para>在 Windows 上平台栈（Schannel）会拒绝内存中派生的主机身份证书，因为其私钥是临时 CNG 密钥，因此该平台回退到
/// BouncyCastle TLS。该回退保证连接可用，但会让 <see cref="ReadAsync(byte[], int, int, int, CancellationToken)"/>
/// 与 <see cref="WriteAsync(byte[], int, int, int, CancellationToken)"/> 退化为 async-over-sync：
/// BouncyCastle 的 TLS 流只提供同步读写，每次操作都会占用一个线程池线程。断开或释放传输时该回退路径与平台栈一样
/// 发送 TLS close_notify 告警，并忽略拆除阶段的失败，保证释放传输永不失败。</para>
/// </summary>
internal sealed class AdbTlsTransport : ICommTransport
{
    private readonly X509Certificate2 _clientCertificate;
    private readonly int _connectTimeoutMs;
    private readonly bool _useManagedTls;

    private TcpClient? _client;
    private TlsClientProtocol? _managedProtocol;
    private Stream? _stream;
    private bool _tlsActive;
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

    /// <summary>Initializes a new TLS ADB transport and opens only the plaintext TCP connection; the A_STLS/TLS upgrade happens later, when the protocol layer receives the peer's A_STLS after its own A_CNXN.
    /// <para>初始化新的 TLS ADB 传输，只打开明文 TCP 连接；A_STLS/TLS 升级稍后进行，由协议层在收到对端针对自身 A_CNXN 的 A_STLS 后触发。</para></summary>
    /// <param name="host">Host name or IP address of the device. 设备的主机名或 IP 地址。</param>
    /// <param name="port">TCP port of the secure adbd service. adbd 安全服务的 TCP 端口。</param>
    /// <param name="clientCertificate">The certificate derived from the host identity key for this handshake.
    /// <para>本次握手由主机身份密钥派生的证书。</para></param>
    /// <param name="connectTimeoutMs">Connection timeout in milliseconds. 连接超时（毫秒）。</param>
    /// <exception cref="ArgumentException">The host is empty.<para>主机为空。</para></exception>
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
        _useManagedTls = RequiresManagedTls(_clientCertificate);
        CreateAndConnect();
    }

    /// <summary>
    /// Connects the transport, re-establishing the plaintext TCP connection if it was disconnected.
    /// <para>连接传输；若已断开则重新建立明文 TCP 连接。</para>
    /// </summary>
    public void Connect()
    {
        ThrowIfDisposed();
        if (_stream is not null)
        {
            return;
        }

        CreateAndConnect();
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

        CloseManagedProtocol();
        _stream?.Dispose();
        _stream = null;
        _tlsActive = false;
        _client?.Dispose();
        _client = null;
    }

    /// <summary>
    /// Resets the transport by disconnecting and reconnecting.
    /// <para>通过断开并重新连接来重置传输。</para>
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        Disconnect();
        CreateAndConnect();
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes from the current (plaintext or TLS) stream.
    /// <para>从当前（明文或 TLS）流精确读取 <paramref name="length"/> 个字节。</para>
    /// </summary>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
        => ReadAsync(buffer, offset, length, timeoutMs).GetAwaiter().GetResult();

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes asynchronously from the current (plaintext or TLS) stream.
    /// <para>异步从当前（明文或 TLS）流精确读取 <paramref name="length"/> 个字节。</para>
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
        ApplyIoTimeout(timeoutMs);
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
        catch (IOException ex) when (IsSocketTimeout(ex))
        {
            throw new TimeoutException($"ADB TLS write timed out after {timeoutMs} ms.", ex);
        }
    }

    /// <summary>
    /// Returns the active stream: the plaintext socket before the upgrade, then either
    /// <see cref="SslStream"/> or the BouncyCastle TLS stream when the platform stack cannot present the
    /// pairing certificate.
    /// <para>返回当前流：升级前为明文套接字流，升级后为 <see cref="SslStream"/>；平台栈无法出示配对证书时为
    /// BouncyCastle TLS 流。</para>
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
        CloseManagedProtocol();
        _stream?.Dispose();
        _stream = null;
        _tlsActive = false;
        _client?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Opens only the plaintext TCP connection. No byte is written here: the host CNXN and any TLS upgrade are
    /// owned by the ADB protocol layer.
    /// <para>只打开明文 TCP 连接。此处不写任何字节：主机 CNXN 与可能的 TLS 升级都由 ADB 协议层负责。</para>
    /// </summary>
    private void CreateAndConnect()
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

            _client = client;
            _stream = client.GetStream();
            _tlsActive = false;
        }
        catch
        {
            client.Dispose();
            _client = null;
            _stream = null;
            _tlsActive = false;
            throw;
        }
    }

    /// <summary>
    /// Performs the TLS 1.3 client handshake on the already connected plaintext socket, presenting the certificate
    /// derived from the host identity. The ADB protocol layer calls this after the peer answers the host CNXN with
    /// A_STLS and the 24-byte STLS reply has been written; the transport only swaps its stream once the handshake
    /// succeeds, so a failed handshake leaves the plaintext stream readable for diagnostics.
    /// <para>在已连接的明文套接字上执行 TLS 1.3 客户端握手，并出示由主机身份派生的证书。ADB 协议层在对端以
    /// A_STLS 应答主机 CNXN 且已写出 24 字节 STLS 回复之后调用它；只有握手成功传输才切换流，握手失败时明文流
    /// 仍可读，便于诊断。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The transport is not connected, or it has already been upgraded.
    /// <para>传输尚未连接，或已升级过。</para></exception>
    /// <exception cref="IOException">The TLS handshake failed. <para>TLS 握手失败。</para></exception>
    /// <exception cref="TimeoutException">The TLS handshake exceeded the connect timeout.
    /// <para>TLS 握手超过连接超时。</para></exception>
    public void UpgradeToTls()
    {
        ThrowIfDisposed();
        if (_stream is null)
        {
            throw new InvalidOperationException("ADB TLS transport is not connected.");
        }

        if (_tlsActive)
        {
            throw new InvalidOperationException("ADB TLS transport is already upgraded.");
        }

        _stream = _useManagedTls ? UpgradeWithManagedTls() : UpgradeWithPlatformTls();
        _tlsActive = true;
    }

    /// <summary>
    /// Determines whether the platform TLS stack can present <paramref name="certificate"/>.
    /// Windows Schannel rejects certificates whose private key is an ephemeral CNG key, which is exactly what a
    /// certificate derived in memory from the host identity key carries, so the handshake is delegated to
    /// BouncyCastle there. The decision is taken before any handshake byte is exchanged, because a failed
    /// handshake may already have written to the socket.
    /// <para>判断平台 TLS 栈能否出示 <paramref name="certificate"/>。Windows Schannel 拒绝私钥为临时 CNG
    /// 密钥的证书，而在内存中由主机身份密钥派生的证书正是如此，因此该平台把握手交给 BouncyCastle。判定在任何
    /// 握手字节交换之前完成，因为失败的握手可能已向套接字写入数据。</para>
    /// </summary>
    /// <param name="certificate">The client certificate derived from the host identity key.
    /// <para>由主机身份密钥派生的客户端证书。</para></param>
    /// <returns><see langword="true"/> when the BouncyCastle TLS stack must be used.
    /// <para>必须使用 BouncyCastle TLS 栈时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is <see langword="null"/>.
    /// <para><paramref name="certificate"/> 为 <see langword="null"/>。</para></exception>
    internal static bool RequiresManagedTls(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = certificate.GetRSAPrivateKey();
        return key is RSACng { Key.IsEphemeral: true };
    }

    private Stream UpgradeWithPlatformTls()
    {
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
            return ssl;
        }
        catch (OperationCanceledException)
        {
            ssl.Dispose();
            throw new TimeoutException($"The TLS handshake with the ADB device at {Host}:{Port} timed out.");
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            ssl.Dispose();
            throw HandshakeFailure(ex);
        }
    }

    private Stream UpgradeWithManagedTls()
    {
        var client = _client ?? throw new InvalidOperationException("ADB TLS transport is not connected.");
        var protocol = new TlsClientProtocol(GetStream());
        client.ReceiveTimeout = _connectTimeoutMs;
        client.SendTimeout = _connectTimeoutMs;
        try
        {
            protocol.Connect(new AdbManagedTlsClient(new BcTlsCrypto(new SecureRandom()), _clientCertificate));
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            throw HandshakeFailure(ex);
        }

        _managedProtocol = protocol;
        client.ReceiveTimeout = 0;
        client.SendTimeout = 0;
        return protocol.Stream;
    }

    private Exception HandshakeFailure(Exception cause) => IsSocketTimeout(cause)
        ? new TimeoutException($"The TLS handshake with the ADB device at {Host}:{Port} timed out.", cause)
        : new IOException($"The TLS handshake with the ADB device at {Host}:{Port} failed: {cause.Message}", cause);

    private static bool IsSocketTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyIoTimeout(int timeoutMs)
    {
        if (!_useManagedTls)
        {
            return;
        }

        var client = _client ?? throw new InvalidOperationException("ADB TLS transport is not connected.");
        client.ReceiveTimeout = Math.Max(0, timeoutMs);
        client.SendTimeout = Math.Max(0, timeoutMs);
    }

    private void CloseManagedProtocol()
    {
        var protocol = _managedProtocol;
        _managedProtocol = null;
        if (protocol is null)
        {
            return;
        }

        if (_client is { } client)
        {
            client.SendTimeout = _connectTimeoutMs;
        }

        try
        {
            protocol.Close();
        }
        catch (Exception)
        {
        }
    }

    private static bool AcceptAnyServerCertificate(object? sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors errors) => true;

    /// <summary>
    /// BouncyCastle TLS 1.3 client used when the platform stack cannot present the ephemeral pairing certificate.
    /// It offers only that certificate, accepts the device's self-signed server certificate without validation
    /// (AOSP trust-on-first-use) and signs the CertificateVerify message with RSA-PSS.
    /// <para>当平台栈无法出示临时配对证书时使用的 BouncyCastle TLS 1.3 客户端。它只出示该证书，不校验设备的
    /// 自签服务端证书（AOSP trust-on-first-use），并用 RSA-PSS 签署 CertificateVerify。</para>
    /// </summary>
    private sealed class AdbManagedTlsClient(BcTlsCrypto crypto, X509Certificate2 certificate)
        : DefaultTlsClient(crypto)
    {
        /// <inheritdoc />
        public override ProtocolVersion[] GetProtocolVersions() => [ProtocolVersion.TLSv13];

        /// <inheritdoc />
        public override TlsAuthentication GetAuthentication() =>
            new AdbManagedTlsAuthentication(new TlsCryptoParameters(m_context), crypto, certificate);

        private sealed class AdbManagedTlsAuthentication(
            TlsCryptoParameters parameters,
            BcTlsCrypto crypto,
            X509Certificate2 certificate) : TlsAuthentication
        {
            /// <inheritdoc />
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
            }

            /// <inheritdoc />
            public TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest)
            {
                using var key = certificate.GetRSAPrivateKey()
                    ?? throw new IOException("The ADB TLS client certificate has no RSA private key.");
                return new BcDefaultTlsCredentialedSigner(
                    parameters,
                    crypto,
                    DotNetUtilities.GetKeyPair(key).Private,
                    new Certificate(
                        [],
                        [new CertificateEntry(crypto.CreateCertificate(certificate.RawDataMemory.ToArray()), null)]),
                    SignatureAndHashAlgorithm.rsa_pss_rsae_sha256);
            }
        }
    }

    private async Task<int> ReadFromStreamAsync(byte[] buffer, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken)
    {
        var stream = GetStream();
        ApplyIoTimeout(timeoutMs);
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
        catch (IOException ex) when (IsSocketTimeout(ex))
        {
            throw new TimeoutException($"ADB TLS read timed out after {timeoutMs} ms.", ex);
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