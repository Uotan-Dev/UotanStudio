using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.ExceptionServices;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Transports.Hsuart;

/// <summary>Incrementally separates raw ACK/NAK bytes and zero-delimited COBS frames.</summary>
public sealed class HsuartFrameParser
{
    private const int MaxAcknowledgementPrefixLength = 16;
    private const int MaxEncodedPacketSize = HsuartPacket.MaxPacketSize + 16;

    // Contiguous bounded buffer: unconsumed bytes are retained across Feed calls without per-byte
    // List<byte> adds or a ToArray() copy per candidate. Only complete frames are copied out, and
    // 0 delimiters are located with span scans instead of a per-byte Add check on the hot path.
    private readonly byte[] _buffer = new byte[MaxEncodedPacketSize];
    private int _start;
    private int _end;

    /// <summary>Gets whether an incomplete COBS frame is buffered.</summary>
    public bool HasPending => _end != _start;

    /// <summary>Consumes an arbitrary serial chunk and returns all complete frames.</summary>
    /// <param name="data">Arbitrary bytes read from the serial stream.</param>
    /// <param name="acceptRawAcknowledgements">Whether raw ACK/NAK bytes are expected in the current protocol state.</param>
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data, bool acceptRawAcknowledgements = false)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            // Take only as much as fits within the maximum pending window; delimiters inside the
            // chunk free room again during the scan below.
            var take = Math.Min(MaxEncodedPacketSize - (_end - _start), data.Length - offset);
            if (take <= 0)
            {
                ClearBuffer();
                throw new InvalidDataException("HSUART frame exceeds the maximum encoded packet size.");
            }
            EnsureRoom(take);
            data.Slice(offset, take).CopyTo(_buffer.AsSpan(_end));
            _end += take;
            offset += take;

            if (!ConsumeCompleteFrames(frames, acceptRawAcknowledgements) && offset < data.Length)
            {
                // The window filled to capacity with no delimiter while more data remains.
                ClearBuffer();
                throw new InvalidDataException("HSUART frame exceeds the maximum encoded packet size.");
            }
        }

        // Raw interpretation is enabled only by the ACK-owned receive path. Once that same bulk
        // read has established a complete framed boundary, a trailing single ACK is unambiguous and
        // can be queued as coalesced control data. Never apply this to a leading 0x06: that remains a
        // legal first COBS code until a complete frame proves otherwise.
        if (acceptRawAcknowledgements && frames.Count > 0 &&
            _end - _start == 1 && IsAcknowledgement(_buffer[_start]))
        {
            frames.Add([_buffer[_start]]);
            _start = 0;
            _end = 0;
        }
        else if (acceptRawAcknowledgements && _end - _start > 1)
        {
            var pending = _buffer.AsSpan(_start, _end - _start);
            if (IsAcknowledgement(pending[0]) && !CouldBeFrame(pending) && CouldBeFrame(pending[1..]))
            {
                frames.Add([pending[0]]);
                _start++;
                if (_start == _end)
                {
                    _start = 0;
                    _end = 0;
                }
            }
        }

        return frames;
    }

    /// <summary>Gets whether the buffered data consists solely of an ambiguous raw ACK/NAK candidate.</summary>
    public bool HasIsolatedAcknowledgementCandidate =>
        _end - _start == 1 && IsAcknowledgement(_buffer[_start]);

    /// <summary>
    /// Explicitly consumes a raw ACK/NAK after the caller has established an out-of-band packet
    /// boundary. A serial read boundary or a short inter-byte delay is not such a boundary: 0x06
    /// is also a legal first COBS code, so the stream reader deliberately never calls this method.
    /// </summary>
    public byte[]? TakeIsolatedAcknowledgement()
    {
        if (_end - _start == 1 && IsAcknowledgement(_buffer[_start]))
        {
            var acknowledgement = new[] { _buffer[_start] };
            _start = 0;
            _end = 0;
            return acknowledgement;
        }
        return null;
    }

    /// <summary>Clears a partial frame when starting a new connection.</summary>
    public void Reset()
    {
        _start = 0;
        _end = 0;
    }

    private bool ConsumeCompleteFrames(List<byte[]> frames, bool acceptRawAcknowledgements)
    {
        var consumedAny = false;
        while (_start < _end)
        {
            var zeroIndex = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)0);
            if (zeroIndex < 0)
            {
                break;
            }
            consumedAny = true;
            EmitCompleteCandidate(frames, zeroIndex, acceptRawAcknowledgements);
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }
        }
        return consumedAny;
    }

    private void EmitCompleteCandidate(List<byte[]> frames, int length, bool acceptRawAcknowledgements)
    {
        // The candidate includes its trailing 0 delimiter; length is the number of bytes before it.
        var candidate = _buffer.AsSpan(_start, length + 1);
        _start += length + 1;

        if (acceptRawAcknowledgements && !IsValidFrame(candidate))
        {
            var prefixLength = 0;
            var maxPrefixLength = Math.Min(length, MaxAcknowledgementPrefixLength);
            while (prefixLength < maxPrefixLength && IsAcknowledgement(candidate[prefixLength]))
            {
                prefixLength++;
            }

            // Usually the sole plausible split is after the contiguous ACK/NAK prefix. A COBS
            // frame may itself start with 0x06, however, so permit that one ambiguous split too.
            var split = prefixLength > 0 && IsValidFrame(candidate[prefixLength..])
                ? prefixLength
                : prefixLength > 1 && IsValidFrame(candidate[(prefixLength - 1)..])
                    ? prefixLength - 1
                    : 0;
            if (split > 0)
            {
                for (var i = 0; i < split; i++)
                {
                    frames.Add([candidate[i]]);
                }
                frames.Add(candidate[split..].ToArray());
                return;
            }
        }
        frames.Add(candidate.ToArray());
    }

    private void EnsureRoom(int needed)
    {
        if (_buffer.Length - _end >= needed)
        {
            return;
        }
        // Compact the pending window to the front of the pooled buffer. Because the pending window
        // is bounded by MaxEncodedPacketSize and the rented buffer is at least that large, this
        // compaction always frees enough room whenever needed is positive.
        if (_start > 0)
        {
            if (_end > _start)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            }
            _end -= _start;
            _start = 0;
        }
    }

    private void ClearBuffer()
    {
        _start = 0;
        _end = 0;
    }

    private static bool IsAcknowledgement(byte value) =>
        value is (byte)HsuartPacketId.AckPacket or (byte)HsuartPacketId.NakPacket;

    private static bool CouldBeFrame(ReadOnlySpan<byte> candidate)
    {
        if (candidate.IsEmpty || candidate[0] == 0)
        {
            return false;
        }

        // The first decoded byte is the packet id. For a non-empty first COBS block it is the
        // second encoded byte, so reject prefixes that already identify an impossible packet.
        return candidate.Length == 1 || IsPacketId(candidate[1]);
    }

    private static bool IsPacketId(byte value) => value is
        (byte)HsuartPacketId.ProtocolPacket or (byte)HsuartPacketId.EndOfTransferPacket or
        (byte)HsuartPacketId.VersionPacket or (byte)HsuartPacketId.ReadyToReadPacket;

    private static bool IsValidFrame(ReadOnlySpan<byte> candidate)
    {
        var decoded = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize);
        try
        {
            HsuartPacket.Decode(candidate, out _, decoded, out var error);
            return error == HsuartPacketError.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decoded);
        }
    }
}

