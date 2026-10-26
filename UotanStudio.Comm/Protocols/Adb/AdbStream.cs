using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// A single ADB service stream implementing the OPEN / OKAY / WRTE / CLSE state machine.
/// <para>单条 ADB 服务流，实现 OPEN / OKAY / WRTE / CLSE 状态机。</para>
/// </summary>
/// <param name="connection">The owning ADB connection.<para>所属的 ADB 连接。</para></param>
/// <param name="localId">The local stream identifier.<para>本地流标识。</para></param>
/// <param name="receiveBudgetBytes">Maximum queued payload bytes before the stream faults.
/// <para>队列负载达到该字节数后判定流故障。</para></param>
/// <param name="writePermitWaitMs">Milliseconds a write waits for the peer to acknowledge the previous chunk.
/// <para>写入等待对端确认上一块的毫秒数。</para></param>
internal sealed class AdbStream(AdbConnection connection, uint localId,
    int receiveBudgetBytes = AdbStream.ProtocolReceiveBudgetBytes,
    int writePermitWaitMs = AdbStream.DefaultWritePermitWaitMs) : IAdbStream
{
    /// <summary>Receive budget for streams opened by the shell, sync and device-service protocol clients.
    /// <para>shell、sync 与设备服务等协议客户端所开流使用的接收预算。</para></summary>
    internal const int ProtocolReceiveBudgetBytes = 4 * 1024 * 1024;

    /// <summary>Receive budget for raw service streams, which carry sustained producer output.
    /// <para>裸服务流使用的接收预算，用于承载持续产出的数据。</para></summary>
    internal const int RawServiceReceiveBudgetBytes = 16 * 1024 * 1024;

    /// <summary>Upper bound on queued chunks, applied together with the byte budget so many small chunks
    /// cannot escape the byte accounting. <para>与字节预算同时生效的队列条目上限，避免大量小块绕过字节记账。</para></summary>
    internal const int MaxQueuedChunks = 4096;

    /// <summary>Default write-permit wait in milliseconds. <para>默认的写入许可等待毫秒数。</para></summary>
    internal const int DefaultWritePermitWaitMs = 10_000;

    /// <summary>Release ceiling for the data signal: one wake-up per queued chunk plus the terminal signals, so a
    /// saturated release never throws while buffered payloads always have a waiter to wake.
    /// <para>数据信号的释放上限：每个排队块一次唤醒再加上终止信号，使饱和释放永不抛出，同时缓冲负载总能有等待者被唤醒。</para></summary>
    private const int SignalCapacity = MaxQueuedChunks + 8;

    /// <summary>Read buffer size ceiling for adb-server-mediated streams, which never negotiate a payload limit
    /// because the server owns the device link.
    /// <para>经 adb server 转发的流所用读取缓冲区的上限；此类流不协商负载上限，因为设备链路归 server 持有。</para></summary>
    private const int ServerReadChunkBytes = 64 * 1024;

    private readonly AdbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly Lock _lock = new();
    private readonly Queue<byte[]> _incoming = new();
    private readonly SemaphoreSlim _dataAvailable = new(0, SignalCapacity);
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly SemaphoreSlim _writePermit = new(0, 1);
    private readonly SemaphoreSlim _responseSignal = new(0, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly int _receiveBudgetBytes = receiveBudgetBytes;
    private readonly int _writePermitWaitMs = writePermitWaitMs;
    private bool _closed;
    private readonly Stream? _serverStream;
    private readonly byte[]? _serverReadBuffer;
    private readonly int _serverReadBufferSize;
    private int _queuedBytes;
    private bool _serverBufferReturned;
    private bool _disposed;

    /// <summary>
    /// Initializes a server-mediated stream that relays an already established adb-server socket.
    /// <para>初始化转发已建立 adb server socket 的流。</para>
    /// </summary>
    /// <param name="connection">The owning ADB connection.<para>所属的 ADB 连接。</para></param>
    /// <param name="localId">The local stream identifier.<para>本地流标识。</para></param>
    /// <param name="serverStream">The server socket to relay; the stream owns and disposes it.
    /// <para>待转发的 server socket；本流持有并负责释放。</para></param>
    internal AdbStream(AdbConnection connection, uint localId, Stream serverStream)
        : this(connection, localId, RawServiceReceiveBudgetBytes)
    {
        _serverStream = serverStream;
        _serverReadBufferSize = connection.PeerMaxPayload is > 0 and < ServerReadChunkBytes
            ? (int)connection.PeerMaxPayload
            : ServerReadChunkBytes;
        _serverReadBuffer = ArrayPool<byte>.Shared.Rent(_serverReadBufferSize);
        Release(_ready);
    }

    /// <summary>
    /// Gets the local stream identifier.
    /// <para>获取本地流标识。</para>
    /// </summary>
    /// <value>The local id used in stream messages.<para>流消息中使用的本地标识。</para></value>
    public uint LocalId { get; } = localId;

    /// <summary>
    /// Gets the remote stream identifier, or 0 until peer acknowledges.
    /// <para>获取远端流标识，对端确认前为 0。</para>
    /// </summary>
    /// <value>The peer-assigned stream id, or 0 before the OKAY reply.<para>对端分配的流标识，OKAY 应答前为 0。</para></value>
    public uint RemoteId { get; internal set; }

    /// <summary>
    /// Gets whether the stream is closed.
    /// <para>获取流是否已关闭。</para>
    /// </summary>
    /// <value><see langword="true"/> after a local close, a peer close or a fault; a faulted stream keeps its
    /// queued payloads readable.
    /// <para>本地关闭、对端关闭或故障后为 <see langword="true"/>；故障流已入队的负载仍可读完。</para></value>
    public bool IsClosed { get { lock (_lock) { return _closed; } } }

    /// <summary>
    /// Gets whether the stream is in a faulted state.
    /// <para>获取流是否处于故障状态。</para>
    /// </summary>
    /// <value><see langword="true"/> once the connection failed or the receive budget was exceeded.
    /// <para>连接失败或超出接收预算后为 <see langword="true"/>。</para></value>
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
    /// <value>The stored fault, which reads attach to the <see cref="IOException"/> raised once the queue has
    /// drained. <para>已保存的故障；队列读空后抛出的 <see cref="IOException"/> 会将其作为内部异常附加。</para></value>
    public Exception? Fault
    {
        get { lock (_lock) { return field; } }

        private set;
    }

    /// <summary>
    /// Sets the remote id and signals readiness.
    /// <para>设置远端 ID 并触发就绪信号。</para>
    /// </summary>
    /// <param name="remoteId">The remote stream identifier.<para>远端流标识。</para></param>
    internal void SetRemoteId(uint remoteId)
    {
        lock (_lock)
        {
            if (_closed || _disposed) return;
            RemoteId = remoteId;
            ReleaseWritePermit();
        }

        Release(_ready);
        Release(_responseSignal);
    }

    /// <summary>
    /// Releases the outstanding write permit after the peer acknowledged a WRTE without reporting a remote stream id,
    /// which is how a zero-arg0 OKAY advances the write pipeline instead of being dropped.
    /// <para>在对端未上报远端流标识的情况下确认一个 WRTE 后释放在途写入许可；arg0 为零的 OKAY 因此推进写流水线而不是被丢弃。</para>
    /// </summary>
    internal void AcknowledgeWrite()
    {
        lock (_lock)
        {
            if (_closed || _disposed)
            {
                return;
            }

            ReleaseWritePermit();
        }
    }

    /// <summary>
    /// Blocks until the stream is ready (OKAY received).
    /// <para>阻塞至流就绪（收到 OKAY）。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds.<para>超时（毫秒）。</para></param>
    /// <returns><c>true</c> if the stream became ready; otherwise, <c>false</c>.
    /// <para>流就绪时为 <c>true</c>，否则为 <c>false</c>。</para></returns>
    internal bool WaitReady(int timeoutMs) => _ready.Wait(timeoutMs);

    /// <summary>
    /// Waits until the stream is ready (OKAY received) without holding a thread.
    /// <para>异步等待流就绪（收到 OKAY），等待期间不占用线程。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the readiness wait.<para>取消就绪等待。</para></param>
    internal async ValueTask WaitReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Enqueues incoming data from the peer.
    /// <para>将来自对端的数据入队。</para>
    /// </summary>
    /// <param name="payload">The data payload.<para>数据负载。</para></param>
    internal void EnqueueData(byte[] payload)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (_queuedBytes + payload.Length > _receiveBudgetBytes || _incoming.Count >= MaxQueuedChunks)
            {
                IsFaulted = true;
                Fault = new IOException(
                    $"ADB stream receive buffer limit exceeded ({_receiveBudgetBytes} bytes, {MaxQueuedChunks} chunks).");
                _closed = true;
                Release(_dataAvailable);
                return;
            }

            _queuedBytes += payload.Length;
            _incoming.Enqueue(payload);
        }

        Release(_dataAvailable);
    }

    /// <summary>
    /// Marks the stream as closed and releases waiting threads.
    /// <para>将流标记为已关闭并释放等待线程。</para>
    /// </summary>
    internal void MarkClosed()
    {
        _serverStream?.Dispose();
        lock (_lock)
        {
            _closed = true;
            if (!_disposed) ReleaseWritePermit();
        }

        Release(_ready);
        Release(_dataAvailable);
        Release(_responseSignal);
    }

    /// <summary>
    /// Marks the stream as faulted, stores the exception, and releases waiting threads.
    /// <para>将流标记为故障，保存异常并释放等待线程。</para>
    /// </summary>
    /// <param name="ex">The exception that caused the fault.<para>导致故障的异常。</para></param>
    internal void MarkFaulted(Exception ex)
    {
        lock (_lock)
        {
            IsFaulted = true;
            Fault = ex;
            _closed = true;
        }

        Release(_ready);
        Release(_dataAvailable);
        Release(_responseSignal);
        lock (_lock)
        {
            if (!_disposed) ReleaseWritePermit();
        }
    }

    /// <summary>
    /// Drops any buffered response signal so the next wait observes only acknowledgements that
    /// arrive after the caller's final protocol message.
    /// <para>丢弃已缓冲的应答信号，使下一次等待只观察调用方最后一条协议消息之后到达的确认。</para>
    /// </summary>
    internal void ResetResponseSignal()
    {
        while (_responseSignal.Wait(0))
        {
        }
    }

    /// <summary>
    /// Waits for the peer's acknowledgement of the final protocol message: each OKAY carrying a
    /// remote stream id releases the signal once, and a close or fault ends the wait with the stored
    /// failure so direct-connection transfers can observe the daemon's verdict.
    /// <para>等待对端对最后一条协议消息的确认：每条携带远端流标识的 OKAY 触发一次信号，关闭或故障会以
    /// 已存故障结束等待，使直连传输能观察到 daemon 的结论。</para>
    /// </summary>
    /// <param name="timeoutMs">Wait deadline in milliseconds. <para>等待截止时间（毫秒）。</para></param>
    /// <param name="cancellationToken">Cancels the wait. <para>取消等待。</para></param>
    /// <exception cref="TimeoutException">The peer did not acknowledge within the deadline.
    /// <para>对端未在期限内确认。</para></exception>
    /// <exception cref="IOException">The stream faulted or closed before the acknowledgement.
    /// <para>流在确认前故障或关闭。</para></exception>
    internal async ValueTask WaitForResponseSignalAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (IsFaulted)
            {
                throw new IOException("ADB stream failed.", Fault);
            }

            if (IsClosed)
            {
                throw new IOException("ADB stream closed before the peer acknowledged the transfer.");
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                throw new TimeoutException("ADB peer did not acknowledge the transfer.");
            }

            if (await _responseSignal.WaitAsync((int)Math.Min(remaining, 50), cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Writes data to the peer on a caller thread that already owns the stream.
    /// <para>在已持有该流的调用线程上同步向对端写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write.<para>要写入的数据。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The stream is closed. <para>流已关闭。</para></exception>
    /// <exception cref="IOException">The peer advertised no payload limit, or the stream closed while chunks were
    /// still pending. <para>对端未通告负载上限，或仍有待发块时流被关闭。</para></exception>
    /// <exception cref="TimeoutException">The peer did not acknowledge the previous chunk within the write permit
    /// wait. <para>对端未在写入许可等待期限内确认上一块。</para></exception>
    internal void Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (IsClosed)
        {
            throw new InvalidOperationException("Stream is closed.");
        }

        if (_serverStream is not null)
        {
            _writeGate.Wait();
            try
            {
                _serverStream.Write(data);
            }
            finally
            {
                _writeGate.Release();
            }

            return;
        }

        var chunkSize = ChunkSize();
        _writeGate.Wait();
        try
        {
            for (var offset = 0; offset < data.Length; offset += chunkSize)
            {
                if (!_writePermit.Wait(_writePermitWaitMs))
                {
                    throw new TimeoutException("ADB peer did not acknowledge the previous write.");
                }

                if (IsClosed) throw new IOException("ADB stream closed while writing.", Fault);
                _connection.WriteToStream(this, Exact(data.AsMemory(), offset, Math.Min(chunkSize, data.Length - offset)));
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Writes a payload to the peer without holding a thread while the peer acknowledges each chunk.
    /// <para>向对端写入负载，等待每块确认时不占用线程。</para></summary>
    /// <param name="data">Bytes to send, split by the negotiated payload limit.
    /// <para>待发送字节，按协商的负载上限分块。</para></param>
    /// <param name="cancellationToken">Cancels a permit wait without consuming the peer acknowledgement.
    /// <para>取消某次许可等待，且不会吞掉对端确认。</para></param>
    /// <exception cref="InvalidOperationException">The stream is closed.<para>流已关闭。</para></exception>
    /// <exception cref="TimeoutException">The peer did not acknowledge the previous chunk within the write permit wait while <paramref name="cancellationToken"/> stayed unset.
    /// <para>对端未在写入许可等待期限内确认上一块，且 <paramref name="cancellationToken"/> 未触发。</para></exception>
    /// <exception cref="IOException">The peer advertised no payload limit, or the stream closed or faulted while chunks were still pending.
    /// <para>对端未通告负载上限，或仍有待发块时流被关闭或判定故障。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (IsClosed)
        {
            throw new InvalidOperationException("Stream is closed.");
        }

        if (_serverStream is not null)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _serverStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return;
        }

        var chunkSize = ChunkSize();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var offset = 0; offset < data.Length; offset += chunkSize)
            {
                await WaitForWritePermitAsync(cancellationToken).ConfigureAwait(false);
                if (IsClosed) throw new IOException("ADB stream closed while writing.", Fault);
                await _connection
                    .WriteToStreamAsync(this, Exact(data, offset, Math.Min(chunkSize, data.Length - offset)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Reads the next chunk, or <see langword="null"/> once the stream is closed and drained.
    /// <para>读取下一块数据；流已关闭且队列读空时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read wait in milliseconds; <see cref="Timeout.Infinite"/> waits until data
    /// arrives or the stream closes, and <c>0</c> polls.<para>单次读取等待毫秒数；
    /// <see cref="Timeout.Infinite"/> 等待至数据到达或流关闭，<c>0</c> 表示轮询。</para></param>
    /// <returns>The next queued payload, or <see langword="null"/> when the read timed out or the stream closed
    /// with an empty queue. <para>下一个排队负载；读取超时或流已关闭且无排队数据时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The stream is faulted and its queue has drained.
    /// <para>流已故障且队列已读完。</para></exception>
    internal byte[]? Read(int timeoutMs = Timeout.Infinite)
    {
        if (_serverStream is not null)
        {
            return ReadServerStream(timeoutMs);
        }

        while (true)
        {
            if (TryDequeue(out var chunk))
            {
                return chunk;
            }

            if (DrainedState() is not WaitState.Continue)
            {
                return null;
            }

            if (!_dataAvailable.Wait(timeoutMs))
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Reads the next chunk asynchronously, or <see langword="null"/> once the stream is closed and drained.
    /// <para>异步读取下一块数据；流已关闭且队列读空时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="cancellationToken">Bounds the wait for inbound data.<para>限制入站数据的等待。</para></param>
    /// <returns>The next queued payload, or <see langword="null"/> when the stream closed with an empty queue.
    /// <para>下一个排队负载；流关闭且队列为空时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The stream is faulted and its queue has drained.
    /// <para>流已故障且队列已读完。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled while no
    /// data was queued. <para>无排队数据时 <paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_serverStream is not null)
        {
            return await ReadServerStreamAsync(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            if (TryDequeue(out var chunk))
            {
                return chunk;
            }

            if (DrainedState() is not WaitState.Continue)
            {
                return null;
            }

            try
            {
                await _dataAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Dequeues a pending payload without waiting.
    /// <para>非阻塞地取出已排队的负载。</para>
    /// </summary>
    /// <param name="chunk">The queued payload when <see langword="true"/> is returned.
    /// <para>返回 <see langword="true"/> 时的排队负载。</para></param>
    /// <returns><see langword="true"/> when a payload was already queued; otherwise <see langword="false"/>.
    /// <para>队列已有负载时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para></returns>
    public bool TryRead([NotNullWhen(true)] out byte[]? chunk)
    {
        if (_serverStream is not null)
        {
            chunk = ReadServerStream(0);
            return chunk is not null;
        }

        return TryDequeue(out chunk);
    }

    /// <summary>
    /// Closes the stream locally and notifies the peer.
    /// <para>本地关闭流并通知对端。</para>
    /// </summary>
    public void Close()
    {
        bool shouldNotify;
        lock (_lock)
        {
            shouldNotify = !_closed;
            _closed = true;
        }

        _serverStream?.Dispose();
        if (shouldNotify || _serverStream is not null) { try { _connection.CloseStream(this); } catch { } }
        Release(_dataAvailable);
        lock (_lock)
        {
            if (!_disposed) ReleaseWritePermit();
        }
    }

    /// <summary>
    /// Closes the stream and notifies the peer without blocking a thread on the close frame.
    /// <para>关闭流并通知对端，等待关闭帧时不占用线程。</para>
    /// </summary>
    public async Task CloseAsync()
    {
        bool shouldNotify;
        lock (_lock)
        {
            shouldNotify = !_closed;
            _closed = true;
        }

        _serverStream?.Dispose();
        if (shouldNotify || _serverStream is not null)
        {
            try
            {
                await _connection.CloseStreamAsync(this).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        Release(_dataAvailable);
        lock (_lock)
        {
            if (!_disposed) ReleaseWritePermit();
        }
    }

    /// <summary>
    /// Releases the resources held by the stream.
    /// <para>释放流持有的资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _writeGate.Wait();
        try
        {
            lock (_lock)
            {
                _disposed = true;
                _writePermit.Dispose();
                ReturnServerBuffer();
            }
        }
        finally
        {
            _writeGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the stream asynchronously.
    /// <para>异步释放流。</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await CloseAsync().ConfigureAwait(false);
            await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (_lock)
                {
                    _disposed = true;
                    _writePermit.Dispose();
                    ReturnServerBuffer();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Classifies a read that found the queue empty.
    /// <para>判定一次发现队列为空的读取。</para>
    /// </summary>
    /// <returns><see cref="WaitState.Continue"/> when the read should wait for more data;
    /// <see cref="WaitState.Ended"/> when the stream is closed cleanly.
    /// <para>读取应继续等待数据时返回 <see cref="WaitState.Continue"/>；流正常关闭时返回
    /// <see cref="WaitState.Ended"/>。</para></returns>
    /// <exception cref="IOException">The stream is faulted and the queue is empty; the stored fault is the inner
    /// exception. <para>流已故障且队列为空；已保存的故障作为内部异常。</para></exception>
    private WaitState DrainedState()
    {
        lock (_lock)
        {
            if (_incoming.Count > 0) return WaitState.Continue;
            if (IsFaulted) throw new IOException("ADB stream failed.", Fault);
            return _closed ? WaitState.Ended : WaitState.Continue;
        }
    }

    /// <summary>
    /// Releases the single outstanding-write permit, ignoring the extra acknowledgements a peer may send.
    /// <para>释放唯一的在途写入许可，并忽略对端可能多发的确认。</para>
    /// </summary>
    private void ReleaseWritePermit() => Release(_writePermit);

    /// <summary>
    /// Signals one of the stream's counting primitives, ignoring the saturating extra signals that a repeated
    /// acknowledgement or close can produce.
    /// <para>触发该流的某个计数原语一次，并忽略重复确认或重复关闭可能造成的饱和多余信号。</para>
    /// </summary>
    /// <param name="signal">The primitive to signal.<para>待触发的原语。</para></param>
    private static void Release(SemaphoreSlim signal)
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>
    /// Waits for the write permit, converting the internal deadline into <see cref="TimeoutException"/> while
    /// preserving external cancellation and leaving the permit available when the wait ends without one.
    /// <para>等待写入许可；把内部期限转为 <see cref="TimeoutException"/>，同时保留外部取消语义，并在未获得许可时
    /// 保持许可可被后续写入使用。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <exception cref="TimeoutException">The deadline expired while <paramref name="cancellationToken"/> stayed
    /// unset. <para>期限已到而 <paramref name="cancellationToken"/> 未触发。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    private async ValueTask WaitForWritePermitAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_writePermitWaitMs);
        try
        {
            await _writePermit.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("ADB peer did not acknowledge the previous write.");
        }
        catch (ObjectDisposedException)
        {
            throw new IOException("ADB stream closed while writing.", Fault);
        }
    }

    /// <summary>
    /// Resolves the payload size for one WRTE frame from the peer-advertised limit.
    /// <para>按对端通告的上限确定单个 WRTE 帧的负载大小。</para>
    /// </summary>
    /// <returns>The payload size for one WRTE frame.<para>单个 WRTE 帧的负载大小。</para></returns>
    /// <exception cref="IOException">The peer did not advertise a payload limit. <para>对端未通告负载上限。</para></exception>
    private int ChunkSize()
    {
        var maxPayload = _connection.PeerMaxPayload;
        if (maxPayload == 0) throw new IOException("ADB peer did not advertise a valid maximum payload.");
        return (int)Math.Min(maxPayload, AdbConstants.MaxPayload);
    }

    /// <summary>
    /// Takes the next queued payload, clearing the data signal only once the queue has emptied.
    /// <para>取出下一个排队负载，仅在队列已空时清除数据信号。</para>
    /// </summary>
    /// <param name="chunk">The payload taken from the queue.<para>从队列取出的负载。</para></param>
    /// <returns><see langword="true"/> when a payload was queued. <para>队列中存在负载时为
    /// <see langword="true"/>。</para></returns>
    private bool TryDequeue(out byte[]? chunk)
    {
        lock (_lock)
        {
            if (_incoming.Count == 0)
            {
                chunk = null;
                return false;
            }

            chunk = _incoming.Dequeue();
            _queuedBytes -= chunk.Length;
            return true;
        }
    }

    /// <summary>
    /// Reads one chunk from the relayed adb-server socket on a caller thread that already owns the stream.
    /// <para>在已持有该流的调用线程上从转发的 adb server socket 读取一块数据。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read wait in milliseconds.<para>单次读取等待毫秒数。</para></param>
    /// <returns>The chunk read from the relayed socket, or <see langword="null"/> on timeout, close, or an
    /// already closed stream. <para>从转发 socket 读到的块；超时、关闭或流已关闭时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The relayed socket failed. <para>被转发的 socket 失败。</para></exception>
    private byte[]? ReadServerStream(int timeoutMs)
    {
        lock (_lock)
        {
            if (_closed) return null;
            try
            {
                _serverStream!.ReadTimeout = timeoutMs == 0 ? 1 : timeoutMs;
                var buffer = _serverReadBuffer!;
                var count = _serverStream.Read(buffer, 0, _serverReadBufferSize);
                if (count == 0)
                {
                    MarkClosed();
                    return null;
                }

                return buffer.AsSpan(0, count).ToArray();
            }
            catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException socket &&
                socket.SocketErrorCode is System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.WouldBlock)
            {
                return null;
            }
            catch (Exception ex)
            {
                MarkFaulted(ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Reads one chunk from the relayed adb-server socket without blocking a thread.
    /// <para>从转发的 adb server socket 异步读取一块数据，不阻塞线程。</para>
    /// </summary>
    /// <param name="cancellationToken">Bounds the wait for inbound bytes.<para>限制入站字节的等待。</para></param>
    /// <returns>The chunk read from the relayed socket, or <see langword="null"/> when cancelled, closed, or an
    /// already closed stream. <para>从转发 socket 读到的块；取消、关闭或流已关闭时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The relayed socket failed. <para>被转发的 socket 失败。</para></exception>
    private async ValueTask<byte[]?> ReadServerStreamAsync(CancellationToken cancellationToken)
    {
        if (IsClosed) return null;
        var buffer = _serverReadBuffer!;
        int count;
        try
        {
            count = await _serverStream!.ReadAsync(buffer.AsMemory(0, _serverReadBufferSize), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            MarkFaulted(ex);
            throw;
        }

        if (count == 0)
        {
            MarkClosed();
            return null;
        }

        return buffer.AsSpan(0, count).ToArray();
    }

    /// <summary>
    /// Returns the pooled relay buffer, which is handed back only once at disposal.
    /// <para>归还池化的转发缓冲区，仅在释放时归还一次。</para>
    /// </summary>
    private void ReturnServerBuffer()
    {
        if (_serverReadBuffer is null || _serverBufferReturned) return;
        _serverBufferReturned = true;
        ArrayPool<byte>.Shared.Return(_serverReadBuffer);
    }

    /// <summary>
    /// Projects one chunk of the payload buffer. An exact-size array is handed over as is, because a wire
    /// message carries its payload as a whole array whose length is the frame length.
    /// <para>取出负载缓冲区中的一块。尺寸与该块恰好相同的数组会直接转交，因为线上消息以整个数组承载负载，
    /// 而数组长度即为帧长度。</para>
    /// </summary>
    /// <param name="data">The payload buffer.<para>负载缓冲区。</para></param>
    /// <param name="offset">Chunk start.<para>块起始位置。</para></param>
    /// <param name="length">Chunk length.<para>块长度。</para></param>
    /// <returns>The payload array for one frame.<para>单帧的负载数组。</para></returns>
    private static byte[] Exact(ReadOnlyMemory<byte> data, int offset, int length) =>
        MemoryMarshal.TryGetArray(data, out var segment) && offset == 0 && length == segment.Array!.Length
            ? segment.Array
            : data.Slice(offset, length).ToArray();

    /// <summary>Outcome of a read that found the queue empty. <para>队列为空时的读取结果。</para></summary>
    private enum WaitState
    {
        /// <summary>Wait for more data. <para>继续等待数据。</para></summary>
        Continue,

        /// <summary>The stream closed cleanly. <para>流已正常关闭。</para></summary>
        Ended,
    }
}