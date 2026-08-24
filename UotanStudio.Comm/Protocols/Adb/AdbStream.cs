namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// A single ADB service stream implementing the OPEN / OKAY / WRTE / CLSE state machine.
/// <para>单条 ADB 服务流，实现 OPEN / OKAY / WRTE / CLSE 状态机。</para>
/// </summary>
public sealed class AdbStream : IDisposable
{
    private readonly AdbConnection _connection;
    private readonly Lock _lock = new();
    private readonly Queue<byte[]> _incoming = new();
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _closed;

    /// <summary>
    /// Gets the local stream identifier.
    /// <para>获取本地流标识。</para>
    /// </summary>
    public uint LocalId { get; }

    /// <summary>
    /// Gets the remote stream identifier, or 0 until peer acknowledges.
    /// <para>获取远端流标识，对端确认前为 0。</para>
    /// </summary>
    public uint RemoteId { get; internal set; }

    /// <summary>
    /// Sets the remote id and signals readiness.
    /// <para>设置远端 ID 并触发就绪信号。</para>
    /// </summary>
    /// <param name="remoteId">The remote stream identifier. 远端流标识。</param>
    internal void SetRemoteId(uint remoteId) { RemoteId = remoteId; _ready.Set(); }

    /// <summary>
    /// Blocks until the stream is ready (OKAY received).
    /// <para>阻塞至流就绪（收到 OKAY）。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns><c>true</c> if the stream became ready; otherwise, <c>false</c>. 流是否就绪。</returns>
    internal bool WaitReady(int timeoutMs) => _ready.Wait(timeoutMs);

    /// <summary>
    /// Gets whether the stream is closed.
    /// <para>获取流是否已关闭。</para>
    /// </summary>
    public bool IsClosed { get { lock (_lock) { return _closed; } } }

    /// <summary>
    /// Gets whether the stream is in a faulted state.
    /// <para>获取流是否处于故障状态。</para>
    /// </summary>
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
    public Exception? Fault
    {
        get { lock (_lock) { return field; } }

        private set;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AdbStream"/> class.
    /// <para>初始化 <see cref="AdbStream"/> 类的新实例。</para>
    /// </summary>
    /// <param name="connection">The ADB connection. ADB 连接。</param>
    /// <param name="localId">The local stream identifier. 本地流标识。</param>
    internal AdbStream(AdbConnection connection, uint localId)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        LocalId = localId;
    }

    /// <summary>
    /// Enqueues incoming data from the peer.
    /// <para>将来自对端的数据入队。</para>
    /// </summary>
    /// <param name="payload">The data payload. 数据负载。</param>
    internal void EnqueueData(byte[] payload)
    {
        lock (_lock) { if (_closed) { return; } _incoming.Enqueue(payload); }
        _dataAvailable.Set();
    }

    /// <summary>
    /// Marks the stream as closed and releases waiting threads.
    /// <para>将流标记为已关闭并释放等待线程。</para>
    /// </summary>
    internal void MarkClosed() { lock (_lock) { _closed = true; } _ready.Set(); _dataAvailable.Set(); }

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
        }
        _ready.Set();
        _dataAvailable.Set();
    }

    /// <summary>
    /// Writes data to the peer.
    /// <para>向对端写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write. 要写入的数据。</param>
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
    /// Reads the next chunk, blocking until data arrives or stream closes.
    /// <para>读取下一块数据，阻塞直至有数据到达或流关闭。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns>The data chunk, or <c>null</c> if the stream is closed. 数据块，流关闭时返回 null。</returns>
    public byte[]? Read(int timeoutMs = Timeout.Infinite)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_incoming.Count > 0)
                {
                    return _incoming.Dequeue();
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
        lock (_lock) { shouldNotify = !_closed; _closed = true; }
        if (shouldNotify) { try { _connection.CloseStream(this); } catch { } }
        _dataAvailable.Set();
    }

    /// <summary>
    /// Releases all resources used by the stream.
    /// <para>释放流使用的所有资源。</para>
    /// </summary>
    public void Dispose() { Close(); _dataAvailable.Dispose(); GC.SuppressFinalize(this); }
}