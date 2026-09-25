namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// A single HDC channel stream implementing the channel handshake / data / close state machine.
/// <para>单条 HDC 通道流，实现通道握手 / 数据 / 关闭状态机。</para>
/// </summary>
public sealed class HdcStream : IDisposable
{
    private readonly HdcConnection _connection;
    private readonly Lock _lock = new();
    private readonly Queue<HdcMessage> _incoming = new();
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _closed;
    private bool _released;
    private int _queuedBytes;

    /// <summary>
    /// Gets the local channel identifier.
    /// <para>获取本地通道标识。</para>
    /// </summary>
    /// <value>The id this side assigned when the channel was opened; it stays fixed for the stream's lifetime.
    /// <para>本端在打开通道时分配的标识；在流的生命周期内保持不变。</para></value>
    public uint ChannelId { get; }

    /// <summary>
    /// Gets the remote channel identifier, or 0 until the peer acknowledges.
    /// <para>获取远端通道标识，对端确认前为 0。</para>
    /// </summary>
    /// <value>The peer-assigned id recorded by the handshake reply; 0 means the peer has not confirmed the channel yet.
    /// <para>握手回复记录的对端分配标识；0 表示对端尚未确认该通道。</para></value>
    public uint RemoteChannelId { get; internal set; }

    /// <summary>
    /// Gets or sets the data command used when sending payloads on this channel.
    /// Defaults to <see cref="HdcCommand.ShellData"/>.
    /// <para>获取或设置在此通道上发送负载时使用的数据命令。
    /// 默认为 <see cref="HdcCommand.ShellData"/>。</para>
    /// </summary>
    internal HdcCommand DataCommand { get; set; } = HdcCommand.ShellData;

    /// <summary>
    /// Gets whether the stream is closed.
    /// <para>获取流是否已关闭。</para>
    /// </summary>
    /// <value><see langword="true"/> after a local close, a peer close, or a fault; reads then drain the queue and return <see langword="null"/>.
    /// <para>本地关闭、对端关闭或发生故障后为 <see langword="true"/>；此后读取会排空队列并返回 <see langword="null"/>。</para></value>
    public bool IsClosed { get { lock (_lock) { return _closed; } } }

    /// <summary>
    /// Gets whether the stream is in a faulted state.
    /// <para>获取流是否处于故障状态。</para>
    /// </summary>
    /// <value><see langword="true"/> once an error has faulted the channel; a faulted stream is closed as well and its queue is discarded.
    /// <para>通道因错误进入故障状态后为 <see langword="true"/>；故障流同时处于关闭状态，其队列被丢弃。</para></value>
    public bool IsFaulted
    {
        get { lock (_lock) { return field; } }

        private set;
    }

    /// <summary>
    /// Gets the exception that faulted the stream, or <c>null</c> when the stream
    /// is not faulted.
    /// <para>获取导致流故障的异常；流未故障时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The fault cause recorded when the receive buffer limit was exceeded or the connection failed.
    /// <para>在接收缓冲区超限或连接失败时记录的故障原因。</para></value>
    public Exception? Fault
    {
        get { lock (_lock) { return field; } }

        private set;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HdcStream"/> class.
    /// <para>初始化 <see cref="HdcStream"/> 类的新实例。</para>
    /// </summary>
    /// <param name="connection">The HDC connection. HDC 连接。</param>
    /// <param name="channelId">The local channel identifier. 本地通道标识。</param>
    internal HdcStream(HdcConnection connection, uint channelId)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ChannelId = channelId;
    }

    /// <summary>
    /// Sets the remote channel id and signals readiness.
    /// <para>设置远端通道 ID 并触发就绪信号。</para>
    /// </summary>
    /// <param name="remoteChannelId">The remote channel identifier. 远端通道标识。</param>
    internal void SetRemoteId(uint remoteChannelId)
    {
        RemoteChannelId = remoteChannelId;
        _ready.Set();
    }

    /// <summary>
    /// Blocks until the stream is ready (peer acknowledged the channel).
    /// <para>阻塞至流就绪（对端确认通道）。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns><c>true</c> if the stream became ready; otherwise, <c>false</c>. 流是否就绪。</returns>
    internal bool WaitReady(int timeoutMs) => _ready.Wait(timeoutMs);

    /// <summary>
    /// Enqueues incoming data from the peer.
    /// <para>将来自对端的数据入队。</para>
    /// </summary>
    /// <param name="message">The command envelope. 数据负载。</param>
    internal void EnqueueData(HdcMessage message)
    {
        var payload = message.Payload ?? [];
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (_queuedBytes + payload.Length > 4 * 1024 * 1024 || _incoming.Count >= 4096)
            {
                MarkFaulted(new IOException("HDC channel receive buffer limit exceeded."));
                return;
            }
            _queuedBytes += payload.Length;
            _incoming.Enqueue(message);
        }