/// <summary>
/// An <see cref="ICommTransport"/> implementation for the HSUART protocol: a serial link
/// wrapped in COBS packet framing with CRC-16, version negotiation, and ACK/NAK semantics.
/// <para>HSUART 协议的 <see cref="ICommTransport"/> 实现：以 COBS 数据包封装（含 CRC-16、
/// 版本协商、ACK/NAK 语义）包裹串行链路。</para>
/// Every payload write is framed as a protocol packet and acknowledged by the device before
/// returning; every read returns the payload of the next received protocol packet.
/// <para>每次负载写入都会被封装为协议数据包并在返回前等待设备确认；每次读取返回下一个
/// 收到的协议数据包的负载。</para>
/// </summary>
/// <remarks>
/// Initializes a new <see cref="HsuartTransport"/> with the specified serial port parameters.
/// <para>使用指定的串口参数初始化 <see cref="HsuartTransport"/> 的新实例。</para>
/// </remarks>
public sealed class HsuartTransport : ICommTransport
{
    private bool _disposed;
    private int _disposeStarted;
    private int _disconnectRequests;
    private int _disconnectGeneration;
    private bool _versionNegotiated;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    // Retained for short lifecycle state transitions only; serial reads and writes have independent
    // gates so a blocking public read cannot monopolize the whole session.
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _sessionCtsLock = new();
    private CancellationTokenSource _sessionCts = new();
    private readonly HsuartFrameParser _frameParser = new();
    private readonly Queue<byte[]> _receivedFrames = [];
    private readonly Queue<byte[]> _receivedPayloads = [];
    private byte[]? _pendingPayload;
    private int _pendingPayloadOffset;

    private const int MaxRetries = 3;
    private const int VersionNegotiationTimeoutMs = 5000;
    // Raw ACK and a COBS frame beginning with code 0x06 are information-theoretically ambiguous on
    // a byte stream. Therefore raw ACK acceptance is explicit protocol configuration, never a timing
    // guess. In this mode the stop-and-wait ACK path performs exactly one one-byte underlying read;
    // that read boundary is the configured protocol boundary and is valid only while ACK is expected.
    private readonly bool _rawAcknowledgementMode;

