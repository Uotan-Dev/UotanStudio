using System.Buffers;
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

    /// <summary>
    /// Largest byte count one HDC USB block may announce. <see cref="HdcUsbHead.DataSize"/> covers the
    /// whole daemon packet that follows the USB header, so the ceiling is the payload limit plus the
    /// fixed daemon PayloadHead of <see cref="HdcConstants.HeaderSize"/> bytes plus
    /// <see cref="HdcDaemonCodec.MaximumMetadataSize"/>, which is 1035 framing bytes on top of the payload limit.
    /// Blocks above it can never satisfy <see cref="HdcDaemonCodec.Decode"/> and are rejected before their body is read.
    /// <para>HDC 单个 USB 块可声明的最大字节数。<see cref="HdcUsbHead.DataSize"/> 覆盖 USB 头之后的完整守护进程报文，
    /// 因此上限等于负载上限加上 <see cref="HdcConstants.HeaderSize"/> 字节的固定守护进程头与
    /// <see cref="HdcDaemonCodec.MaximumMetadataSize"/>，即负载上限再加 1035 字节帧开销。
    /// 超过该值的块无法满足 <see cref="HdcDaemonCodec.Decode"/>，会在读取其主体前被拒绝。</para>
    /// </summary>
    internal const int MaximumUsbBlockSize = HdcConstants.MaxPayloadSize + HdcConstants.HeaderSize + HdcDaemonCodec.MaximumMetadataSize;

    /// <summary>
    /// Capacity of the reusable buffer that absorbs one stale USB block during session setup. It is one
    /// daemon payload limit plus slack for the USB framing, so a single read can drain the largest block
    /// the peer may still be queued behind the reset marker.
    /// <para>会话建立期间复用缓冲区的容量，用于吸收单个陈旧 USB 块。其大小为一个守护进程负载上限加上 USB 帧余量，
    /// 因此一次读取即可排空对端在重置标记之后仍在排队的最大块。</para>
    /// </summary>
    private const int StaleBlockBufferSize = 513 * 1024;

    /// <summary>
    /// Overall budget for waiting until the USB peer reports an idle OUT pipe.
    /// <para>等待 USB 对端 OUT 管道变为空闲的总预算（毫秒）。</para>
    /// </summary>
    private const int StaleBlockDrainTimeoutMs = 3000;

    /// <summary>
    /// Upper bound of one blocking read issued while draining stale USB blocks.
    /// <para>排空陈旧 USB 块时单次阻塞读取的上限（毫秒）。</para>
    /// </summary>
    private const int StaleBlockReadTimeoutMs = 160;

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

    /// <summary>
    /// Persistent native TCP mappings owned by this connection.
    /// <para>由本连接持有的持久原生 TCP 映射。</para>
    /// </summary>
    /// <value>
    /// Lazily created manager whose lifetime is tied to this connection; disposed by <see cref="Dispose"/>.
    /// <para>延迟创建的管理器，生命周期跟随本连接；由 <see cref="Dispose"/> 释放。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">The connection has been disposed. <para>连接已释放。</para></exception>
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
    /// <value><see langword="true"/> after the authentication handshake completes and while the message loop has not faulted; read under the stream lock. <para>认证握手完成且消息循环未故障时为 <see langword="true"/>；在流锁保护下读取。</para></value>
    public bool IsConnected
    {
        get { lock (_streamsLock) { return _connected; } }
    }

    /// <summary>
    /// Gets the underlying transport.
    /// <para>获取底层传输。</para>
    /// </summary>
    /// <value>The transport supplied at construction; the connection disposes it together with itself. <para>构造时传入的传输；连接释放时会一并处置它。</para></value>
    public ICommTransport Transport => _transport;

    /// <summary>
    /// Gets the peer protocol version.
    /// <para>获取对端协议版本。</para>
    /// </summary>
    /// <value>The fixed constant assigned during the authentication handshake; 0 before <see cref="Connect()"/> succeeds. <para>认证握手中赋值的固定常量；<see cref="Connect()"/> 成功前为 0。</para></value>
    public uint PeerVersion => _peerVersion;

    /// <summary>
    /// Gets the peer banner / system info.
    /// <para>获取对端横幅 / 系统信息。</para>
    /// </summary>
    /// <value>The handshake banner, later replaced by the daemon-reported <c>devname</c> when present; <see langword="null"/> before authentication. <para>握手横幅，认证中若上报 <c>devname</c> 会被替换；认证前为 <see langword="null"/>。</para></value>
    public string? PeerBanner => _peerBanner;

    /// <summary>
    /// The version string reported by the daemon, without substituting our own protocol version.
    /// <para>守护进程上报的版本字符串，不会用本端协议版本替换。</para>
    /// </summary>
    /// <value>The daemon handshake version; empty until the authentication handshake assigns it. <para>守护进程握手版本；认证握手赋值前为空字符串。</para></value>
    public string PeerVersionString => _peerVersionString;

    /// <summary>
    /// Capabilities reported in the daemon's successful authentication response.
    /// <para>守护进程认证成功响应中上报的能力集。</para>
    /// </summary>
    /// <value>Read-only capability map decoded from the final handshake state; empty until authentication completes. <para>从最终握手状态解码的只读能力映射；认证完成前为空。</para></value>
    public IReadOnlyDictionary<string, string> PeerCapabilities => _peerCapabilities;

    /// <summary>
    /// Initializes a new instance of the <see cref="HdcConnection"/> class.
    /// <para>初始化 <see cref="HdcConnection"/> 类的新实例。</para>
    /// </summary>
    /// <param name="transport">The device transport. <para>设备传输。</para></param>
    /// <param name="authentication">The RSA-3072 authentication identity. <para>RSA-3072 认证身份。</para></param>
    public HdcConnection(ICommTransport transport, HdcAuthentication authentication)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    /// <summary>
    /// Connects to the HDC peer (sends session handshake and starts the message loop).
    /// <para>连接到 HDC 对端（发送会话握手并启动消息循环）。</para>
    /// </summary>
    public void Connect() => Connect(CancellationToken.None);

    /// <summary>
    /// Connects to the HDC peer, observing cancellation between bounded device waits.
    /// <para>连接到 HDC 对端，并在有界的设备等待之间响应取消。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the stale-block drain and the authentication rounds; the caller breaks an already blocked device read by disposing the transport. <para>取消陈旧块排空与认证轮次；已被阻塞的设备读取由调用方释放传输来打断。</para></param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled during setup. <para><paramref name="cancellationToken"/> 在建立期间被触发。</para></exception>
    private void Connect(CancellationToken cancellationToken)
    {
        if (_transport is UsbDeviceTransport)
            UotanStudio.Comm.Transports.Usb.Core.UsbAccessPolicy.RequireDirect(0xff, 0x50, 1);
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

            WriteExact([(byte)'U', (byte)'B', 2, 0, 0, 0, 0, 0, 0, 0, 0]);
            DrainStaleBlocks(_transport, cancellationToken);
        }

        Send(new HdcMessage(HdcCommand.Handshake, 0, _sessionId,
            HdcDaemonCodec.Handshake(0, _sessionId, string.Empty, System.Text.Encoding.ASCII.GetBytes("authtype        1               1"), "Ver: 3.2.0f")));
        var authenticated = false;
        var verifyType = _authentication.VerifyType;
        var authDeadline = Environment.TickCount64 + 120000;
        for (var round = 0; round < 8;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= authDeadline) throw new TimeoutException("HDC authentication deadline exceeded.");
            HdcMessage response;
            try { response = ReadMessage(); }
            catch (TimeoutException) { continue; }
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
            cancellationToken.ThrowIfCancellationRequested();
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

        lock (_streamsLock)
        {
            _connected = true;
        }

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
    /// <param name="cancellationToken">On cancellation the underlying transport is disposed to break the blocking handshake. <para>取消时会释放底层传输以打断阻塞的握手。</para></param>
    /// <returns>A task that completes once authentication, channel closure and the message loop startup have finished. <para>认证、通道关闭与消息循环启动全部完成后完成的任务。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled during the handshake. <para><paramref name="cancellationToken"/> 在握手期间被取消。</para></exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(() => _transport.Dispose());
        try { await Task.Run(() => Connect(cancellationToken), cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Discards USB blocks the peer still had queued when the session was reset, returning once the
    /// OUT pipe reports idle. The drain buffer is rented from <see cref="ArrayPool{T}"/> because the
    /// connection setup path otherwise commits a fresh half-megabyte for every physical session.
    /// <para>丢弃会话重置时对端仍在排队的 USB 块，OUT 管道报告空闲即返回。排空缓冲区取自
    /// <see cref="ArrayPool{T}"/>，否则每个物理会话的建立路径都要新分配半兆字节。</para>
    /// </summary>
    /// <param name="transport">The USB transport whose pending blocks are discarded. <para>需要丢弃待读块的 USB 传输。</para></param>
    /// <param name="cancellationToken">Checked before every bounded wait; cancellation stops the drain without consuming further blocks. <para>在每次有界等待前检查；取消会停止排空且不再消费后续的块。</para></param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled while blocks were still pending. <para>仍有待读块时 <paramref name="cancellationToken"/> 被触发。</para></exception>
    /// <exception cref="IOException">The transport kept delivering data past <see cref="StaleBlockDrainTimeoutMs"/>. <para>传输在 <see cref="StaleBlockDrainTimeoutMs"/> 之后仍在持续交付数据。</para></exception>
    internal static void DrainStaleBlocks(ICommTransport transport, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StaleBlockBufferSize);
        var deadline = Environment.TickCount64 + StaleBlockDrainTimeoutMs;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    throw new IOException("HDC USB session did not become idle.");
                }

                if (transport.Read(buffer, 0, StaleBlockBufferSize, (int)Math.Min(remaining, StaleBlockReadTimeoutMs)) == 0)
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reports whether an announced USB block size exceeds what the daemon framing can encode,
    /// so the body is never allocated for a block that <see cref="HdcDaemonCodec.Decode"/> must reject.
    /// <para>判断声明的 USB 块大小是否超出守护进程帧编码能力，从而避免为
    /// <see cref="HdcDaemonCodec.Decode"/> 必然拒绝的块分配主体。</para>
    /// </summary>
    /// <param name="dataSize">The <see cref="HdcUsbHead.DataSize"/> value carried by the USB header. <para>USB 头部携带的 <see cref="HdcUsbHead.DataSize"/> 值。</para></param>
    /// <returns><see langword="true"/> when the block is larger than <see cref="MaximumUsbBlockSize"/>. <para>块大于 <see cref="MaximumUsbBlockSize"/> 时为 <see langword="true"/>。</para></returns>
    internal static bool ExceedsUsbBlockLimit(uint dataSize) => dataSize > MaximumUsbBlockSize;

    /// <summary>
    /// Opens a channel to a remote service (e.g. "shell:cmd", "file:sync", "forward:tcp:").
    /// <para>打开到远端服务的通道（如 "shell:cmd"、"file:sync"、"forward:tcp:"）。</para>
    /// </summary>
    /// <param name="command">The service command string. <para>服务命令字符串。</para></param>
    /// <returns>An HDC stream. <para>HDC 流。</para></returns>
    /// <exception cref="NotSupportedException">The prefix is neither <c>shell:</c> nor <c>exec:</c>; native tasks must use the <see cref="OpenChannel(HdcCommand, byte[])"/> overload. <para>前缀既不是 <c>shell:</c> 也不是 <c>exec:</c>；原生任务须使用 <see cref="OpenChannel(HdcCommand, byte[])"/> 重载。</para></exception>
    public HdcStream OpenChannel(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.StartsWith("shell:", StringComparison.Ordinal))
            return OpenChannel(HdcCommand.UnityExecute, System.Text.Encoding.UTF8.GetBytes(command[6..]));
        if (command.StartsWith("exec:", StringComparison.Ordinal))
            return OpenChannel(HdcCommand.UnityExecute, System.Text.Encoding.UTF8.GetBytes(command[5..]));
        throw new NotSupportedException("Use a native HDC command identifier for device channels.");
    }

    /// <summary>
    /// Starts a native task directly on a shared channel ID, without a host-channel handshake.
    /// <para>在共享通道 ID 上直接启动原生任务，不进行宿主通道握手。</para>
    /// </summary>
    /// <param name="command">Native task command; it also becomes the channel's data command, except <c>shell:</c> traffic which uses <see cref="HdcCommand.ShellData"/>. <para>原生任务命令；它同时成为通道的数据命令，仅 <c>shell:</c> 流量改用 <see cref="HdcCommand.ShellData"/>。</para></param>
    /// <param name="initialPayload">Payload carried by the opening message, or <see langword="null"/> for none. <para>打开消息携带的负载；为 <see langword="null"/> 表示不带负载。</para></param>
    /// <returns>
    /// The registered stream sharing the local and remote channel ID; the caller owns and must dispose it.
    /// <para>注册后的流，本地与远端通道 ID 相同；由调用方持有并负责释放。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">The connection has not been established. <para>连接尚未建立。</para></exception>
    /// <exception cref="IOException">The 128-channel limit is reached or the 32-bit channel IDs are exhausted. <para>达到 128 通道上限，或 32 位通道 ID 已耗尽。</para></exception>
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
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时（毫秒）。</para></param>
    /// <returns><c>true</c> if the peer responded; otherwise, <c>false</c>. <para>对端是否在超时前响应。</para></returns>
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
                    if (ExceedsUsbBlockLimit(usb.DataSize))
                        throw new InvalidDataException("Invalid stale HDC USB block.");
                    if (usb.DataSize != 0) _ = ReadBody((int)usb.DataSize);
                    continue;
                }
                if (usb.SessionId != _sessionId) throw new InvalidDataException("Unexpected HDC USB session.");
                if ((usb.Option & 2) != 0) throw new IOException("HDC USB session reset by peer.");
                if (usb.Option == 0 && usb.DataSize == 0) continue;
                if (usb.Option != 1 || usb.DataSize < HdcConstants.HeaderSize || ExceedsUsbBlockLimit(usb.DataSize))
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