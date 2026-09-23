using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Represents an HDC (OpenHarmony Device Connector) connection over a device transport:
/// session handshake, message dispatch loop, and channel multiplexing.
/// <para>基于设备传输的 HDC 连接：会话握手、消息分发循环、通道复用。</para>
/// </summary>
public sealed class HdcConnection : IDisposable
{
    /// <summary>Default I/O timeout in milliseconds. 默认 I/O 超时（毫秒）。</summary>
    private const int DefaultTimeoutMs = 5000;

    /// <summary>Timeout for channel-open acknowledgment in milliseconds. 通道打开确认超时（毫秒）。</summary>
    private const int ChannelOpenTimeoutMs = 10000;

    private readonly ICommTransport _transport;
    private readonly HdcAuthentication _authentication;
    private readonly Lock _sendLock = new();
    private readonly Lock _streamsLock = new();
    private readonly Dictionary<uint, HdcStream> _streams = [];
    private readonly ManualResetEventSlim _peerReady = new(false);
    private Thread? _messageThread;
    private uint _sessionId;
    private uint _peerVersion;
    private string? _peerBanner;
    private bool _connected;
    private bool _disposed;
    private string _peerVersionString = "";
    private IReadOnlyDictionary<string, string> _peerCapabilities = new Dictionary<string, string>();
    private HdcForwardManager? _forwards;

    /// <summary>
    /// Cached USB OUT endpoint max packet size, resolved once per connection.
    /// <para>USB OUT 端点的最大包大小，连接建立时解析一次并缓存，
    /// 避免每条消息重复枚举接口/端点。</para>
    /// </summary>
    private int _usbOutPacketSize;

    /// <summary>Persistent native TCP mappings owned by this connection.</summary>
    public HdcForwardManager Forwards
    {
        get
        {
            lock (_streamsLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _forwards ??= new HdcForwardManager(this);
            }
        }
    }
    private uint _nextChannelId = 1;

    /// <summary>
    /// Gets whether the connection is established.
    /// <para>获取连接是否已建立。</para>
    /// </summary>
    public bool IsConnected
    {
        get { lock (_streamsLock) { return _connected; } }
    }

    /// <summary>
    /// Gets the underlying transport.
    /// <para>获取底层传输。</para>
    /// </summary>
    public ICommTransport Transport => _transport;

    /// <summary>
    /// Gets the peer protocol version.
    /// <para>获取对端协议版本。</para>
    /// </summary>
    public uint PeerVersion => _peerVersion;

    /// <summary>
    /// Gets the peer banner / system info.
    /// <para>获取对端横幅 / 系统信息。</para>
    /// </summary>
    public string? PeerBanner => _peerBanner;

    /// <summary>The version string reported by the daemon, without substituting our own protocol version.</summary>
    public string PeerVersionString => _peerVersionString;

    /// <summary>Capabilities reported in the daemon's successful authentication response.</summary>
    public IReadOnlyDictionary<string, string> PeerCapabilities => _peerCapabilities;