    /// <summary>Creates a transport using the protocol-compatible raw ACK mode.</summary>
    public HsuartTransport(
        string portName,
        int baudRate = 3000000,
        Parity parity = Parity.None,
        int dataBits = 8,
        StopBits stopBits = StopBits.One,
        Handshake handshake = Handshake.None)
        : this(portName, baudRate, parity, dataBits, stopBits, handshake, true)
    {
    }

    /// <summary>Creates a transport with explicitly configured raw ACK handling.</summary>
    public HsuartTransport(
        string portName,
        int baudRate,
        Parity parity,
        int dataBits,
        StopBits stopBits,
        Handshake handshake,
        bool rawAcknowledgementMode)
    {
        Serial = new SerialTransport(portName, baudRate, parity, dataBits, stopBits, handshake);
        _rawAcknowledgementMode = rawAcknowledgementMode;
    }

    /// <summary>
    /// Gets a value indicating whether the underlying serial transport is connected.
    /// <para>获取底层串口传输是否已连接。</para>
    /// </summary>
    public bool IsConnected => Serial.IsConnected && Volatile.Read(ref _versionNegotiated) &&
        Volatile.Read(ref _disconnectRequests) == 0 && Volatile.Read(ref _disposeStarted) == 0;

    /// <summary>
    /// Gets or sets the default timeout in milliseconds for read and write operations.
    /// <para>获取或设置读写操作的默认超时时间（毫秒）。</para>
    /// </summary>
    public int DefaultTimeoutMs
    {
        get => Serial.DefaultTimeoutMs;
        set => Serial.DefaultTimeoutMs = value;
    }

    /// <summary>
    /// Gets the underlying serial transport, exposed for callers that need raw serial access.
    /// <para>获取底层串口传输，供需要原始串口访问的调用方使用。</para>
    /// </summary>
    public SerialTransport Serial { get; }

