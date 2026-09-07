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
    private uint _peerSessionId;
    private uint _peerVersion;
    private string? _peerBanner;
    private bool _connected;
    private bool _disposed;
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

        // Generate random session id.
        // <para>生成随机会话 ID。</para>
        _sessionId = (uint)Random.Shared.Next();

        // Build and send our session handshake.
        // <para>构建并发送本端会话握手。</para>
        var handshake = new HdcSessionHandShake
        {
            Banner = HdcConstants.HandshakeMessage,
            AuthType = _authentication.VerifyType,
            SessionId = _sessionId,
            ConnectKey = string.Empty,
            Buf = _authentication.BuildPublicKeyPayload(),
            Version = HdcConstants.ProtocolVersion,
        };
        SendHandShake(handshake);

        // Read the peer's handshake response (synchronous, before the message loop starts).
        // <para>读取对端握手响应（同步，在消息循环启动前完成）。</para>
        var response = ReadMessage();
        if (response.Command != HdcCommand.Handshake)
        {
            throw new InvalidDataException(
                $"Expected session handshake response, but received command {response.Command}.");
        }

        if (response.Payload is not { Length: > 0 })
        {
            throw new InvalidDataException("Session handshake response contains no payload.");
        }

        var peerHandshake = HdcSessionHandShake.Parse(response.Payload);

        // Verify the peer banner.
        // <para>验证对端横幅。</para>
        if (!string.Equals(peerHandshake.Banner, HdcConstants.HandshakeMessage, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Peer banner mismatch: expected \"{HdcConstants.HandshakeMessage}\", " +
                $"got \"{peerHandshake.Banner}\".");
        }

        // Save peer session information.
        // <para>保存对端会话信息。</para>
        _peerSessionId = peerHandshake.SessionId;
        _peerVersion = peerHandshake.Version;
        _peerBanner = peerHandshake.Banner;

        // Start the background message loop.
        // <para>启动后台消息循环。</para>
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
    public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
        await Task.Run(Connect, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens a channel to a remote service (e.g. "shell:cmd", "file:sync", "forward:tcp:").
    /// <para>打开到远端服务的通道（如 "shell:cmd"、"file:sync"、"forward:tcp:"）。</para>
    /// </summary>
    /// <param name="command">The service command string. 服务命令字符串。</param>
    /// <returns>An HDC stream. HDC 流。</returns>
    public HdcStream OpenChannel(string command)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        EnsureConnected();

        uint channelId;
        HdcStream stream;
        lock (_streamsLock)
        {
            channelId = _nextChannelId++;
            stream = new HdcStream(this, channelId)
            {
                DataCommand = InferDataCommand(command),
            };
            _streams[channelId] = stream;
        }

        // Build and send the channel-level handshake with our channelId.
        // <para>构建并发送通道级握手，携带本端 channelId。</para>
        var channelPayload = HdcChannelHandShake.Serialize(command);
        Send(new HdcMessage(HdcCommand.Handshake, channelId, _sessionId, channelPayload));

        // Wait for the peer to acknowledge the channel (sets RemoteChannelId).
        // <para>等待对端确认通道（设置 RemoteChannelId）。</para>
        if (!stream.WaitReady(ChannelOpenTimeoutMs))
        {
            lock (_streamsLock) { _streams.Remove(channelId); }
            throw new TimeoutException("Channel open timed out waiting for peer acknowledgment.");
        }

        return stream;
    }

    /// <summary>
    /// Opens a channel for the specified HDC command with an optional initial payload.
    /// <para>为指定 HDC 命令打开通道，可选附带初始负载。</para>
    /// </summary>
    /// <param name="command">The HDC command identifier. HDC 命令标识。</param>
    /// <param name="initialPayload">Optional initial payload to send with the first data message.
    /// <para>可选的初始负载，随首条数据消息发送。</para></param>
    /// <returns>An HDC stream. HDC 流。</returns>
    public HdcStream OpenChannel(HdcCommand command, byte[]? initialPayload = null)
    {
        var serviceName = command switch
        {
            HdcCommand.ShellInit => "shell:",
            HdcCommand.UnityExecute => "exec:",
            HdcCommand.UnityReboot => "reboot:",
            HdcCommand.UnityRemount => "remount:",
            HdcCommand.TargetList => "list:",
            HdcCommand.TargetConnect => "connect:",
            HdcCommand.WaitFor => "wait:",
            HdcCommand.CheckServer => "check:",
            HdcCommand.FileInit => "file:",
            HdcCommand.ForwardInit => "forward:",
            _ => $"cmd:{(ushort)command}:",
        };

        var stream = OpenChannel(serviceName);

        // If an initial payload is provided, send it as the first data message.
        // <para>如果提供了初始负载，将其作为首条数据消息发送。</para>
        if (initialPayload is { Length: > 0 })
        {
            stream.DataCommand = command;
            stream.Write(initialPayload);
        }

        return stream;
    }

    /// <summary>
    /// Waits for the peer session handshake banner to arrive (handshake complete).
    /// <para>等待对端会话握手横幅到达（握手完成）。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns><c>true</c> if the peer responded; otherwise, <c>false</c>. 对端是否响应。</returns>
    public bool WaitForPeer(int timeoutMs = 10000) => _peerReady.Wait(timeoutMs);

    /// <summary>
    /// Writes data to the specified stream.
    /// <para>向指定流写入数据。</para>
    /// </summary>
    /// <param name="stream">The target stream. 目标流。</param>
    /// <param name="data">The data to write. 要写入的数据。</param>
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
    internal void CloseChannel(HdcStream stream)
    {
        lock (_streamsLock) { _streams.Remove(stream.ChannelId); }
        Send(HdcMessaging.BuildCommand(HdcCommand.ChannelClose, stream.ChannelId, null));
    }

    /// <summary>
    /// Serializes and sends a session handshake through the transport.
    /// <para>序列化并通过传输发送会话握手。</para>
    /// </summary>
    /// <param name="handshake">The session handshake to send. 要发送的会话握手。</param>
    private void SendHandShake(HdcSessionHandShake handshake)
    {
        var msg = HdcMessaging.BuildConnectHandshake(
            handshake.AuthType,
            handshake.SessionId,
            handshake.ConnectKey,
            handshake.Buf,
            handshake.Version);

        lock (_sendLock)
        {
            var data = HdcMessaging.Serialize(msg, _sessionId);
            _transport.Write(data, 0, data.Length, DefaultTimeoutMs);
        }
    }

    /// <summary>
    /// Sends a command message to the transport. If the payload exceeds the single-packet
    /// limit, it is automatically fragmented into multiple packets.
    /// <para>向传输发送命令消息。若负载超过单包限制，将自动分片为多个数据包。</para>
    /// </summary>
    /// <param name="message">The message to send. 要发送的消息。</param>
    private void Send(HdcMessage message)
    {
        lock (_sendLock)
        {
            if (message.Payload is not { Length: > 0 })
            {
                var packet = HdcMessaging.Serialize(message, _sessionId);
                _transport.Write(packet, 0, packet.Length, DefaultTimeoutMs);
                return;
            }

            // Fragment the payload when it exceeds the single-packet limit.
            // <para>负载超过单包限制时进行分片。</para>
            var payload = message.Payload;
            var offset = 0;
            while (offset < payload.Length)
            {
                var chunkSize = Math.Min(payload.Length - offset, HdcConstants.MaxPayloadSize);
                var chunk = new byte[chunkSize];
                Buffer.BlockCopy(payload, offset, chunk, 0, chunkSize);

                var fragment = new HdcMessage(
                    message.Command,
                    message.ChannelId,
                    message.SessionId,
                    chunk);

                var packet = HdcMessaging.Serialize(fragment, _sessionId);
                _transport.Write(packet, 0, packet.Length, DefaultTimeoutMs);

                offset += chunkSize;
            }
        }
    }

    /// <summary>
    /// Reads the next complete HDC message from the transport.
    /// <para>从传输读取下一条完整的 HDC 消息。</para>
    /// </summary>
    /// <returns>The parsed HDC message. 解析后的 HDC 消息。</returns>
    private HdcMessage ReadMessage()
    {
        // Step 1: Read PayloadHead (11 bytes) — transport framing header.
        // <para>步骤 1：读取 PayloadHead（11 字节）— 传输帧头。</para>
        var headerBytes = ReadExact(HdcConstants.HeaderSize, DefaultTimeoutMs);
        var head = HdcMessaging.ParseHeader(headerBytes);

        // Step 2: Read PayloadProtect channel metadata (14 bytes, always first 14 bytes
        // of the data portion). The command identifier lives in CommandFlag.
        // <para>步骤 2：读取 PayloadProtect 通道元数据（14 字节，始终为数据部分的前 14 字节）。
        // 命令标识位于 CommandFlag 字段。</para>
        var protectBytes = ReadExact(HdcPayloadProtect.Size, DefaultTimeoutMs);
        var protect = HdcMessaging.ParsePayloadProtect(protectBytes);

        // Step 3: Read the remaining data payload (DataSize - 14 bytes).
        // <para>步骤 3：读取剩余数据负载（DataSize - 14 字节）。</para>
        var dataSize = (int)head.DataSize;
        var remainingSize = dataSize - HdcPayloadProtect.Size;
        byte[]? payload = null;
        if (remainingSize > 0)
        {
            payload = ReadExact(remainingSize, DefaultTimeoutMs);
        }

        return new HdcMessage(
            (HdcCommand)protect.CommandFlag,
            protect.ChannelId,
            head.SessionId,
            payload);
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
                catch (InvalidDataException)
                {
                    // Single message parse failure — skip this message and continue the loop.
                    // <para>单条消息解析失败 — 跳过此消息并继续循环。</para>
                    continue;
                }

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

                        case HdcCommand.ShellData:
                        case HdcCommand.FileData:
                        case HdcCommand.ForwardData:
                            HandleData(message);
                            break;

                        case HdcCommand.HeartbeatMsg:
                            // Reply with a heartbeat to keep the connection alive.
                            // <para>回复心跳以保持连接活跃。</para>
                            Send(HdcMessaging.BuildHeartbeat(message.ChannelId));
                            break;

                        default:
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Per-message handler failure — log-worthy but the connection survives.
                    // <para>单条消息处理失败 — 值得记录日志，但连接不受影响。</para>
                }
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
        if (message.ChannelId != 0)
        {
            // Channel-level handshake response — acknowledge the pending channel.
            // <para>通道级握手响应 — 确认等待中的通道。</para>
            lock (_streamsLock)
            {
                if (_streams.TryGetValue(message.ChannelId, out var stream))
                {
                    // In HDC the channelId is shared between both sides.
                    // <para>在 HDC 中 channelId 由双方共享。</para>
                    stream.SetRemoteId(message.ChannelId);
                }
            }
        }
        else if (message.Payload is { Length: > 0 })
        {
            // Session-level handshake (re-handshake during the session).
            // <para>会话级握手（会话期间的重握手）。</para>
            var handshake = HdcSessionHandShake.Parse(message.Payload);
            _peerSessionId = handshake.SessionId;
            _peerVersion = handshake.Version;
            _peerBanner = handshake.Banner;
        }
    }

    /// <summary>
    /// Handles an echo message from the peer (diagnostic / debug output).
    /// <para>处理来自对端的 Echo 消息（诊断 / 调试输出）。</para>
    /// </summary>
    /// <param name="message">The echo message. Echo 消息。</param>
    private void HandleEcho(HdcMessage message)
    {
        // Echo messages carry diagnostic text in the payload.
        // Currently consumed silently; callers can extend via events if needed.
        // <para>Echo 消息在负载中携带诊断文本。
        // 当前静默消费；调用方可通过事件扩展。</para>
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

        // Echo CLSE back so the peer tears down its side.
        // <para>回送 CLSE 使对端拆除其侧流。</para>
        Send(HdcMessaging.BuildCommand(HdcCommand.ChannelClose, message.ChannelId, null));
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
                stream.EnqueueData(message.Payload ?? []);
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

        // Default to ShellData for unknown channel types.
        // <para>未知通道类型默认使用 ShellData。</para>
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
        while (totalRead < length)
        {
            var n = _transport.Read(buffer, totalRead, length - totalRead, timeoutMs);
            if (n == 0)
            {
                throw new TimeoutException("HDC read timeout.");
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
        _peerReady.Dispose();
        GC.SuppressFinalize(this);
    }
}