        _dataAvailable.Set();
    }

    /// <summary>
    /// Marks the stream as closed and releases waiting threads.
    /// <para>将流标记为已关闭并释放等待线程。</para>
    /// </summary>
    internal void MarkClosed()
    {
        lock (_lock) { _closed = true; }
        _ready.Set();
        _dataAvailable.Set();
    }

    /// <summary>
    /// Marks the stream as faulted, stores the exception, and releases waiting threads.
    /// <para>将流标记为故障，保存异常并释放等待线程。</para>
    /// </summary>
    /// <param name="ex">The exception that caused the fault. 导致故障的异常。</param>
    internal void MarkFaulted(Exception ex)
    {
        lock (_lock)
        {
            IsFaulted = true;
            Fault = ex;
            _closed = true;
            _incoming.Clear();
            _queuedBytes = 0;
        }

        _ready.Set();
        _dataAvailable.Set();
    }

    /// <summary>
    /// Writes data to the peer.
    /// <para>向对端写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write.<para>要写入的数据。</para></param>
    public void Write(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (IsClosed)
        {
            throw new InvalidOperationException("Stream is closed.");
        }

        _connection.WriteToStream(this, data);
    }

    /// <summary>
    /// Sends a task state transition on this channel.
    /// <para>在此通道上发送任务状态迁移。</para>
    /// </summary>
    /// <param name="command">The HDC command carried by the envelope.<para>信封携带的 HDC 命令。</para></param>
    /// <param name="payload">The payload bytes sent with the command.<para>随命令一起发送的负载字节。</para></param>
    /// <exception cref="IOException">The channel is closed. <para>通道已关闭。</para></exception>
    public void SendCommand(HdcCommand command, byte[] payload)
    {
        if (IsClosed) throw new IOException("HDC channel is closed.");
        _connection.SendOnChannel(this, command, payload);
    }

    /// <summary>
    /// Reads the next chunk, blocking until data arrives or stream closes.
    /// <para>读取下一块数据，阻塞直至有数据到达或流关闭。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds.<para>超时（毫秒）。</para></param>
    /// <returns>The data chunk, or <c>null</c> if the stream is closed.<para>数据块；流关闭时返回 null。</para></returns>
    public byte[]? Read(int timeoutMs = Timeout.Infinite) => ReadMessage(timeoutMs)?.Payload;

    /// <summary>
    /// Reads a complete command envelope without discarding native task metadata.
    /// <para>读取完整的命令信封，不丢弃原生任务元数据。</para>
    /// </summary>
    /// <param name="timeoutMs">Maximum milliseconds to wait for the next envelope; <see cref="Timeout.Infinite"/> waits indefinitely.
    /// <para>等待下一个信封的最大毫秒数；<see cref="Timeout.Infinite"/> 表示无限等待。</para></param>
    /// <returns>The next queued envelope, or <see langword="null"/> when the wait times out or the stream is closed with an empty queue.
    /// <para>下一个入队的信封；等待超时或流已关闭且队列为空时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The stream is faulted; the stored fault is attached as the inner exception.
    /// <para>流处于故障状态；已存储的故障作为内部异常附加。</para></exception>
    public HdcMessage? ReadMessage(int timeoutMs = Timeout.Infinite)
    {
        while (true)
        {
            lock (_lock)
            {
                if (IsFaulted) throw new IOException("HDC channel failed.", Fault);
                if (_incoming.Count > 0)
                {
                    var payload = _incoming.Dequeue();
                    _queuedBytes -= payload.Payload?.Length ?? 0;
                    return payload;
                }

                if (_closed)
                {
                    return null;
                }
            }

            if (!_dataAvailable.Wait(timeoutMs))
            {
                return null;
            }

            _dataAvailable.Reset();
        }
    }

    /// <summary>
    /// Closes the stream locally and notifies the peer.
    /// <para>关闭流并通知对端。</para>
    /// </summary>
    public void Close()
    {
        bool shouldNotify;
        lock (_lock)
        {
            if (_released) return;
            _released = true;
            shouldNotify = !_closed || IsFaulted;
            _closed = true;
        }
        try { _connection.CloseChannel(this, shouldNotify); }
        catch { }

        _dataAvailable.Set();
    }

    /// <summary>
    /// Releases all resources used by the stream.
    /// <para>释放流使用的所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        Close();
        lock (_lock) { _incoming.Clear(); _queuedBytes = 0; }
        GC.SuppressFinalize(this);
    }
}