    /// <summary>
    /// Connects to the HSUART device and performs version negotiation.
    /// <para>连接 HSUART 设备并执行版本协商。</para>
    /// </summary>
    /// <exception cref="IOException">Version negotiation was rejected (NAK) or failed to encode. <para>版本协商被拒绝（NAK）或编码失败。</para></exception>
    public void Connect()
    {
        ThrowIfDisposed();
        var disconnectGeneration = Volatile.Read(ref _disconnectGeneration);
        _lifecycleGate.Wait();
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _disconnectRequests) != 0 ||
                disconnectGeneration != Volatile.Read(ref _disconnectGeneration))
            {
                throw new InvalidOperationException("HSUART transport is disconnecting.");
            }
            _writeGate.Wait();
            try
            {
                _readGate.Wait();
                try
                {
                    _sessionGate.Wait();
                    try
                    {
                        ThrowIfDisposed();
                        ClearSessionState();
                        RenewSessionCancellation();
                        Serial.Connect();
                        if (Volatile.Read(ref _disconnectRequests) != 0 ||
                            disconnectGeneration != Volatile.Read(ref _disconnectGeneration))
                        {
                            Serial.Disconnect();
                            throw new InvalidOperationException("HSUART transport is disconnecting.");
                        }
                        try
                        {
                            PerformVersionNegotiation();
                        }
                        catch
                        {
                            ClearSessionState();
                            Serial.Disconnect();
                            throw;
                        }
                    }
                    finally
                    {
                        _sessionGate.Release();
                    }
                }
                finally
                {
                    _readGate.Release();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Asynchronously connects to the HSUART device and performs version negotiation.
    /// <para>异步连接 HSUART 设备并执行版本协商。</para>
    /// </summary>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous connect operation. <para>表示异步连接操作的任务。</para></returns>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var disconnectGeneration = Volatile.Read(ref _disconnectGeneration);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _disconnectRequests) != 0 ||
                disconnectGeneration != Volatile.Read(ref _disconnectGeneration))
            {
                throw new InvalidOperationException("HSUART transport is disconnecting.");
            }
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        ThrowIfDisposed();
                        ClearSessionState();
                        RenewSessionCancellation();
                        await Serial.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        if (Volatile.Read(ref _disconnectRequests) != 0 ||
                            disconnectGeneration != Volatile.Read(ref _disconnectGeneration))
                        {
                            Serial.Disconnect();
                            throw new InvalidOperationException("HSUART transport is disconnecting.");
                        }
                        try
                        {
                            await PerformVersionNegotiationAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            ClearSessionState();
                            Serial.Disconnect();
                            throw;
                        }
                    }
                    finally
                    {
                        _sessionGate.Release();
                    }
                }
                finally
                {
                    _readGate.Release();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Disconnects from the HSUART device.
    /// <para>断开与 HSUART 设备的连接。</para>
    /// </summary>
    public void Disconnect()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _disconnectRequests);
        Interlocked.Increment(ref _disconnectGeneration);
        // Mark disconnecting and interrupt physical I/O before waiting for Connect's lifecycle lock.
        // Otherwise Connect can hold that lock while negotiation waits for serial input forever.
        CancelSession();
        Exception? disconnectError = null;
        try
        {
            try
            {
                Serial.Disconnect();
            }
            catch (Exception ex)
            {
                // Physical teardown may fail, but lifecycle state must still be serialized and cleared.
                disconnectError = ex;
            }

            _lifecycleGate.Wait();
            try
            {
                _writeGate.Wait();
                try
                {
                    _readGate.Wait();
                    try
                    {
                        _sessionGate.Wait();
                        try
                        {
                            ClearSessionState();
                            RenewSessionCancellation();
                        }
                        finally
                        {
                            _sessionGate.Release();
                        }
                    }
                    finally
                    {
                        _readGate.Release();
                    }
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (disconnectError is not null)
            {
                ExceptionDispatchInfo.Capture(disconnectError).Throw();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _disconnectRequests);
        }
    }

    /// <summary>
    /// Reads the payload of the next received HSUART protocol packet. Returns 0 when a
    /// control packet (ACK/NAK/version/end-of-transfer) was received instead, after handling it.
    /// <para>读取下一个收到的 HSUART 协议数据包的负载。若收到的是控制包
    /// （ACK/NAK/版本/传输结束），则在处理该控制包后返回 0。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of payload bytes copied. <para>复制的负载字节数。</para></returns>
    /// <exception cref="TimeoutException">No packet arrived within the timeout. <para>超时时间内无数据包到达。</para></exception>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (length <= 0)
        {
            return 0;
        }

        _readGate.Wait();
        try
        {
            EnsureSessionConnected();
            return ReadPayload(buffer, offset, length, timeoutMs);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// Reads the payload of the next received HSUART protocol packet asynchronously.
    /// <para>异步读取下一个收到的 HSUART 协议数据包的负载。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of payload bytes copied. <para>返回复制的负载字节数的任务。</para></returns>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (length <= 0)
        {
            return 0;
        }

        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSessionConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await ReadPayloadAsync(buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// Frames <paramref name="length"/> bytes as a protocol packet, writes it, and waits for the
    /// device ACK before returning.
    /// <para>将 <paramref name="length"/> 个字节封装为协议数据包并写入，在返回前等待设备 ACK。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    /// <exception cref="IOException">No ACK was received, the target returned NAK, or framing failed. <para>未收到 ACK、目标返回 NAK 或封包失败。</para></exception>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (length <= 0)
        {
            return 0;
        }

        var payload = new byte[length];
        Array.Copy(data, offset, payload, 0, length);

        _writeGate.Wait();
        try
        {
            // ACK waiting owns the sole serial receive path; public reads cannot consume this
            // stop-and-wait response. The write itself remains independently serialized.
            _readGate.Wait();
            try
            {
                _sessionGate.Wait();
                try
                {
                    EnsureSessionConnected();
                    PrepareAcknowledgementBoundary();
                    SendProtocolPacket(payload);
                    WaitForAck(timeoutMs);
                    return length;
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Frames <paramref name="length"/> bytes as a protocol packet, writes it asynchronously,
    /// and waits for the device ACK before completing.
    /// <para>将 <paramref name="length"/> 个字节封装为协议数据包并异步写入，完成后等待设备 ACK。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes written. <para>返回实际写入字节数的任务。</para></returns>
    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (length <= 0)
        {
            return 0;
        }

        var payload = new byte[length];
        Array.Copy(data, offset, payload, 0, length);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureSessionConnectedAsync(cancellationToken).ConfigureAwait(false);
                    PrepareAcknowledgementBoundary();
                    await SendProtocolPacketAsync(payload, cancellationToken).ConfigureAwait(false);
                    await WaitForAckAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                    return length;
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Resets the HSUART transport by clearing the underlying serial buffers.
    /// <para>通过清除底层串口缓冲区来重置 HSUART 传输。</para>
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        EnsureConnected();
        // Keep the same global acquisition order as Write: writer, sole receiver, then the
        // short-lived session state gate. This makes parser reset exclusive with both public reads
        // and ACK waiters without introducing an inverted read/write lock order.
        _writeGate.Wait();
        try
        {
            _readGate.Wait();
            try
            {
                _sessionGate.Wait();
                try
                {
                    EnsureSessionConnected();
                    Serial.Reset();
                    ClearBufferedData();
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Sends an EndOfTransfer packet to signal the end of a data transfer.
    /// <para>发送 EndOfTransfer 数据包以表示数据传输结束。</para>
    /// </summary>
    public void SendEndOfTransfer()
    {
        ThrowIfDisposed();
        EnsureConnected();
        _writeGate.Wait();
        try
        {
            _readGate.Wait();
            try
            {
                _sessionGate.Wait();
                try
                {
                    EnsureSessionConnected();
                    var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
                    var encodedLength = HsuartPacket.Encode(HsuartPacketId.EndOfTransferPacket, [], encodedBuffer, out var error);

                    if (error != HsuartPacketError.Success)
                    {
                        throw new IOException($"Failed to encode HSUART EndOfTransfer packet: {error}");
                    }

                    Serial.Write(encodedBuffer, 0, encodedLength, DefaultTimeoutMs);
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Asynchronously sends an EndOfTransfer packet to signal the end of a data transfer.
    /// <para>异步发送 EndOfTransfer 数据包以表示数据传输结束。</para>
    /// </summary>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous send operation. <para>表示异步发送操作的任务。</para></returns>
    public async Task SendEndOfTransferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureSessionConnectedAsync(cancellationToken).ConfigureAwait(false);
                    var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
                    var encodedLength = HsuartPacket.Encode(HsuartPacketId.EndOfTransferPacket, [], encodedBuffer, out var error);

                    if (error != HsuartPacketError.Success)
                    {
                        throw new IOException($"Failed to encode HSUART EndOfTransfer packet: {error}");
                    }

                    await Serial.WriteAsync(encodedBuffer, 0, encodedLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Returns the raw byte stream of the underlying serial port. COBS framing and ACK handling
    /// are bypassed — use only for diagnostics or raw boot-mode access.
    /// <para>返回底层串口的原始字节流。COBS 封装与 ACK 处理被绕过——仅用于诊断或原始引导模式访问。</para>
    /// </summary>
    /// <returns>A stream over the serial port. <para>串口上的流。</para></returns>
    public Stream GetStream() => Serial.GetStream();

    /// <summary>
    /// Disposes the transport, releasing the underlying serial transport.
    /// <para>释放传输及其底层串口传输。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        // Interrupt an infinite serial read before waiting for the session owner; waiting first can
        // deadlock forever because that owner cannot release the gate until its I/O completes.
        _lifetimeCts.Cancel();
        CancelSession();
        Exception? disposeError = null;
        try
        {
            Serial.Dispose();
        }
        catch (Exception ex)
        {
            disposeError = ex;
        }
        _lifecycleGate.Wait();
        try
        {
            _writeGate.Wait();
            try
            {
                _readGate.Wait();
                try
                {
                    _sessionGate.Wait();
                    try
                    {
                        _disposed = true;
                        _versionNegotiated = false;
                        ClearBufferedData();
                    }
                    finally
                    {
                        _sessionGate.Release();
                    }
                }
                finally
                {
                    _readGate.Release();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
            // Do not dispose the gates or cancellation sources: callers may already be queued on a
            // gate or may be constructing a linked token. They are small managed lifetime resources,
            // and cancellation plus the disposed guard safely completes those waiters.
        }

        if (disposeError is not null)
        {
            ExceptionDispatchInfo.Capture(disposeError).Throw();
        }
    }

    private void CancelSession()
    {
        lock (_sessionCtsLock)
        {
            _sessionCts.Cancel();
        }
    }

    private void RenewSessionCancellation()
    {
        lock (_sessionCtsLock)
        {
            _sessionCts.Dispose();
            _sessionCts = new CancellationTokenSource();
        }
    }

    private CancellationTokenSource CreateSessionLinkedTokenSource(CancellationToken cancellationToken)
    {
        lock (_sessionCtsLock)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCts.Token, _sessionCts.Token);
        }
    }

    private void PerformVersionNegotiation()
    {
        if (_versionNegotiated)
        {
            return;
        }

        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.VersionPacket, [], encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART version packet: {error}");
        }

        Serial.Write(encodedBuffer, 0, encodedLength, DefaultTimeoutMs);

        try
        {
            // Version negotiation is always a complete COBS-framed exchange. Raw ACK mode applies
            // only to the stop-and-wait acknowledgement immediately following a protocol write.
            var response = ReadNegotiationResponse(VersionNegotiationTimeoutMs);
            DecodePacket(response, response.Length, out var packetId);
            if (packetId == HsuartPacketId.VersionPacket)
            {
                SendAck();
            }
            else if (packetId == HsuartPacketId.NakPacket)
            {
                throw new IOException("HSUART version negotiation rejected by device (NAK).");
            }
        }
        catch (TimeoutException)
        {
            // Some devices do not answer the version packet; proceed regardless.
            // 部分设备不回应版本包；无论是否回应都继续。
        }

        _versionNegotiated = true;
    }

    private async Task PerformVersionNegotiationAsync(CancellationToken cancellationToken)
    {
        if (_versionNegotiated)
        {
            return;
        }

        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.VersionPacket, [], encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART version packet: {error}");
        }

        await Serial.WriteAsync(encodedBuffer, 0, encodedLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await ReadNegotiationResponseAsync(VersionNegotiationTimeoutMs, cancellationToken).ConfigureAwait(false);
            DecodePacket(response, response.Length, out var packetId);
            if (packetId == HsuartPacketId.VersionPacket)
            {
                await SendAckAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (packetId == HsuartPacketId.NakPacket)
            {
                throw new IOException("HSUART version negotiation rejected by device (NAK).");
            }
        }
        catch (TimeoutException)
        {
            // Some devices do not answer the version packet; proceed regardless.
            // 部分设备不回应版本包；无论是否回应都继续。
        }

        _versionNegotiated = true;
    }

    private void SendProtocolPacket(byte[] payload)
    {
        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.ProtocolPacket, payload, encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART protocol packet: {error}");
        }

        Serial.Write(encodedBuffer, 0, encodedLength, DefaultTimeoutMs);
    }

    private async Task SendProtocolPacketAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.ProtocolPacket, payload, encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART protocol packet: {error}");
        }

        await Serial.WriteAsync(encodedBuffer, 0, encodedLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private void SendAck()
    {
        var ackBuffer = new byte[1];
        var ackLength = HsuartPacket.Encode(HsuartPacketId.AckPacket, [], ackBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART ACK packet: {error}");
        }

        Serial.Write(ackBuffer, 0, ackLength, DefaultTimeoutMs);
    }

    private async Task SendAckAsync(CancellationToken cancellationToken)
    {
        var ackBuffer = new byte[1];
        var ackLength = HsuartPacket.Encode(HsuartPacketId.AckPacket, [], ackBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART ACK packet: {error}");
        }

        await Serial.WriteAsync(ackBuffer, 0, ackLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private void WaitForAck(int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        var nonAckFrames = 0;
        var performedPostSendRead = false;
        while (nonAckFrames < MaxRetries)
        {
            try
            {
                var remaining = RemainingTimeout(timeoutMs, stopwatch);
                byte[] response;
                if (performedPostSendRead && _receivedFrames.Count > 0)
                {
                    response = _receivedFrames.Dequeue();
                }
                else
                {
                    response = _rawAcknowledgementMode
                        ? ReadAcknowledgementOrFrame(remaining)
                        : ReadFrame(remaining);
                    performedPostSendRead = true;
                }
                var decoded = DecodePacket(response, response.Length, out var packetId);
                if (packetId == HsuartPacketId.AckPacket)
                {
                    return;
                }
                if (packetId == HsuartPacketId.NakPacket)
                {
                    throw new IOException("Target returned NAK for HSUART packet.");
                }
                nonAckFrames++;
                if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
                {
                    _receivedPayloads.Enqueue(decoded);
                }
                else
                {
                    HandleReceivedPacketId(packetId);
                }
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        throw new IOException("No ACK received within the HSUART acknowledgement budget.");
    }

    private async Task WaitForAckAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var nonAckFrames = 0;
        var performedPostSendRead = false;
        while (nonAckFrames < MaxRetries)
        {
            try
            {
                var remaining = RemainingTimeout(timeoutMs, stopwatch);
                byte[] response;
                if (performedPostSendRead && _receivedFrames.Count > 0)
                {
                    response = _receivedFrames.Dequeue();
                }
                else
                {
                    response = _rawAcknowledgementMode
                        ? await ReadAcknowledgementOrFrameAsync(remaining, cancellationToken).ConfigureAwait(false)
                        : await ReadFrameAsync(remaining, cancellationToken).ConfigureAwait(false);
                    performedPostSendRead = true;
                }
                var decoded = DecodePacket(response, response.Length, out var packetId);
                if (packetId == HsuartPacketId.AckPacket)
                {
                    return;
                }
                if (packetId == HsuartPacketId.NakPacket)
                {
                    throw new IOException("Target returned NAK for HSUART packet.");
                }
                nonAckFrames++;
                if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
                {
                    _receivedPayloads.Enqueue(decoded);
                }
                else
                {
                    HandleReceivedPacketId(packetId);
                }
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        throw new IOException("No ACK received within the HSUART acknowledgement budget.");
    }

    private static int RemainingTimeout(int timeoutMs, Stopwatch stopwatch)
    {
        if (timeoutMs == Timeout.Infinite)
        {
            return Timeout.Infinite;
        }
        if (timeoutMs < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        }

        var remaining = timeoutMs - (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        // Zero is a valid one-shot poll; the underlying transport must get the opportunity to perform
        // one nonblocking read before reporting that no data is available.
        return Math.Max(0, remaining);
    }

    private byte[] ReadNegotiationResponse(int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        var first = new byte[1];
        var count = Serial.Read(first, 0, 1, RemainingFrameTimeout(timeoutMs, stopwatch));
        if (count == 0)
        {
            throw new TimeoutException("No HSUART version response arrived.");
        }
        // Negotiation may explicitly permit a raw NAK, but never a raw ACK: 0x06 is a legal COBS
        // code and a successful version response must always reach its frame delimiter.
        if (first[0] == (byte)HsuartPacketId.NakPacket)
        {
            return first;
        }
        foreach (var frame in _frameParser.Feed(first))
        {
            _receivedFrames.Enqueue(frame);
        }
        return ReadFrame(RemainingFrameTimeout(timeoutMs, stopwatch));
    }

    private async Task<byte[]> ReadNegotiationResponseAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var first = new byte[1];
        using var linked = CreateSessionLinkedTokenSource(cancellationToken);
        var count = await Serial.ReadAsync(first, 0, 1, RemainingFrameTimeout(timeoutMs, stopwatch), linked.Token).ConfigureAwait(false);
        if (count == 0)
        {
            throw new TimeoutException("No HSUART version response arrived.");
        }
        if (first[0] == (byte)HsuartPacketId.NakPacket)
        {
            return first;
        }
        foreach (var frame in _frameParser.Feed(first))
        {
            _receivedFrames.Enqueue(frame);
        }
        return await ReadFrameAsync(RemainingFrameTimeout(timeoutMs, stopwatch), cancellationToken).ConfigureAwait(false);
    }

    private byte[] ReadAcknowledgementOrFrame(int timeoutMs)
    {
        // Never inspect queued parser output before performing the ACK-owned read. In particular, an
        // isolated 0x06 buffered by a previous public read is not acknowledgement of this send.
        var stopwatch = Stopwatch.StartNew();
        var raw = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize + 16);
        try
        {
            var count = Serial.Read(raw, 0, raw.Length, RemainingFrameTimeout(timeoutMs, stopwatch));
            if (count == 0)
            {
                throw new TimeoutException("No HSUART acknowledgement arrived.");
            }
            // A sole byte from this post-send read is the configured raw control boundary. For a
            // coalesced read, require the parser to establish frame/control boundaries instead of
            // treating a leading 0x06 (also a valid COBS code) as ACK.
            if (count == 1 && raw[0] is (byte)HsuartPacketId.AckPacket or (byte)HsuartPacketId.NakPacket)
            {
                return [raw[0]];
            }
            foreach (var frame in _frameParser.Feed(raw.AsSpan(0, count), _rawAcknowledgementMode))
            {
                _receivedFrames.Enqueue(frame);
            }
            return ReadFrame(RemainingFrameTimeout(timeoutMs, stopwatch), acceptRawAcknowledgements: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private async Task<byte[]> ReadAcknowledgementOrFrameAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var raw = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize + 16);
        using var linked = CreateSessionLinkedTokenSource(cancellationToken);
        try
        {
            var count = await Serial.ReadAsync(raw, 0, raw.Length, RemainingFrameTimeout(timeoutMs, stopwatch), linked.Token).ConfigureAwait(false);
            if (count == 0)
            {
                throw new TimeoutException("No HSUART acknowledgement arrived.");
            }
            if (count == 1 && raw[0] is (byte)HsuartPacketId.AckPacket or (byte)HsuartPacketId.NakPacket)
            {
                return [raw[0]];
            }
            foreach (var frame in _frameParser.Feed(raw.AsSpan(0, count), _rawAcknowledgementMode))
            {
                _receivedFrames.Enqueue(frame);
            }
            return await ReadFrameAsync(RemainingFrameTimeout(timeoutMs, stopwatch), cancellationToken, acceptRawAcknowledgements: true).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private byte[] ReadFrame(int timeoutMs, bool acceptRawAcknowledgements = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var raw = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize + 16);
        try
        {
            while (_receivedFrames.Count == 0)
            {
                var count = Serial.Read(raw, 0, raw.Length, RemainingFrameTimeout(timeoutMs, stopwatch));
                if (count == 0)
                {
                    throw new TimeoutException("No complete HSUART frame arrived.");
                }
                foreach (var frame in _frameParser.Feed(raw.AsSpan(0, count), acceptRawAcknowledgements))
                {
                    _receivedFrames.Enqueue(frame);
                }
            }
            return _receivedFrames.Dequeue();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private async Task<byte[]> ReadFrameAsync(int timeoutMs, CancellationToken cancellationToken, bool acceptRawAcknowledgements = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var raw = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize + 16);
        using var linked = CreateSessionLinkedTokenSource(cancellationToken);
        try
        {
            while (_receivedFrames.Count == 0)
            {
                var count = await Serial.ReadAsync(raw, 0, raw.Length, RemainingFrameTimeout(timeoutMs, stopwatch), linked.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new TimeoutException("No complete HSUART frame arrived.");
                }
                foreach (var frame in _frameParser.Feed(raw.AsSpan(0, count), acceptRawAcknowledgements))
                {
                    _receivedFrames.Enqueue(frame);
                }
            }
            return _receivedFrames.Dequeue();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static int RemainingFrameTimeout(int timeoutMs, Stopwatch stopwatch)
    {
        if (timeoutMs == Timeout.Infinite)
        {
            return Timeout.Infinite;
        }
        if (timeoutMs < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        }

        var remaining = timeoutMs - (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        return Math.Max(0, remaining);
    }

    private int CopyPayload(byte[] buffer, int offset, int length)
    {
        _pendingPayload ??= _receivedPayloads.Count > 0 ? _receivedPayloads.Dequeue() : null;
        if (_pendingPayload is null)
        {
            return 0;
        }
        var count = Math.Min(length, _pendingPayload.Length - _pendingPayloadOffset);
        Array.Copy(_pendingPayload, _pendingPayloadOffset, buffer, offset, count);
        _pendingPayloadOffset += count;
        if (_pendingPayloadOffset == _pendingPayload.Length)
        {
            _pendingPayload = null;
            _pendingPayloadOffset = 0;
        }
        return count;
    }

    private int ReadPayload(byte[] buffer, int offset, int length, int timeoutMs)
    {
        var copied = CopyPayload(buffer, offset, length);
        if (copied > 0)
        {
            return copied;
        }

        var frame = ReadFrame(timeoutMs);
        var decoded = DecodePacket(frame, frame.Length, out var packetId);
        HandleReceivedPacketId(packetId);
        if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
        {
            _receivedPayloads.Enqueue(decoded);
            return CopyPayload(buffer, offset, length);
        }
        return 0;
    }

    private async Task<int> ReadPayloadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        var copied = CopyPayload(buffer, offset, length);
        if (copied > 0)
        {
            return copied;
        }

        var frame = await ReadFrameAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        var decoded = DecodePacket(frame, frame.Length, out var packetId);
        if (packetId is HsuartPacketId.VersionPacket or HsuartPacketId.EndOfTransferPacket)
        {
            await SendAckAsync(cancellationToken).ConfigureAwait(false);
        }
        if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
        {
            _receivedPayloads.Enqueue(decoded);
            return CopyPayload(buffer, offset, length);
        }
        return 0;
    }

    private static byte[] DecodePacket(byte[] buffer, int length, out HsuartPacketId packetId)
    {
        if (length <= 0)
        {
            packetId = 0;
            return [];
        }

        if (length == 1)
        {
            var singleByte = buffer[0];
            if (singleByte == (byte)HsuartPacketId.AckPacket)
            {
                packetId = HsuartPacketId.AckPacket;
                return [];
            }
            if (singleByte == (byte)HsuartPacketId.NakPacket)
            {
                packetId = HsuartPacketId.NakPacket;
                return [];
            }
        }

        var dstBuffer = new byte[HsuartPacket.MaxPacketSize];
        var decodedLength = HsuartPacket.Decode(buffer.AsSpan(0, length), out packetId, dstBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"HSUART packet decode error: {error}");
        }

        if (decodedLength == 0)
        {
            return [];
        }

        var result = new byte[decodedLength];
        Array.Copy(dstBuffer, result, decodedLength);
        return result;
    }

    private void HandleReceivedPacketId(HsuartPacketId packetId)
    {
        switch (packetId)
        {
            case HsuartPacketId.VersionPacket:
            case HsuartPacketId.EndOfTransferPacket:
                SendAck();
                break;
            case HsuartPacketId.NakPacket:
            case HsuartPacketId.ReadyToReadPacket:
                break;
            case HsuartPacketId.ProtocolPacket:
                break;
            case HsuartPacketId.AckPacket:
                break;
            default:
                break;
        }
    }

    private void PrepareAcknowledgementBoundary()
    {
        // ACKs already queued before this write belong to an earlier generation. Keep data/control
        // frames in order, but discard stale acknowledgements; preserve any partial COBS bytes.
        var retained = new Queue<byte[]>();
        while (_receivedFrames.Count > 0)
        {
            var frame = _receivedFrames.Dequeue();
            if (frame.Length != 1 || frame[0] is not ((byte)HsuartPacketId.AckPacket) and not ((byte)HsuartPacketId.NakPacket))
            {
                retained.Enqueue(frame);
            }
        }
        while (retained.Count > 0)
        {
            _receivedFrames.Enqueue(retained.Dequeue());
        }

        // A raw candidate isolated by a pre-send physical read is stale. This consumes only that
        // single ambiguous control candidate and never clears a legitimate multi-byte partial COBS frame.
        _frameParser.TakeIsolatedAcknowledgement();
    }

    private void ClearSessionState()
    {
        _versionNegotiated = false;
        ClearBufferedData();
    }

    private void ClearBufferedData()
    {
        _frameParser.Reset();
        _receivedFrames.Clear();
        _receivedPayloads.Clear();
        _pendingPayload = null;
        _pendingPayloadOffset = 0;
    }

    private void EnsureSessionConnected()
    {
        EnsureConnected();
        PerformVersionNegotiation();
    }

    private async Task EnsureSessionConnectedAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await PerformVersionNegotiationAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!Serial.IsConnected)
        {
            throw new InvalidOperationException("HSUART transport is not connected.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0 || Volatile.Read(ref _disposed))
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }
}