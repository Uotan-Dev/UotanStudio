using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Transports.Hdlc;

/// <summary>
/// HDLC framing decorator wrapping an underlying <see cref="ICommTransport"/>.
/// <para>HDLC 帧封装装饰器，包裹底层 <see cref="ICommTransport"/>。</para>
/// Sahara spec §2.2 requires Sahara packets to be carried in HDLC frames (opening/closing
/// flags 0x7E, byte-stuffing, CRC-16 FCS). USB endpoints natively frame packets and may
/// bypass this decorator; HSUART/serial links must use it or the device cannot detect
/// frame boundaries.
/// <para>Sahara spec §2.2 要求 Sahara 协议包在 HDLC 帧中传输（起始/结束标志 0x7E、转义、
/// CRC-16 FCS）。USB 端点天然成帧，可跳过本装饰器；HSUART/串行链路必须使用本装饰器，
/// 否则设备无法识别帧边界。</para>
/// </summary>
/// <remarks>
/// Write semantics: wraps the payload into a single HDLC frame (flag+addr+ctrl+info+fcs+flag)
/// and writes it via the underlying transport.
/// <para>写入语义：将 payload 封装为单个 HDLC 帧（flag+addr+ctrl+info+fcs+flag），经底层
/// transport 写出。</para>
/// Read semantics: reads a byte stream from the underlying transport, locates flag boundaries,
/// reverses transparency, verifies the FCS, and returns the info field to the caller.
/// <para>读取语义：从底层 transport 读取字节流，定位 flag 边界，反向转义后校验 FCS，
/// 提取 info 字段返回给上层。</para>
/// </remarks>
/// <remarks>
/// Initializes the decorator with the underlying transport and custom HDLC address/control bytes.
/// <para>使用底层 transport 与自定义 HDLC 地址/控制字节初始化。</para>
/// </remarks>
/// <param name="inner">The underlying transport being decorated. <para>被装饰的底层 transport。</para></param>
/// <param name="address">The HDLC address byte. <para>HDLC 地址字节。</para></param>
/// <param name="control">The HDLC control byte. <para>HDLC 控制字节。</para></param>
public sealed class HdlcFramingTransport(ICommTransport inner, byte address, byte control) : ICommTransport
{
    private readonly byte _address = address;
    private readonly byte _control = control;
    private bool _disposed;

    /// <summary>
    /// Gets or sets the default timeout in milliseconds used when a caller omits one.
    /// <para>获取或设置调用方未显式指定超时时使用的默认超时（毫秒）。</para>
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 5000;

    // Receive-side byte accumulation buffer used to assemble a complete HDLC frame across
    // multiple underlying Read calls.
    // 接收侧字节累加缓冲，用于跨多次底层 Read 调用拼装完整 HDLC 帧。
    private readonly List<byte> _rxBuffer = new(4096);

    /// <summary>
    /// Initializes the decorator with the underlying transport and the default HDLC address
    /// (0xFF) / control (0x03 UI) bytes.
    /// <para>使用底层 transport 与默认 HDLC 地址 (0xFF) / 控制 (0x03 UI) 字节初始化。</para>
    /// </summary>
    /// <param name="inner">The underlying transport being decorated. <para>被装饰的底层 transport。</para></param>
    public HdlcFramingTransport(ICommTransport inner)
        : this(inner, Hsuart.Hdlc.DefaultAddress, Hsuart.Hdlc.DefaultControl)
    {
    }

    /// <summary>
    /// Passes through the underlying transport's connection state.
    /// <para>透传底层 transport 的连接状态。</para>
    /// </summary>
    public bool IsConnected => Inner.IsConnected;

    /// <summary>
    /// Gets the wrapped inner transport.
    /// <para>获取被包装的底层 transport。</para>
    /// </summary>
    public ICommTransport Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>
    /// Connects the underlying transport.
    /// <para>连接底层 transport。</para>
    /// </summary>
    public void Connect() => Inner.Connect();

