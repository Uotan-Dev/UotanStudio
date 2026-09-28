using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents an ADB connection over a device transport: CNXN handshake, authentication,
/// message dispatch loop, and stream multiplexing.
/// <para>基于设备传输的 ADB 连接：CNXN 握手、认证、消息分发循环、流复用。</para>
/// </summary>
internal sealed class AdbConnection : IDisposable
{
    private readonly IReadOnlyList<AdbAuthentication> _authentications;
    private readonly AdbServerServices? _server;
    private readonly ICommTransport? _transport;

    internal AdbConnection(AdbServerServices server)
    {
        _server = server;
        _authentications = [];
    }

    /// <summary>Whether services are owned by an external ADB server.</summary>
    public bool UsesServer => _server is not null;

    private int _authKeyIndex;
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, AdbStream> _streams = [];
    private readonly Lock _sendLock = new();
    private readonly ManualResetEventSlim _peerReady = new(false);
    private uint _nextLocalId = 1;
    private bool _connected;
    private bool _disposed;
    private Thread? _messageLoopThread;

    /// <summary>
    /// Gets whether the connection is established.
    /// <para>获取连接是否已建立。</para>
    /// </summary>
    public bool IsConnected { get { lock (_lock) { return _connected; } } }

    /// <summary>
    /// Gets the peer protocol version.
    /// <para>获取对端协议版本。</para>
    /// </summary>
    public uint PeerVersion { get; private set; }

    /// <summary>
    /// Gets the peer max payload size.
    /// <para>获取对端最大负载大小。</para>
    /// </summary>
    public uint PeerMaxPayload { get; private set; }

    /// <summary>
    /// Gets the peer banner / system info.
    /// <para>获取对端横幅 / 系统信息。</para>
    /// </summary>
    public string? PeerBanner { get; private set; }

    /// <summary>
    /// Gets the features advertised by the peer.
    /// <para>获取对端宣告的特性。</para>
    /// </summary>
    public IReadOnlyList<string> PeerFeatures { get; private set; } = [];

    /// <summary>
    /// Gets the underlying transport.
    /// <para>获取底层传输。</para>
    /// </summary>
    public ICommTransport Transport => _transport ?? throw new NotSupportedException("Server services do not expose a raw daemon transport.");