    /// <summary>
    /// Initializes a new instance of the <see cref="HdcConnection"/> class.
    /// <para>初始化 <see cref="HdcConnection"/> 类的新实例。</para>
    /// </summary>
    /// <param name="transport">The device transport. 设备传输。</param>
    /// <param name="authentication">The RSA-3072 authentication identity. RSA-3072 认证身份。</param>
    public HdcConnection(ICommTransport transport, HdcAuthentication authentication)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    /// <summary>
    /// Connects to the HDC peer (sends session handshake and starts the message loop).
    /// <para>连接到 HDC 对端（发送会话握手并启动消息循环）。</para>
    /// </summary>
    public void Connect()
    {
        if (_transport is UsbDeviceTransport)
            UotanStudio.Comm.Protocols.Common.DebugBridgeRoute.RequireDirectUsb(0xff, 0x50, 1);
        lock (_streamsLock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HdcConnection));
            }

            if (_connected)
            {
                return;
            }
        }

        _sessionId = (uint)Random.Shared.Next();

        if (_transport is UsbDeviceTransport usbTransport)
        {
            _usbOutPacketSize = usbTransport.Session.DeviceInfo.Interfaces.SelectMany(i => i.Endpoints)
                .FirstOrDefault(e => e.EndpointAddress == usbTransport.Session.EndpointOut)?.MaxPacketSize ?? 0;

            // Official host_usb ClearUsbChannel: reset only the HDC session,
            // then drain old USB replies before starting a fresh handshake.
            WriteExact([(byte)'U', (byte)'B', 2, 0, 0, 0, 0, 0, 0, 0, 0]);
            var discard = new byte[513 * 1024];
            var deadline = Environment.TickCount64 + 3000;
            while (_transport.Read(discard, 0, discard.Length, 160) != 0)
                if (Environment.TickCount64 >= deadline) throw new IOException("HDC USB session did not become idle.");
        }

        // session.h auth states: NONE=0, SIGNATURE=2, PUBLICKEY=3, OK=4.
        Send(new HdcMessage(HdcCommand.Handshake, 0, _sessionId,
            HdcDaemonCodec.Handshake(0, _sessionId, string.Empty, System.Text.Encoding.ASCII.GetBytes("authtype        1               1"), "Ver: 3.2.0f")));
        var authenticated = false;
        var verifyType = _authentication.VerifyType;
        var authDeadline = Environment.TickCount64 + 120000;
        for (var round = 0; round < 8;)
        {
            if (Environment.TickCount64 >= authDeadline) throw new TimeoutException("HDC authentication deadline exceeded.");
            HdcMessage response;
            try { response = ReadMessage(); }
            catch (TimeoutException) { continue; } // Allow time for the device's host trust dialog.
            if (response.ChannelId == 0 && response.Command is HdcCommand.Echo or HdcCommand.ChannelClose)
                continue;
            round++;
            if (response.Command != HdcCommand.Handshake || response.ChannelId != 0)
                throw new InvalidDataException("Expected HDC authentication handshake.");
            var handshake = HdcDaemonCodec.ReadHandshake(response.Payload ?? []);
            if (handshake.SessionId != 0 && handshake.SessionId != _sessionId)
                throw new InvalidDataException("HDC authentication session mismatch.");
            _peerBanner = HdcConstants.HandshakeMessage;
            _peerVersion = HdcConstants.ProtocolVersion;
            _peerVersionString = handshake.Version;
            if (handshake.State == 3)
            {
                var capabilities = handshake.Buffer.AsSpan().StartsWith("authtype        "u8)
                    ? HdcCapabilities.Decode(handshake.Buffer) : new Dictionary<string, string>();
                verifyType = capabilities.TryGetValue("authtype", out var algorithm) ? algorithm switch
                {
                    "1" => HdcAuthVerifyType.Rsa3072Sha512,
                    "0" => HdcAuthVerifyType.RsaEncrypt,
                    _ => throw new NotSupportedException("Unsupported HDC authentication algorithm: " + algorithm),
                } : HdcAuthVerifyType.RsaEncrypt;
            }
            if (handshake.State == 4)
            {
                var status = System.Text.Encoding.UTF8.GetString(handshake.Buffer);
                var capabilities = handshake.Buffer.AsSpan().StartsWith("emgmsg          "u8) || handshake.Buffer.AsSpan().StartsWith("daemonauthstatus "u8)
                    ? HdcCapabilities.Decode(handshake.Buffer) : new Dictionary<string, string>();
                var diagnostic = capabilities.GetValueOrDefault("emgmsg", "");
                if (diagnostic.StartsWith("[E000002]", StringComparison.Ordinal)) continue;
                if (capabilities.GetValueOrDefault("daemonauthstatus") == "DAEMON_UNAUTH" || status == "DAEMON_UNAUTH")
                    throw new UnauthorizedAccessException("HDC device did not authorize this host: " + diagnostic);
                _peerCapabilities = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(capabilities);
                _peerBanner = capabilities.GetValueOrDefault("devname", status);
                authenticated = true; break;
            }
            byte[] buffer = handshake.State switch
            {
                3 => _authentication.BuildDaemonPublicKeyPayload(),
                2 => System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(_authentication.SignToken(handshake.Buffer, verifyType))),
                5 => throw new UnauthorizedAccessException("HDC device rejected host authorization."),
                _ => throw new NotSupportedException($"HDC authentication state {handshake.State} is not supported."),
            };
            Send(new HdcMessage(HdcCommand.Handshake, 0, _sessionId,
                HdcDaemonCodec.Handshake(handshake.State, handshake.SessionId, string.Empty, buffer, "Ver: 3.2.0f")));
        }
        if (!authenticated) throw new UnauthorizedAccessException("HDC authorization did not complete.");

        var closeDeadline = Environment.TickCount64 + 10000;
        while (true)
        {
            if (Environment.TickCount64 >= closeDeadline)
                throw new TimeoutException("HDC authentication channel did not close.");
            HdcMessage response;
            try { response = ReadMessage(); }
            catch (TimeoutException) { continue; }
            if (response.ChannelId == 0 && response.Command == HdcCommand.Echo) continue;
            if (response.ChannelId != 0 || response.Command != HdcCommand.ChannelClose ||
                response.Payload is not { Length: 1 } close)
                throw new InvalidDataException("Expected HDC authentication channel closure.");
            if (close[0] > 0)
                Send(HdcMessaging.BuildCommand(HdcCommand.ChannelClose, 0, [(byte)(close[0] - 1)]));
            break;
        }

        _connected = true;
        _peerReady.Set();

        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "HDC message loop",
        };
        _messageThread.Start();
    }

    /// <summary>
    /// Asynchronously connects to the HDC peer.
    /// <para>异步连接到 HDC 对端。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(() => _transport.Dispose());
        try { await Task.Run(Connect, cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Opens a channel to a remote service (e.g. "shell:cmd", "file:sync", "forward:tcp:").
    /// <para>打开到远端服务的通道（如 "shell:cmd"、"file:sync"、"forward:tcp:"）。</para>
    /// </summary>
    /// <param name="command">The service command string. 服务命令字符串。</param>
    /// <returns>An HDC stream. HDC 流。</returns>
    public HdcStream OpenChannel(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.StartsWith("shell:", StringComparison.Ordinal))
            return OpenChannel(HdcCommand.UnityExecute, System.Text.Encoding.UTF8.GetBytes(command[6..]));
        if (command.StartsWith("exec:", StringComparison.Ordinal))
            return OpenChannel(HdcCommand.UnityExecute, System.Text.Encoding.UTF8.GetBytes(command[5..]));
        throw new NotSupportedException("Use a native HDC command identifier for device channels.");
    }

    /// <summary>Starts a native task directly on a shared channel ID, without a host-channel handshake.</summary>
    public HdcStream OpenChannel(HdcCommand command, byte[]? initialPayload = null)
    {
        EnsureConnected();
        HdcStream stream;
        lock (_streamsLock)
        {
            if (_streams.Count >= 128) throw new IOException("HDC channel limit exceeded.");
            var id = _nextChannelId++;
            if (id == 0) throw new IOException("HDC channel IDs exhausted.");
            stream = new HdcStream(this, id) { DataCommand = command == HdcCommand.ShellInit ? HdcCommand.ShellData : command };
            stream.SetRemoteId(id);
            _streams.Add(id, stream);
        }
        try
        {
            if (command is HdcCommand.FileCheck or HdcCommand.AppCheck or HdcCommand.ForwardCheck)
                Send(new HdcMessage(HdcCommand.WakeupSlaveTask, stream.ChannelId, _sessionId, []));
            Send(new HdcMessage(command, stream.ChannelId, _sessionId, initialPayload));
        }
        catch { lock (_streamsLock) _streams.Remove(stream.ChannelId); stream.MarkClosed(); throw; }
        return stream;
    }

    /// <summary>
    /// Waits for the peer session handshake banner to arrive (handshake complete).
    /// <para>等待对端会话握手横幅到达（握手完成）。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns><c>true</c> if the peer responded; otherwise, <c>false</c>. 对端是否响应。</returns>
    public bool WaitForPeer(int timeoutMs = 10000) => _peerReady.Wait(timeoutMs);

    /// <summary>Sends a native task transition on a registered channel.</summary>
    internal void SendOnChannel(HdcStream stream, HdcCommand command, byte[] payload)
    {
        EnsureConnected();
        Send(new HdcMessage(command, stream.ChannelId, _sessionId, payload));
    }

    internal void WriteToStream(HdcStream stream, byte[] data)
    {
        EnsureConnected();

        if (stream.RemoteChannelId == 0)
        {
            stream.WaitReady(ChannelOpenTimeoutMs);
        }

        if (stream.RemoteChannelId == 0)
        {
            throw new InvalidOperationException("Stream was not acknowledged by the peer.");
        }

        Send(new HdcMessage(stream.DataCommand, stream.ChannelId, _sessionId, data));
    }

    /// <summary>
    /// Closes the specified channel and notifies the peer.
    /// <para>关闭指定通道并通知对端。</para>
    /// </summary>
    /// <param name="stream">The stream to close. 要关闭的流。</param>
    /// <param name="notify">Whether the peer still needs a close request.</param>
    internal void CloseChannel(HdcStream stream, bool notify = true)
    {
        lock (_streamsLock) { _streams.Remove(stream.ChannelId); }
        if (notify) Send(HdcMessaging.BuildCommand(HdcCommand.ChannelClose, stream.ChannelId, [1]));
    }

    private void Send(HdcMessage message)
    {
        lock (_sendLock)
        {
            var packet = HdcDaemonCodec.Encode(message);
            if (_transport is UsbDeviceTransport usb)
            {
                WriteUsbHeader(1, (uint)packet.Length);
                WriteExact(packet);
                var packetSize = _usbOutPacketSize;
                if (packetSize == 0 || packet.Length % packetSize == 0) WriteUsbHeader(0, 0);
            }
            else WriteExact(packet);
        }
    }

    private void WriteUsbHeader(byte option, uint size)
    {
        var header = new byte[11];
        new HdcUsbHead
        {
            Flag0 = (byte)'U',
            Flag1 = (byte)'B',
            Option = option,
            SessionId = _sessionId,
            DataSize = size
        }.WriteTo(header);
        WriteExact(header);
    }

    private void WriteExact(byte[] data)
    {
        var offset = 0;
        var deadline = Environment.TickCount64 + DefaultTimeoutMs;
        while (offset < data.Length)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new TimeoutException("HDC write deadline exceeded.");
            var written = _transport.Write(data, offset, data.Length - offset, (int)remaining);
            if (written <= 0 || written > data.Length - offset) throw new IOException("Incomplete HDC transport write.");
            offset += (int)written;
        }
    }

    private byte[] ReadBody(int length)
    {
        try { return ReadExact(length, DefaultTimeoutMs); }
        catch (TimeoutException ex) { throw new IOException("HDC frame body timed out.", ex); }
    }

    private HdcMessage ReadMessage()
    {
        if (_transport is UsbDeviceTransport)
        {
            while (true)
            {
                var usb = HdcUsbHead.Parse(ReadExact(11, DefaultTimeoutMs));
                if (usb.Flag0 != 'U' || usb.Flag1 != 'B')
                    throw new InvalidDataException("Invalid HDC USB identity.");
                if (!_connected && usb.SessionId != _sessionId)
                {
                    // A prior host can close during authorization with complete
                    // replies still queued. Drain bounded old-session blocks.
                    if (usb.DataSize > HdcConstants.MaxPayloadSize + 1035)
                        throw new InvalidDataException("Invalid stale HDC USB block.");
                    if (usb.DataSize != 0) _ = ReadBody((int)usb.DataSize);
                    continue;
                }
                if (usb.SessionId != _sessionId) throw new InvalidDataException("Unexpected HDC USB session.");
                if ((usb.Option & 2) != 0) throw new IOException("HDC USB session reset by peer.");
                if (usb.Option == 0 && usb.DataSize == 0) continue;
                if (usb.Option != 1 || usb.DataSize < 11 || usb.DataSize > HdcConstants.MaxPayloadSize + 1035)
                    throw new InvalidDataException("Invalid HDC USB block.");
                return HdcDaemonCodec.Decode(ReadBody((int)usb.DataSize));
            }
        }
        var header = ReadExact(11, DefaultTimeoutMs);
        var (metadata, payload) = HdcDaemonCodec.ReadHeader(header);
        var body = ReadBody(metadata + payload);
        return HdcDaemonCodec.Decode([.. header, .. body]);
    }

    /// <summary>
    /// Main message dispatch loop running on a background thread.
    /// <para>在后台线程上运行的主消息分发循环。</para>
    /// </summary>
    private void MessageLoop()
    {
        try
        {
            while (!_disposed && _connected)
            {
                HdcMessage message;
                try
                {
                    message = ReadMessage();
                }
                catch (TimeoutException) when (_connected)
                {
                    continue;
                }
                catch (InvalidDataException) { throw; }

                try
                {
                    switch (message.Command)
                    {
                        case HdcCommand.Handshake:
                            HandleHandshake(message);
                            break;

                        case HdcCommand.Echo:
                        case HdcCommand.EchoRaw:
                            HandleEcho(message);
                            break;

                        case HdcCommand.ChannelClose:
                            HandleChannelClose(message);
                            break;

                        case HdcCommand.WakeupSlaveTask:
                            // session.cpp wakes the already-created task; no task payload or reply.
                            break;

                        case HdcCommand.ShellData:
                        case HdcCommand.FileData:
                        case HdcCommand.ForwardData:
                            HandleData(message);
                            break;

                        case HdcCommand.HeartbeatMsg:
                            Send(message);
                            break;

                        default:
                            HandleData(message);
                            break;
                    }
                }
                catch { throw; }
            }
        }
        catch (Exception ex)
        {
            FaultAllStreams(ex);
        }
    }

    /// <summary>
    /// Handles a handshake message from the peer. When <c>ChannelId == 0</c> this is a
    /// session-level re-handshake; otherwise it acknowledges a channel open.
    /// <para>处理来自对端的握手消息。当 <c>ChannelId == 0</c> 时为会话级重握手；
    /// 否则为通道打开确认。</para>
    /// </summary>
    /// <param name="message">The handshake message. 握手消息。</param>
    private void HandleHandshake(HdcMessage message)
    {
        throw new InvalidDataException("Unexpected authentication message on an established HDC session.");
    }

    /// <summary>
    /// Handles an echo message from the peer (diagnostic / debug output).
    /// <para>处理来自对端的 Echo 消息（诊断 / 调试输出）。</para>
    /// </summary>
    /// <param name="message">The echo message. Echo 消息。</param>
    private void HandleEcho(HdcMessage message)
    {
        if (message.Command == HdcCommand.EchoRaw) { HandleData(message); return; }
        if (message.Payload is { Length: > 0 } payload)
        {
            if (payload[0] == 0)
            {
                lock (_streamsLock)
                    if (_streams.TryGetValue(message.ChannelId, out var stream))
                        stream.MarkFaulted(new IOException(System.Text.Encoding.UTF8.GetString(payload.AsSpan(1))));
            }
            else HandleData(message with { Payload = payload[1..] });
        }
    }

    /// <summary>
    /// Handles a channel close message from the peer.
    /// <para>处理来自对端的通道关闭消息。</para>
    /// </summary>
    /// <param name="message">The channel close message. 通道关闭消息。</param>
    private void HandleChannelClose(HdcMessage message)
    {
        HdcStream? stream;
        lock (_streamsLock)
        {
            if (_streams.TryGetValue(message.ChannelId, out stream))
            {
                _streams.Remove(message.ChannelId);
            }
        }

        stream?.MarkClosed();

        if (message.Payload is { Length: > 0 } close && close[0] > 0)
            Send(HdcMessaging.BuildCommand(HdcCommand.ChannelClose, message.ChannelId, [(byte)(close[0] - 1)]));
    }

    /// <summary>
    /// Handles incoming data and dispatches it to the corresponding stream.
    /// <para>处理传入数据并将其分发到对应的流。</para>
    /// </summary>
    /// <param name="message">The data message. 数据消息。</param>
    private void HandleData(HdcMessage message)
    {
        lock (_streamsLock)
        {
            if (_streams.TryGetValue(message.ChannelId, out var stream))
            {
                stream.EnqueueData(message with { Payload = message.Payload ?? [] });
            }
        }
    }

    /// <summary>
    /// Faults all open streams due to an error.
    /// <para>因错误将所有打开的流标记为故障。</para>
    /// </summary>
    /// <param name="ex">The exception. 异常。</param>
    private void FaultAllStreams(Exception ex)
    {
        HdcStream[] streams;
        lock (_streamsLock)
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
    /// Infers the appropriate data command for a channel based on the command string prefix.
    /// <para>根据命令字符串前缀推断通道的适当数据命令。</para>
    /// </summary>
    /// <param name="command">The channel command string. 通道命令字符串。</param>
    /// <returns>The inferred data command. 推断出的数据命令。</returns>
    private static HdcCommand InferDataCommand(string command)
    {
        if (command.StartsWith("shell", StringComparison.OrdinalIgnoreCase))
        {
            return HdcCommand.ShellData;
        }

        if (command.StartsWith("file", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("sync", StringComparison.OrdinalIgnoreCase))
        {
            return HdcCommand.FileData;
        }

        if (command.StartsWith("forward", StringComparison.OrdinalIgnoreCase))
        {
            return HdcCommand.ForwardData;
        }

        return HdcCommand.ShellData;
    }

    /// <summary>
    /// Reads an exact number of bytes from the transport.
    /// <para>从传输读取精确数量的字节。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. 要读取的字节数。</param>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns>The read bytes. 读取到的字节。</returns>
    private byte[] ReadExact(int length, int timeoutMs)
    {
        var buffer = new byte[length];
        var totalRead = 0;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (totalRead < length)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new IOException("HDC frame deadline exceeded.");
            var n = _transport.Read(buffer, totalRead, length - totalRead, (int)remaining);
            if (n == 0)
            {
                if (totalRead == 0) throw new TimeoutException("HDC idle read timeout.");
                throw new IOException("HDC frame truncated by timeout.");
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
        _forwards?.Dispose();

        lock (_streamsLock)
        {
            _connected = false;
            foreach (var stream in _streams.Values)
            {
                stream.MarkClosed();
            }

            _streams.Clear();
        }

        _transport.Dispose();
        _authentication.Dispose();
        _messageThread?.Join(DefaultTimeoutMs + 100);
        _peerReady.Dispose();
        GC.SuppressFinalize(this);
    }
}