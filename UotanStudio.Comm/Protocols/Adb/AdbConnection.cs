using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents an ADB connection over a USB transport: CNXN handshake, authentication,
/// message dispatch loop, and stream multiplexing.
/// <para>基于 USB 传输的 ADB 连接：CNXN 握手、认证、消息分发循环、流复用。</para>
/// </summary>
public sealed class AdbConnection : IDisposable
{
    private readonly IUsbDeviceSession _transport;
    private readonly object _lock = new();
    private readonly Dictionary<uint, AdbStream> _streams = new();
    private readonly object _sendLock = new();
    private readonly ManualResetEventSlim _peerReady = new(false);
    private uint _nextLocalId = 1;
    private bool _connected;
    private bool _disposed;
    private Thread? _messageLoopThread;

    /// <summary>
    /// Gets whether the connection is established.
    /// <para>获取连接是否已建立。</para>
    /// </summary>
    public bool IsConnected { get { lock (_lock) return _connected; } }

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
    public IReadOnlyList<string> PeerFeatures { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Gets the underlying USB transport.
    /// <para>获取底层 USB 传输。</para>
    /// </summary>
    public IUsbDeviceSession Transport => _transport;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdbConnection"/> class.
    /// <para>初始化 <see cref="AdbConnection"/> 类的新实例。</para>
    /// </summary>
    /// <param name="transport">The USB transport session. USB 传输会话。</param>
    public AdbConnection(IUsbDeviceSession transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Connects to the ADB peer (sends CNXN and starts the message loop).
    /// <para>连接到 ADB 对端（发送 CNXN 并启动消息循环）。</para>
    /// </summary>
    public void Connect()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AdbConnection));
            if (_connected) return;
        }

        Send(AdbMessaging.BuildConnect());
        _connected = true;

        _messageLoopThread = new Thread(MessageLoop) { IsBackground = true, Name = "ADB message loop" };
        _messageLoopThread.Start();
    }

    /// <summary>
    /// Asynchronously connects to the ADB peer.
    /// <para>异步连接到 ADB 对端。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Connect(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a stream to a service (e.g. "shell:v2:...", "sync:").
    /// <para>打开到服务的流。</para>
    /// </summary>
    /// <param name="service">The service destination string. 服务目标字符串。</param>
    /// <returns>An ADB stream. ADB 流。</returns>
    public AdbStream OpenStream(string service)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        EnsureConnected();

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
    public bool WaitForPeer(int timeoutMs = 10000) => _peerReady.Wait(timeoutMs);

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
            stream.WaitReady(5000);

        if (stream.RemoteId == 0)
            throw new InvalidOperationException("Stream was not acknowledged by the peer.");

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
        Send(AdbMessaging.BuildClose(stream.LocalId, stream.RemoteId));
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
            _transport.Write(header, header.Length, 5000);

            if (message.Payload is { Length: > 0 } payload)
            {
                _transport.Write(payload, payload.Length, 5000);
            }
        }
    }

    /// <summary>
    /// Reads the next ADB message from the transport.
    /// <para>从传输读取下一条 ADB 消息。</para>
    /// </summary>
    private AdbMessage ReadMessage()
    {
        var header = ReadExact(AdbConstants.HeaderSize, 5000);
        (AdbCommand command, uint arg0, uint arg1, uint payloadLength, uint payloadCrc) = AdbMessaging.ParseHeader(header);

        if (payloadLength > AdbConstants.MaxPayload)
            throw new InvalidDataException($"ADB payload length {payloadLength} exceeds the protocol maximum.");

        byte[]? payload = null;
        if (payloadLength > 0)
        {
            payload = ReadExact((int)payloadLength, 5000);
            if (payloadCrc != 0)
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
                AdbMessage message = ReadMessage();
                switch (message.Command)
                {
                    case AdbCommand.Cnxn: HandleConnect(message); break;
                    case AdbCommand.Open: HandleOpen(message); break;
                    case AdbCommand.Okay: HandleOkay(message); break;
                    case AdbCommand.Wrte: HandleWrite(message); break;
                    case AdbCommand.Clse: HandleClose(message); break;
                }
            }
        }
        catch (Exception ex)
        {
            FaultAllStreams(ex);
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
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg0, out AdbStream? stream))
                stream.SetRemoteId(message.Arg1);
        }
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
            if (_streams.TryGetValue(message.Arg0, out AdbStream? stream))
                stream.EnqueueData(message.Payload ?? Array.Empty<byte>());
        }
    }

    /// <summary>
    /// Handles the CLSE message from the peer.
    /// <para>处理来自对端的 CLSE 消息。</para>
    /// </summary>
    /// <param name="message">The CLSE message. CLSE 消息。</param>
    private void HandleClose(AdbMessage message)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg0, out AdbStream? stream))
            {
                _streams.Remove(message.Arg0);
                stream.MarkClosed();
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
        lock (_lock)
        {
            foreach (AdbStream stream in _streams.Values)
                stream.MarkFaulted(ex);
            _streams.Clear();
        }
    }

    /// <summary>
    /// Parses the features list from the peer banner.
    /// <para>从对端横幅解析特性列表。</para>
    /// </summary>
    /// <param name="banner">The peer banner string. 对端横幅字符串。</param>
    private static IReadOnlyList<string> ParseFeatures(string? banner)
    {
        if (string.IsNullOrEmpty(banner)) return Array.Empty<string>();
        var idx = banner.IndexOf("features=", StringComparison.Ordinal);
        if (idx < 0) return Array.Empty<string>();
        return banner.Substring(idx + "features=".Length).Trim()
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Reads an exact number of bytes from the transport.
    /// <para>从传输读取精确数量的字节。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. 要读取的字节数。</param>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    private byte[] ReadExact(int length, int timeoutMs)
    {
        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var n = _transport.ReadInto(buffer, totalRead, length - totalRead, timeoutMs);
            if (n == 0) throw new TimeoutException("ADB read timeout");
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
        if (!_connected) throw new InvalidOperationException("Connection not established. Call Connect() first.");
    }

    /// <summary>
    /// Releases all resources used by the connection.
    /// <para>释放连接使用的所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (AdbStream stream in _streams.Values)
                stream.MarkClosed();
            _streams.Clear();
        }

        _transport.Dispose();
        _peerReady.Dispose();
    }
}