    /// <summary>
    /// Initializes a new instance of the <see cref="AdbConnection"/> class with a
    /// single authentication identity.
    /// <para>使用单个认证身份初始化 <see cref="AdbConnection"/> 类的新实例。</para>
    /// </summary>
    /// <param name="transport">The device transport. 设备传输。</param>
    /// <param name="authentication">The RSA authentication identity. RSA 认证身份。</param>
    public AdbConnection(ICommTransport transport, AdbAuthentication authentication)
        : this(transport, authentication is null ? Array.Empty<AdbAuthentication>() : [authentication])
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AdbConnection"/> class with an
    /// ordered list of authentication identities. On AUTH token challenges each
    /// identity is tried in order (AOSP adb_auth_host.cpp NextKey semantics); when
    /// the list is exhausted the client advertises its public key. The connection
    /// OWNS the list and disposes every identity in <see cref="Dispose"/>; callers
    /// must not dispose the identities themselves.
    /// <para>使用有序认证身份列表初始化 <see cref="AdbConnection"/> 类的新实例。
    /// AUTH 令牌挑战时按顺序尝试每个身份（AOSP adb_auth_host.cpp NextKey 语义）；
    /// 列表耗尽后客户端宣告其公钥。连接拥有该列表并在 <see cref="Dispose"/> 中释放
    /// 每个身份；调用方不得自行释放这些身份。</para>
    /// </summary>
    /// <param name="transport">The device transport. 设备传输。</param>
    /// <param name="authentications">The ordered RSA authentication identities. 有序 RSA 认证身份列表。</param>
    public AdbConnection(ICommTransport transport, IReadOnlyList<AdbAuthentication> authentications)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authentications = authentications ?? throw new ArgumentNullException(nameof(authentications));
        if (_authentications.Count == 0)
        {
            throw new ArgumentException("At least one authentication identity is required.", nameof(authentications));
        }
    }

    /// <summary>
    /// Connects to the ADB peer (sends CNXN and starts the message loop).
    /// <para>连接到 ADB 对端（发送 CNXN 并启动消息循环）。</para>
    /// </summary>
    public void Connect()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AdbConnection));
            }

            if (_connected && _server is null)
            {
                return;
            }
        }

        if (_server is not null)
        {
            try { _server.ValidateTarget(); }
            catch { lock (_lock) _connected = false; throw; }
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _connected = true;
                _peerReady.Set();
            }
            return;
        }
        if (_transport is UsbDeviceTransport usbTransport)
            UotanStudio.Comm.Transports.Usb.Core.UsbAccessPolicy.RequireDirect(0xff, 0x42, 1,
                usbTransport.Session.DeviceInfo.SerialNumber);
        try
        {
            Send(AdbMessaging.BuildConnect());
        }
        catch (Exception ex) when ((ex is System.ComponentModel.Win32Exception or IOException)
            && _transport is UsbDeviceTransport)
        {
            _transport.Reset();
            Send(AdbMessaging.BuildConnect());
        }
        _connected = true;
        _authKeyIndex = 0;

        _messageLoopThread = new Thread(MessageLoop) { IsBackground = true, Name = "ADB message loop" };
        _messageLoopThread.Start();
    }

    /// <summary>
    /// Asynchronously connects to the ADB peer.
    /// <para>异步连接到 ADB 对端。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default) => await Task.Run(Connect, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens a stream to a service (e.g. "shell:v2:...", "sync:").
    /// <para>打开到服务的流。</para>
    /// </summary>
    /// <param name="service">The service destination string. 服务目标字符串。</param>
    /// <returns>An ADB stream. ADB 流。</returns>
    public AdbStream OpenStream(string service)
    {
        if (service is null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        EnsureConnected();

        if (_server is not null)
        {
            var serviceStream = _server.Open(service);
            lock (_lock)
            {
                if (_disposed) { serviceStream.Dispose(); throw new ObjectDisposedException(nameof(AdbConnection)); }
                var id = _nextLocalId++;
                var serverStream = new AdbStream(this, id, serviceStream);
                _streams.Add(id, serverStream);
                return serverStream;
            }
        }
        uint localId;
        AdbStream stream;
        lock (_lock)
        {
            localId = _nextLocalId++;
            stream = new AdbStream(this, localId);
            _streams[localId] = stream;
        }

        Send(AdbMessaging.BuildOpen(localId, service));
        return stream;
    }

    /// <summary>
    /// Waits for the peer CNXN banner to arrive (handshake complete).
    /// <para>等待对端 CNXN 横幅到达（握手完成）。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns><c>true</c> if the peer responded; otherwise, <c>false</c>. 对端是否响应。</returns>
    /// <param name="cancellationToken">Cancels the handshake wait.</param>
    public bool WaitForPeer(int timeoutMs = 10000, CancellationToken cancellationToken = default)
        => _peerReady.Wait(timeoutMs, cancellationToken);

    /// <summary>
    /// Writes data to the specified stream.
    /// <para>向指定流写入数据。</para>
    /// </summary>
    /// <param name="stream">The target stream. 目标流。</param>
    /// <param name="data">The data to write. 要写入的数据。</param>
    internal void WriteToStream(AdbStream stream, byte[] data)
    {
        EnsureConnected();

        if (stream.RemoteId == 0)
        {
            stream.WaitReady(5000);
        }

        if (stream.RemoteId == 0)
        {
            throw new InvalidOperationException("Stream was not acknowledged by the peer.");
        }

        Send(AdbMessaging.BuildWrite(stream.LocalId, stream.RemoteId, data));
    }

    /// <summary>
    /// Closes the specified stream and notifies the peer.
    /// <para>关闭指定流并通知对端。</para>
    /// </summary>
    /// <param name="stream">The stream to close. 要关闭的流。</param>
    internal void CloseStream(AdbStream stream)
    {
        lock (_lock) { _streams.Remove(stream.LocalId); }
        if (_server is null) Send(AdbMessaging.BuildClose(stream.LocalId, stream.RemoteId));
    }

    /// <summary>
    /// Sends a message to the transport.
    /// <para>向传输发送消息。</para>
    /// </summary>
    /// <param name="message">The message to send. 要发送的消息。</param>
    private void Send(AdbMessage message)
    {
        lock (_sendLock)
        {
            var header = AdbMessaging.SerializeHeader(message);
            Transport.Write(header, 0, header.Length, 5000);

            if (message.Payload is { Length: > 0 } payload)
            {
                Transport.Write(payload, 0, payload.Length, 5000);
            }
        }
    }

    /// <summary>
    /// Reads the next ADB message from the transport.
    /// <para>从传输读取下一条 ADB 消息。</para>
    /// </summary>
    private AdbMessage ReadMessage()
    {
        var header = ReadExact(AdbConstants.HeaderSize, 5000, allowIdle: true);
        (var command, var arg0, var arg1, var payloadLength, var payloadCrc) = AdbMessaging.ParseHeader(header);

        if (payloadLength > AdbConstants.MaxPayload)
        {
            throw new InvalidDataException($"ADB payload length {payloadLength} exceeds the protocol maximum.");
        }

        byte[]? payload = null;
        if (payloadLength > 0)
        {
            payload = ReadExact((int)payloadLength, 5000);
            AdbMessaging.VerifyCrc(payload, payloadCrc);
        }

        return new AdbMessage(command, arg0, arg1, payload);
    }

    /// <summary>
    /// Main message dispatch loop running on a background thread.
    /// <para>在后台线程上运行的主消息分发循环。</para>
    /// </summary>
    private void MessageLoop()
    {
        try
        {
            while (!_disposed)
            {
                var message = ReadMessage();
                switch (message.Command)
                {
                    case AdbCommand.Auth: HandleAuth(message); break;
                    case AdbCommand.Cnxn: HandleConnect(message); break;
                    case AdbCommand.Open: HandleOpen(message); break;
                    case AdbCommand.Okay: HandleOkay(message); break;
                    case AdbCommand.Wrte: HandleWrite(message); break;
                    case AdbCommand.Clse: HandleClose(message); break;
                    case AdbCommand.Sync:
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            FaultAllStreams(ex);
        }
    }

    /// <summary>
    /// Handles the AUTH handshake from the peer (TOKEN / SIGNATURE / RSAPUBLICKEY).
    /// <para>处理来自对端的 AUTH 握手（TOKEN / SIGNATURE / RSAPUBLICKEY）。</para>
    /// </summary>
    /// <param name="message">The AUTH message. AUTH 消息。</param>
    private void HandleAuth(AdbMessage message)
    {
        switch ((AdbAuthType)message.Arg0)
        {
            case AdbAuthType.Token:
                var token = message.Payload ?? [];
                if (_authKeyIndex < _authentications.Count)
                {
                    var signature = _authentications[_authKeyIndex].SignToken(token);
                    _authKeyIndex++;
                    Send(AdbMessaging.BuildAuth(AdbAuthType.Signature, signature));
                }
                else
                {
                    var publicKey = _authentications[^1].BuildPublicKeyPayload();
                    Send(AdbMessaging.BuildAuth(AdbAuthType.RsaPublicKey, publicKey));
                }
                break;

            case AdbAuthType.RsaPublicKey:
                var explicitPublicKey = _authentications[0].BuildPublicKeyPayload();
                Send(AdbMessaging.BuildAuth(AdbAuthType.RsaPublicKey, explicitPublicKey));
                break;

            case AdbAuthType.Signature:
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Handles the CNXN connect message from the peer.
    /// <para>处理来自对端的 CNXN 连接消息。</para>
    /// </summary>
    /// <param name="message">The CNXN message. CNXN 消息。</param>
    private void HandleConnect(AdbMessage message)
    {
        PeerVersion = message.Arg0;
        PeerMaxPayload = message.Arg1;
        PeerBanner = message.PayloadAsString();
        PeerFeatures = ParseFeatures(PeerBanner);
        _peerReady.Set();
    }

    /// <summary>
    /// Handles the OPEN message from the peer.
    /// <para>处理来自对端的 OPEN 消息。</para>
    /// </summary>
    /// <param name="message">The OPEN message. OPEN 消息。</param>
    private void HandleOpen(AdbMessage message)
    {
        AdbStream stream;
        lock (_lock)
        {
            stream = new AdbStream(this, _nextLocalId++);
            _streams[stream.LocalId] = stream;
        }
        Send(AdbMessaging.BuildOkay(stream.LocalId, message.Arg0));
    }

    /// <summary>
    /// Handles the OKAY message from the peer.
    /// <para>处理来自对端的 OKAY 消息。</para>
    /// </summary>
    /// <param name="message">The OKAY message. OKAY 消息。</param>
    private void HandleOkay(AdbMessage message)
    {
        AdbStream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(message.Arg1, out stream);
        }

        if (stream is null)
        {
            Send(AdbMessaging.BuildClose(message.Arg1, message.Arg0));
            return;
        }

        stream.SetRemoteId(message.Arg0);
    }

    /// <summary>
    /// Handles the WRTE message from the peer.
    /// <para>处理来自对端的 WRTE 消息。</para>
    /// </summary>
    /// <param name="message">The WRTE message. WRTE 消息。</param>
    private void HandleWrite(AdbMessage message)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg1, out var stream))
            {
                stream.EnqueueData(message.Payload ?? []);
                Send(AdbMessaging.BuildOkay(message.Arg1, message.Arg0));
            }
            else
            {
                Send(AdbMessaging.BuildClose(message.Arg1, message.Arg0));
            }
        }
    }

    /// <summary>
    /// Handles the CLSE message from the peer.
    /// <para>处理来自对端的 CLSE 消息。</para>
    /// </summary>
    /// <param name="message">The CLSE message. CLSE 消息。</param>
    private void HandleClose(AdbMessage message)
    {
        AdbStream? stream;
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg1, out stream))
            {
                _streams.Remove(message.Arg1);
            }
        }

        if (stream is not null)
        {
            stream.MarkClosed();
        }
    }

    /// <summary>
    /// Faults all open streams due to an error.
    /// <para>因错误将所有打开的流标记为故障。</para>
    /// </summary>
    /// <param name="ex">The exception. 异常。</param>
    private void FaultAllStreams(Exception ex)
    {
        AdbStream[] streams;
        lock (_lock)
        {
            streams = [.. _streams.Values];
            _streams.Clear();
            _connected = false;
        }

        foreach (var stream in streams)
        {
            stream.MarkFaulted(ex);
        }
    }

    /// <summary>
    /// Parses the features list from the peer banner.
    /// <para>从对端横幅解析特性列表。</para>
    /// </summary>
    /// <param name="banner">The peer banner string. 对端横幅字符串。</param>
    private static IReadOnlyList<string> ParseFeatures(string? banner)
    {
        if (string.IsNullOrEmpty(banner))
        {
            return [];
        }

        var idx = banner.IndexOf("features=", StringComparison.Ordinal);
        if (idx < 0)
        {
            return [];
        }

        var remainder = banner[(idx + "features=".Length)..];
        var end = remainder.IndexOf(';');
        var value = end >= 0 ? remainder[..end] : remainder;
        return value
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(feature => feature.Trim())
            .Where(feature => feature.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Reads an exact number of bytes from the transport.
    /// <para>从传输读取精确数量的字节。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. 要读取的字节数。</param>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <param name="allowIdle">Keep waiting between packets; incomplete packets still time out.</param>
    private byte[] ReadExact(int length, int timeoutMs, bool allowIdle = false)
    {
        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int n;
            try { n = Transport.Read(buffer, totalRead, length - totalRead, timeoutMs); }
            catch (TimeoutException) when (allowIdle && totalRead == 0 && !_disposed && Transport.IsConnected)
            {
                continue;
            }
            if (n == 0)
            {
                if (allowIdle && totalRead == 0 && !_disposed && Transport.IsConnected) continue;
                throw new TimeoutException("ADB read timeout");
            }

            totalRead += n;
        }
        return buffer;
    }

    /// <summary>
    /// Ensures the connection is established before performing operations.
    /// <para>确保在执行操作之前已建立连接。</para>
    /// </summary>
    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Connection not established. Call Connect() first.");
        }
    }

    /// <summary>
    /// Releases all resources used by the connection.
    /// <para>释放连接使用的所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_lock)
        {
            _connected = false;
            foreach (var stream in _streams.Values)
            {
                stream.MarkClosed();
            }

            _streams.Clear();
        }

        _transport?.Dispose();
        foreach (var auth in _authentications)
        {
            auth.Dispose();
        }
        _peerReady.Dispose();
        GC.SuppressFinalize(this);
    }
}