    /// <summary>
    /// Disconnects the underlying transport.
    /// <para>断开底层 transport。</para>
    /// </summary>
    public void Disconnect() => Inner.Disconnect();

    /// <summary>
    /// Frames the payload into an HDLC frame and writes it via the underlying transport.
    /// <para>将 payload 封装为 HDLC 帧并写入底层 transport。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    public void Write(byte[] data, int offset, int length)
        => Write(data, offset, length, DefaultTimeoutMs);

    /// <summary>
    /// Frames the payload into an HDLC frame and writes it via the underlying transport.
    /// <para>将 payload 封装为 HDLC 帧并写入底层 transport。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        ThrowIfDisposed();
        if (length <= 0)
        {
            return 0;
        }

        WriteFrame(data.AsSpan(offset, length), timeoutMs);
        return length;
    }

    /// <summary>
    /// Frames the payload into an HDLC frame and writes it via the underlying transport asynchronously.
    /// <para>将 payload 封装为 HDLC 帧并异步写入底层 transport。</para>
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
        if (length <= 0)
        {
            return 0;
        }
        var info = data.AsSpan(offset, length).ToArray();
        await WriteFrameAsync(info, timeoutMs, cancellationToken).ConfigureAwait(false);
        return length;
    }

    /// <summary>
    /// Reads one complete HDLC frame's info field from the underlying transport.
    /// <para>从底层 transport 读取一个完整 HDLC 帧的 info 字段。</para>
    /// The caller-supplied buffer length must be ≥ the expected info length. Bytes are
    /// accumulated until a closing flag is detected, then decoded, FCS-verified, and the
    /// info field is extracted.
    /// <para>调用方提供的缓冲区长度需 ≥ 期望 info 字段长度。内部会累加字节直到检测到关闭
    /// flag，再解码、校验 FCS、提取 info。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of info bytes written to the buffer. <para>写入缓冲区的 info 字节数。</para></returns>
    /// <exception cref="TimeoutException">No complete frame arrived within the timeout. <para>超时时间内未收到完整帧。</para></exception>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        ThrowIfDisposed();
        if (length <= 0)
        {
            return 0;
        }

        var dst = buffer.AsSpan(offset, length).ToArray();
        var n = ReadFrame(dst, timeoutMs, CancellationToken.None);
        Array.Copy(dst, 0, buffer, offset, n);
        return n;
    }

    /// <summary>
    /// Reads one complete HDLC frame's info field from the underlying transport asynchronously.
    /// <para>从底层 transport 异步读取一个完整 HDLC 帧的 info 字段。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of info bytes written. <para>返回写入的 info 字节数的任务。</para></returns>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (length <= 0)
        {
            return 0;
        }
        var dst = buffer.AsSpan(offset, length).ToArray();
        var n = await ReadFrameAsync(dst, timeoutMs, cancellationToken).ConfigureAwait(false);
        Array.Copy(dst, 0, buffer, offset, n);
        return n;
    }

    /// <summary>
    /// Resets the decorator by clearing the receive accumulation buffer and resetting the
    /// underlying transport.
    /// <para>通过清除接收累加缓冲并重置底层 transport 来重置装饰器。</para>
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        _rxBuffer.Clear();
        Inner.Reset();
    }

    /// <summary>
    /// Returns the raw byte stream of the underlying transport. HDLC framing is bypassed —
    /// use only for diagnostics or raw boot-mode access.
    /// <para>返回底层 transport 的原始字节流。HDLC 封装被绕过——仅用于诊断或原始引导模式访问。</para>
    /// </summary>
    /// <returns>A stream over the underlying transport. <para>底层 transport 上的流。</para></returns>
    public Stream GetStream() => Inner.GetStream();

    /// <summary>
    /// Disposes the decorator and the underlying transport.
    /// <para>释放装饰器及其底层 transport。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Inner.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HdlcFramingTransport));
        }
    }

    // ============== Write path: HDLC encoding ==============
    // 写入路径：HDLC 编码

    private void WriteFrame(ReadOnlySpan<byte> info, int timeoutMs)
    {
        var frame = new byte[(info.Length * 2) + 8];
        var n = Hsuart.Hdlc.Encode(info, frame, out var err, _address, _control);
        if (n == 0)
        {
            throw new IOException($"HDLC encode failed: {err}");
        }

        Inner.Write(frame, 0, n, timeoutMs);
    }

    private async Task WriteFrameAsync(byte[] info, int timeoutMs, CancellationToken cancellationToken)
    {
        var frame = new byte[(info.Length * 2) + 8];
        var n = Hsuart.Hdlc.Encode(info, frame, out var err, _address, _control);
        if (n == 0)
        {
            throw new IOException($"HDLC encode failed: {err}");
        }

        await Inner.WriteAsync(frame, 0, n, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    // ============== Read path: HDLC decoding ==============
    // 读取路径：HDLC 解码

    private int ReadFrame(byte[] dst, int timeoutMs, CancellationToken cancellationToken) =>
        ReadFrameAsync(dst, timeoutMs, cancellationToken).GetAwaiter().GetResult();

    private async Task<int> ReadFrameAsync(byte[] dst, int timeoutMs, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryExtractFrame(dst, out var infoLen, out var consumed))
            {
                _rxBuffer.RemoveRange(0, consumed);
                return infoLen;
            }

            var chunk = new byte[256];
            int n;
            try
            {
                n = await Inner.ReadAsync(chunk, 0, chunk.Length, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _rxBuffer.Clear();
                throw;
            }
            if (n <= 0)
            {
                _rxBuffer.Clear();
                throw new IOException("Underlying transport returned no data while waiting for HDLC frame.");
            }
            _rxBuffer.AddRange(chunk.AsSpan(0, n).ToArray());
        }
    }

    /// <summary>
    /// Attempts to extract the first complete HDLC frame's info field from <see cref="_rxBuffer"/>.
    /// <para>尝试从 <see cref="_rxBuffer"/> 中提取第一个完整 HDLC 帧的 info 字段。</para>
    /// </summary>
    /// <param name="dst">The destination buffer for the info field. <para>info 字段的目标缓冲区。</para></param>
    /// <param name="infoLen">The extracted info length on success. <para>成功时提取的 info 长度。</para></param>
    /// <param name="consumed">The number of <see cref="_rxBuffer"/> bytes consumed on success. <para>成功时 <see cref="_rxBuffer"/> 中被消费的字节数。</para></param>
    /// <returns>True on success. <para>成功时返回 true。</para></returns>
    private bool TryExtractFrame(Span<byte> dst, out int infoLen, out int consumed)
    {
        infoLen = 0;
        consumed = 0;

        var start = -1;
        for (var i = 0; i < _rxBuffer.Count; i++)
        {
            if (_rxBuffer[i] == Hsuart.Hdlc.Flag)
            {
                start = i;
                break;
            }
        }
        if (start < 0)
        {
            _rxBuffer.Clear();
            return false;
        }

        var end = -1;
        for (var i = start + 1; i < _rxBuffer.Count; i++)
        {
            if (_rxBuffer[i] == Hsuart.Hdlc.Flag)
            {
                end = i;
                break;
            }
        }
        if (end < 0)
        {
            if (start > 0)
            {
                _rxBuffer.RemoveRange(0, start);
            }

            return false;
        }

        var frame = _rxBuffer.GetRange(start, end - start + 1).ToArray();
        var infoBuf = new byte[frame.Length];
        var n = Hsuart.Hdlc.ExtractInfo(frame, infoBuf, out var err);
        if (n == 0)
        {
            throw new IOException($"HDLC decode failed: {err}");
        }

        if (dst.Length < n)
        {
            throw new IOException($"HDLC info length {n} exceeds caller buffer {dst.Length}.");
        }

        infoBuf.AsSpan(0, n).CopyTo(dst);
        infoLen = n;
        consumed = end + 1;
        return true;
    }
}