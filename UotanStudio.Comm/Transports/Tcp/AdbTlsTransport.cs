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
/// upgrade is driven by the ADB protocol layer in protocol order: the host sends its A_CNXN first, and only a
/// device that answers with A_STLS gets the 24-byte STLS reply followed by the TLS 1.3 client handshake
/// presenting the certificate registered during pairing. Plaintext devices therefore keep speaking CNXN/AUTH
/// on an untouched wire. The device's self-signed server certificate is accepted without validation: adbd generates a random RSA key per process and rebuilds its certificate with the current time as
/// <c>notBefore</c> on every handshake, so the certificate differs on each connection and the device, not the
/// host, is the side that authenticates a key.
/// <para>直连 adbd Android 11+ 安全服务（<c>_adb-tls-connect</c>）的 ADB 传输。构造或重连只建立明文 TCP
/// 连接、不在线路上写任何字节，因为升级由 ADB 协议层按协议顺序驱动：主机先发 A_CNXN，只有以 A_STLS
/// 应答的设备才会收到 24 字节 STLS 回复，随后出示配对阶段登记的证书完成 TLS 1.3 客户端握手。明文设备
/// 因此在未被触碰的线路上继续使用 CNXN/AUTH。设备自签的服务端证书接受且不校验：adbd 每个进程
/// 生成随机 RSA 密钥，并在每次握手时以当前时间作为 <c>notBefore</c> 重新生成证书，因此证书每次连接都会变化，
/// 认证方向是设备校验主机密钥而不是主机校验设备。</para>
/// <para>On Windows the platform stack (Schannel) rejects the in-memory host identity certificate because its
/// private key is an ephemeral CNG key, so the handshake falls back to BouncyCastle TLS there. That fallback keeps
/// the connection working but performs blocking I/O: a read runs on a pool thread and a write runs on the calling
/// thread, because BouncyCastle serializes awaited reads and writes through one semaphore and a permanently pending
/// read would otherwise block every write of the full-duplex ADB protocol.
/// A TLS record layer cannot resume after an interrupted read, so a deadline is only honoured for a frame that
/// already started arriving (the ADB connection waits for the next frame without a deadline) and an expired deadline
/// marks the transport dead instead of leaving a corrupted stream in the pool; TCP keepalive is the liveness
/// backstop for a peer that vanished while the link was idle. Disconnecting or disposing the transport sends the TLS
/// close_notify alert on that fallback path just as the platform stack does, ignoring teardown failures so that
/// releasing the transport never fails.</para>
/// <para>在 Windows 上平台栈（Schannel）会拒绝内存中派生的主机身份证书，因为其私钥是临时 CNG 密钥，因此该平台回退到
/// BouncyCastle TLS。该回退保证连接可用，但执行阻塞式 I/O：读取运行在线程池线程上，写入运行在调用线程上，因为
/// BouncyCastle 用一个信号量串行化被 await 的读写，而常驻的读取会阻塞全双工 ADB 协议的每一次写入。TLS 记录层在读取
/// 被打断后无法继续，因此期限只对已经开始到达的帧生效（ADB 连接等待下一帧时不设期限），期限到达即把传输标记为失效，
/// 而不是把损坏的流留在池中；链路空闲期间消失的对端由 TCP keepalive 兜底发现。断开或释放传输时该回退路径与平台栈一样
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
    /// Set once a read or write failed on the established connection, or the managed TLS protocol reported a
    /// close or failure; <see cref="IsConnected"/> then reports the transport as dead so pooled sessions are
    /// evicted instead of reused after the device dropped the connection.
    /// <para>连接建立后读或写失败、或托管 TLS 协议报告关闭/失败时置位；此后 <see cref="IsConnected"/> 报告传输
    /// 已失效，使池中的会话被淘汰而不是在设备断开后继续复用。</para>
    /// </summary>
    private volatile bool _faulted;

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
    /// Gets a value indicating whether the transport is currently connected. A connection whose managed TLS
    /// protocol was closed or failed, or on which a read/write already failed, is reported as disconnected even
    /// though the socket object still exists.
    /// <para>获取传输当前是否已连接。托管 TLS 协议已关闭或失败、或读写已经失败的连接，即使套接字对象仍在，也报告为
    /// 未连接。</para>
    /// </summary>
    public bool IsConnected => !_disposed && !_faulted && _stream is not null &&
        _managedProtocol is not { IsClosed: true } and not { IsFailed: true };

    /// <summary>
    /// Gets whether the TLS handshake completed and application data now travels inside the encrypted stream.
    /// <para>获取 TLS 握手是否已完成、应用数据是否已进入加密流。</para>
    /// </summary>
    /// <value><see langword="true"/> after <see cref="UpgradeToTls"/> succeeded.
    /// <para><see cref="UpgradeToTls"/> 成功后为 <see langword="true"/>。</para></value>
    internal bool IsEncrypted => _tlsActive;

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
    /// Reads up to <paramref name="length"/> bytes that have already arrived from the current (plaintext or TLS)
    /// stream. Zero means the peer closed the connection; a timeout before any byte arrived raises
    /// <see cref="TimeoutException"/> instead of losing a partial frame.
    /// <para>从当前（明文或 TLS）流读取已经到达的最多 <paramref name="length"/> 个字节。返回 0 表示对端关闭
    /// 连接；一个字节都未到达即超时会抛出 <see cref="TimeoutException"/>，不会丢失半帧。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes read, or zero at end of stream. <para>读取的字节数；流结束时为 0。</para></returns>
    /// <exception cref="TimeoutException">No byte arrived within <paramref name="timeoutMs"/>.
    /// <para>在 <paramref name="timeoutMs"/> 内没有字节到达。</para></exception>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
        => ReadAsync(buffer, offset, length, timeoutMs).GetAwaiter().GetResult();

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes asynchronously from the current (plaintext or TLS) stream.
    /// Zero means the peer closed the connection; a timeout before any byte arrived raises
    /// <see cref="TimeoutException"/> instead of losing a partial frame.
    /// <para>异步从当前（明文或 TLS）流读取已经到达的最多 <paramref name="length"/> 个字节。返回 0 表示对端
    /// 关闭连接；一个字节都未到达即超时会抛出 <see cref="TimeoutException"/>，不会丢失半帧。</para>
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
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ValidateBuffer(buffer, offset, length);
        if (length == 0)
        {
            return 0;
        }

        var stream = GetStream();
        ApplyReadTimeout(timeoutMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
        {
            timeoutCts.CancelAfter(timeoutMs);
        }

        try
        {
            if (_managedProtocol is not null)
            {
                var read = await Task.Run(() => stream.Read(buffer, offset, length), CancellationToken.None)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return read;
            }

            return await stream.ReadAsync(buffer.AsMemory(offset, length), timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            FaultInterruptedTlsStream();
            throw new TimeoutException($"ADB TLS read timed out after {timeoutMs} ms.");
        }
        catch (IOException ex) when (IsSocketTimeout(ex))
        {
            FaultInterruptedTlsStream();
            throw new TimeoutException($"ADB TLS read timed out after {timeoutMs} ms.", ex);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _faulted = true;
            throw ClientKeyRejection(ex) ?? ex;
        }
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
        ApplyWriteTimeout(timeoutMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
        {
            timeoutCts.CancelAfter(timeoutMs);
        }

        try
        {
            if (_managedProtocol is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(data, offset, length);
                return length;
            }

            await stream.WriteAsync(data.AsMemory(offset, length), timeoutCts.Token).ConfigureAwait(false);
            return length;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            FaultInterruptedTlsStream();
            throw new TimeoutException($"ADB TLS write timed out after {timeoutMs} ms.");
        }
        catch (IOException ex) when (IsSocketTimeout(ex))
        {
            FaultInterruptedTlsStream();
            throw new TimeoutException($"ADB TLS write timed out after {timeoutMs} ms.", ex);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _faulted = true;
            throw ClientKeyRejection(ex) ?? ex;
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
        _clientCertificate.Dispose();
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
            _faulted = false;
            TcpKeepAlive.Enable(client.Client);
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
                RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
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

    /// <summary>
    /// Converts a handshake failure into the exception callers act on: a socket deadline stays a
    /// <see cref="TimeoutException"/>, a fatal alert from the device that rejects this host's key becomes an
    /// <see cref="IOException"/> naming the remedy, and every other cause keeps the TLS reason.
    /// <para>把握手失败转换为调用方可处理的异常：套接字期限保持 <see cref="TimeoutException"/>；设备以致命告警
    /// 拒绝本机密钥时转为说明补救方式的 <see cref="IOException"/>；其余原因保留 TLS 原因。</para>
    /// </summary>
    /// <param name="cause">The failure raised by the TLS stack. <para>TLS 栈抛出的失败。</para></param>
    /// <returns>The exception to throw. <para>要抛出的异常。</para></returns>
    private Exception HandshakeFailure(Exception cause)
    {
        if (IsSocketTimeout(cause))
        {
            return new TimeoutException($"The TLS handshake with the ADB device at {Host}:{Port} timed out.", cause);
        }

        return ClientKeyRejection(cause)
            ?? new IOException($"The TLS handshake with the ADB device at {Host}:{Port} failed: {cause.Message}", cause);
    }

    /// <summary>
    /// Builds the actionable failure for a device that rejected this host's key, or returns
    /// <see langword="null"/> when the TLS stack reported anything else.
    /// <para>为拒绝本机密钥的设备构造可处理的失败；TLS 栈报告其他原因时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="cause">The failure raised by the TLS stack. <para>TLS 栈抛出的失败。</para></param>
    /// <returns>The rejection failure, or <see langword="null"/>. <para>密钥被拒的失败，或 <see langword="null"/>。</para></returns>
    private IOException? ClientKeyRejection(Exception cause) => IsClientKeyRejected(cause)
        ? new IOException(
            $"The ADB device at {Host}:{Port} rejected this host's ADB key during the TLS handshake. " +
            "Re-run QR pairing (or accept the device's debugging prompt) and retry.", cause)
        : null;

    /// <summary>
    /// Reports whether the peer sent a fatal alert for the client certificate, which adbd produces when the
    /// presented key is not in its authorized list (<c>adbd_tls_verify_cert</c>).
    /// <para>判断对端是否针对客户端证书发送了致命告警；当出示的密钥不在 adbd 的授权列表
    /// （<c>adbd_tls_verify_cert</c>）中时会产生该告警。</para>
    /// </summary>
    /// <param name="exception">The failure to inspect, including its inner causes.
    /// <para>待检查的失败及其内部原因。</para></param>
    /// <returns><see langword="true"/> when the peer rejected the client certificate.
    /// <para>对端拒绝客户端证书时为 <see langword="true"/>。</para></returns>
    internal static bool IsClientKeyRejected(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TlsFatalAlertReceived alert &&
                alert.AlertDescription is AlertDescription.bad_certificate or AlertDescription.certificate_unknown
                    or AlertDescription.unknown_ca or AlertDescription.access_denied)
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>
    /// Applies the receive deadline to the managed TLS stack, which reads the socket directly and therefore can only
    /// honour a deadline through the socket option. A read deadline is only ever passed for a frame that already
    /// started arriving; the platform stack and the plaintext phase enforce their deadline through the linked
    /// cancellation token instead.
    /// <para>给托管 TLS 栈设置读取期限；它直接读取套接字，因此只能通过套接字选项设置期限。读取期限只会传入已经开始
    /// 到达的帧；平台栈与明文阶段改用联动取消令牌执行期限。</para>
    /// </summary>
    /// <param name="timeoutMs">Deadline in milliseconds; zero or negative waits indefinitely.
    /// <para>期限（毫秒）；零或负数表示无限等待。</para></param>
    private void ApplyReadTimeout(int timeoutMs)
    {
        if (!_useManagedTls)
        {
            return;
        }

        var client = _client ?? throw new InvalidOperationException("ADB TLS transport is not connected.");
        client.ReceiveTimeout = Math.Max(0, timeoutMs);
    }

    /// <summary>
    /// Applies the send deadline to the managed TLS stack, which writes to the socket directly and therefore can only
    /// honour a deadline through the socket option.
    /// <para>给托管 TLS 栈设置写入期限；它直接写套接字，因此只能通过套接字选项设置期限。</para>
    /// </summary>
    /// <param name="timeoutMs">Deadline in milliseconds; zero or negative waits indefinitely.
    /// <para>期限（毫秒）；零或负数表示无限等待。</para></param>
    private void ApplyWriteTimeout(int timeoutMs)
    {
        if (!_useManagedTls)
        {
            return;
        }

        var client = _client ?? throw new InvalidOperationException("ADB TLS transport is not connected.");
        client.SendTimeout = Math.Max(0, timeoutMs);
    }

    /// <summary>
    /// Marks the transport dead once a read or write was interrupted on the established TLS stream. A TLS record layer
    /// keeps no reusable state after an interrupted record, so the pooled session must be rebuilt instead of continuing
    /// on it; a plaintext phase interrupted by a timeout stays reusable.
    /// <para>已建立的 TLS 流上的读取或写入被打断后把传输标记为失效。TLS 记录层在记录被中断后没有可复用的状态，因此池中
    /// 会话必须重建；明文阶段被超时打断仍可继续使用。</para>
    /// </summary>
    private void FaultInterruptedTlsStream()
    {
        if (_tlsActive)
        {
            _faulted = true;
        }
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

    /// <summary>
    /// BouncyCastle TLS 1.3 client used when the platform stack cannot present the ephemeral pairing certificate.
    /// It offers only that certificate, accepts the device's self-signed server certificate and signs
    /// the CertificateVerify message with RSA-PSS.
    /// <para>当平台栈无法出示临时配对证书时使用的 BouncyCastle TLS 1.3 客户端。它只出示该证书，接受
    /// 设备的自签服务端证书，并用 RSA-PSS 签署 CertificateVerify。</para>
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