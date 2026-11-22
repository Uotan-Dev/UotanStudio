using System.Buffers.Binary;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Hash.Crc16Xmodem;
using UotanStudio.Hash.Sums;

namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// Byte-exact builders and parsers of the BSL HDLC frames: <c>[7E][type BE16][len BE16][payload][checksum BE16][7E]</c>,
/// the escape transcoding (<c>7E→7D 5E</c>, <c>7D→7D 5D</c>), the baud probe of repeated header bytes, and the
/// per-stage checksum algorithms (CRC-16/XMODEM on the Boot ROM, the additive ones-complement checksum on FDL1/FDL2).
/// <para>BSL HDLC 帧的逐字节构造与解析：<c>[7E][类型 BE16][长度 BE16][负载][校验 BE16][7E]</c>、
/// 转义变换（<c>7E→7D 5E</c>、<c>7D→7D 5D</c>）、重复帧头字节组成的波特率探测，以及分阶段校验算法
/// （Boot ROM 用 CRC-16/XMODEM，FDL1/FDL2 用加性一补数校验和）。</para>
/// </summary>
internal static class UnisocFrames
{
    /// <summary>
    /// Encodes the baud probe as repeated header bytes, which both stages answer with a version frame. The vendor
    /// tools send exactly one header byte, so callers pass <c>1</c>; the count stays a parameter so a probe budget
    /// experiment does not need a second builder.
    /// <para>编码波特率探测为重复帧头字节，两个阶段都会以版本帧应答。厂商工具只发送一个帧头字节，因此调用方传
    /// <c>1</c>；数量保留为参数，使探测预算的调整不必再写第二个构造器。</para>
    /// </summary>
    /// <param name="count">Number of header bytes to send; must be positive, and the vendor tools use <c>1</c>.
    /// <para>发送的帧头字节数；必须为正，厂商工具使用 <c>1</c>。</para></param>
    /// <returns>The probe bytes. <para>探测字节。</para></returns>
    public static byte[] EncodeCheckBaud(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var probe = new byte[count];
        Array.Fill(probe, UnisocConstants.HeaderByte);
        return probe;
    }

    /// <summary>
    /// Encodes one command frame. <para>编码一条命令帧。</para>
    /// </summary>
    /// <param name="command">Command number. <para>命令号。</para></param>
    /// <param name="payload">Payload bytes; must not exceed <see cref="UnisocConstants.MaximumPayload"/>.
    /// <para>负载字节；不得超过 <see cref="UnisocConstants.MaximumPayload"/>。</para></param>
    /// <param name="kind">Checksum algorithm of the current stage. <para>当前阶段的校验算法。</para></param>
    /// <param name="transcode">Whether the payload is escape-transcoded on the wire.
    /// <para>负载在线上是否做转义变换。</para></param>
    /// <returns>The complete encoded frame including both delimiters.
    /// <para>包含两个定界符的完整编码帧。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The payload exceeds the 16-bit length field.
    /// <para>负载超出 16 位长度字段。</para></exception>
    public static byte[] EncodeCommand(UnisocCommand command, ReadOnlySpan<byte> payload, UnisocChecksumKind kind,
        bool transcode)
    {
        if (payload.Length > UnisocConstants.MaximumPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                "A BSL frame payload must fit the 16-bit length field.");
        }

        var raw = new byte[4 + payload.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(raw, (ushort)command);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(raw.AsSpan(4));
        var checksum = kind == UnisocChecksumKind.Crc16
            ? Crc16Xmodem.Compute(raw.AsSpan(0, 4 + payload.Length))
            : SprdChecksum16.Compute(raw.AsSpan(0, 4 + payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(4 + payload.Length), checksum);

        var frame = new byte[2 * (4 + payload.Length + 2) + 2];
        var length = 0;
        frame[length++] = UnisocConstants.HeaderByte;
        if (transcode)
        {
            length = Transcode(raw, frame, length);
        }
        else
        {
            raw.CopyTo(frame, length);
            length += raw.Length;
        }

        frame[length++] = UnisocConstants.HeaderByte;
        return frame[..length];
    }

    /// <summary>
    /// Escapes the payload into the destination buffer. <para>把负载转义后写入目标缓冲区。</para>
    /// </summary>
    /// <param name="source">Raw bytes. <para>原始字节。</para></param>
    /// <param name="destination">Destination buffer sized for the worst case.
    /// <para>按最坏情况分配的目标缓冲区。</para></param>
    /// <param name="offset">Start offset in the destination. <para>目标缓冲区的起始偏移。</para></param>
    /// <returns>The length written. <para>写入的长度。</para></returns>
    private static int Transcode(ReadOnlySpan<byte> source, byte[] destination, int offset)
    {
        var position = offset;
        foreach (var value in source)
        {
            if (value is UnisocConstants.HeaderByte or UnisocConstants.EscapeByte)
            {
                destination[position++] = UnisocConstants.EscapeByte;
                destination[position++] = (byte)(value ^ UnisocConstants.EscapeMask);
            }
            else
            {
                destination[position++] = value;
            }
        }

        return position;
    }
}

/// <summary>
/// One decoded BSL frame. <para>一条解码后的 BSL 帧。</para>
/// </summary>
/// <param name="Type">Frame type number (command or reply). <para>帧类型号（命令或应答）。</para></param>
/// <param name="Payload">The payload bytes. <para>负载字节。</para></param>
internal readonly record struct UnisocFrame(ushort Type, byte[] Payload)
{
    /// <summary>Gets the payload length. <para>获取负载长度。</para></summary>
    public int PayloadLength => Payload.Length;
}

/// <summary>
/// Outcome of one parse attempt over the buffered bytes.
/// <para>对缓冲字节一次解析尝试的结果。</para>
/// </summary>
internal enum UnisocParseStatus
{
    /// <summary>A complete frame was parsed. <para>解析出一条完整帧。</para></summary>
    Frame = 0,

    /// <summary>More bytes are needed. <para>还需要更多字节。</para></summary>
    Incomplete = 1,

    /// <summary>The buffered bytes violate the framing or checksum contract; the link must be faulted.
    /// <para>缓冲字节违反帧定界或校验契约；链路必须判失效。</para></summary>
    Malformed = 2,
}

/// <summary>
/// Streaming HDLC frame parser: bytes are appended as they arrive from the transport and complete frames are
/// extracted one at a time. Bytes before the first delimiter are skipped, the declared length bounds every frame,
/// and checksum verification uses the caller's per-stage algorithm.
/// <para>流式 HDLC 帧解析器：字节随传输到达即追加，完整帧逐条取出。首个定界符之前的字节会被跳过，声明的长度
/// 约束每条帧，校验验证使用调用方给定的分阶段算法。</para>
/// </summary>
internal sealed class UnisocFrameParser
{
    private readonly byte[] _raw = new byte[UnisocConstants.MaximumPayload + 6];
    private byte[] _buffer = new byte[1024];
    private int _length;
    private int _consumed;

    /// <summary>Discards every buffered byte. <para>丢弃所有缓冲字节。</para></summary>
    public void Clear()
    {
        _consumed = 0;
        _length = 0;
    }

    /// <summary>Appends freshly received bytes. <para>追加新收到的字节。</para></summary>
    /// <param name="data">Bytes received. <para>收到的字节。</para></param>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (_consumed == _length)
        {
            _consumed = 0;
            _length = 0;
        }

        if (_length + data.Length > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, _length + data.Length);
            if (newSize > UnisocConstants.MaximumEncodedFrame)
            {
                throw new UnisocProtocolException("The receive buffer outgrew the largest legal BSL frame.");
            }

            Array.Resize(ref _buffer, newSize);
        }

        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    /// <summary>Attempts to parse one complete frame from the buffered bytes.
    /// <para>尝试从缓冲字节解析一条完整帧。</para></summary>
    /// <param name="kind">Checksum algorithm of the current stage. <para>当前阶段的校验算法。</para></param>
    /// <param name="transcode">Whether frames are escape-transcoded. <para>帧是否做转义变换。</para></param>
    /// <param name="frame">The parsed frame when the status is <see cref="UnisocParseStatus.Frame"/>.
    /// <para>状态为 <see cref="UnisocParseStatus.Frame"/> 时解析出的帧。</para></param>
    /// <returns>The parse outcome. <para>解析结果。</para></returns>
    public UnisocParseStatus TryRead(UnisocChecksumKind kind, bool transcode, out UnisocFrame frame)
    {
        frame = default;
        var index = _consumed;
        var start = -1;
        while (index < _length)
        {
            if (_buffer[index] == UnisocConstants.HeaderByte)
            {
                start = index;
                break;
            }

            index++;
        }

        if (start < 0)
        {
            _consumed = _length;
            return UnisocParseStatus.Incomplete;
        }

        _consumed = start;
        return transcode ? TryReadTranscoded(kind, out frame) : TryReadPlain(kind, out frame);
    }

    private UnisocParseStatus TryReadTranscoded(UnisocChecksumKind kind, out UnisocFrame frame)
    {
        frame = default;
        var index = _consumed + 1;
        var raw = _raw;
        var read = 0;
        var escaped = false;
        var declared = 6;
        while (index < _length)
        {
            var value = _buffer[index++];
            if (escaped && value is not ((byte)(UnisocConstants.HeaderByte ^ UnisocConstants.EscapeMask)
                or (byte)(UnisocConstants.EscapeByte ^ UnisocConstants.EscapeMask)))
            {
                return UnisocParseStatus.Malformed;
            }

            if (value == UnisocConstants.HeaderByte)
            {
                if (read == 0)
                {
                    continue;
                }

                if (read < declared)
                {
                    return UnisocParseStatus.Malformed;
                }

                var transcoded = Finish(kind, raw, read, out frame);
                if (transcoded == UnisocParseStatus.Frame)
                {
                    _consumed = index;
                }

                return transcoded;
            }

            if (value == UnisocConstants.EscapeByte)
            {
                escaped = true;
                continue;
            }

            escaped = false;
            if (read >= declared || read >= raw.Length)
            {
                return UnisocParseStatus.Malformed;
            }

            raw[read++] = value;
            if (read == 4)
            {
                declared = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2)) + 6;
                if (declared > raw.Length)
                {
                    return UnisocParseStatus.Malformed;
                }
            }
        }

        return UnisocParseStatus.Incomplete;
    }

    private UnisocParseStatus TryReadPlain(UnisocChecksumKind kind, out UnisocFrame frame)
    {
        frame = default;
        var index = _consumed + 1;
        var raw = _raw;
        var read = 0;
        var declared = 6;
        while (index < _length)
        {
            var value = _buffer[index++];
            if (read == declared)
            {
                if (value != UnisocConstants.HeaderByte)
                {
                    return UnisocParseStatus.Malformed;
                }

                var plain = Finish(kind, raw, read, out frame);
                if (plain == UnisocParseStatus.Frame)
                {
                    _consumed = index;
                }

                return plain;
            }

            raw[read++] = value;
            if (read == 4)
            {
                declared = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2)) + 6;
                if (declared > raw.Length)
                {
                    return UnisocParseStatus.Malformed;
                }
            }
        }

        return UnisocParseStatus.Incomplete;
    }

    private UnisocParseStatus Finish(UnisocChecksumKind kind, byte[] raw, int length, out UnisocFrame frame)
    {
        frame = default;
        if (length < 6)
        {
            return UnisocParseStatus.Malformed;
        }

        var expected = kind == UnisocChecksumKind.Crc16
            ? Crc16Xmodem.Compute(raw.AsSpan(0, length - 2))
            : SprdChecksum16.Compute(raw.AsSpan(0, length - 2));
        if (BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(length - 2)) != expected)
        {
            return UnisocParseStatus.Malformed;
        }

        frame = new UnisocFrame(BinaryPrimitives.ReadUInt16BigEndian(raw),
            raw[4..(length - 2)].ToArray());
        return UnisocParseStatus.Frame;
    }
}

/// <summary>
/// Minimal byte surface the Unisoc sessions need, so the protocol is testable without a device. The session owns
/// the transport and disposes it on fault, so adapters must dispose the underlying port or USB session.
/// <para>紫光展锐会话所需的最小字节面，使协议无需设备即可测试。会话持有传输并在失效时释放它，因此适配器必须
/// 释放底层串口或 USB 会话。</para>
/// </summary>
internal interface IUnisocTransport : IDisposable
{
    /// <summary>Writes one complete buffer. <para>写入一个完整缓冲区。</para></summary>
    /// <param name="data">Bytes to write. <para>待写入的字节。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <param name="timeoutMilliseconds">Transfer timeout, or <see langword="null"/> for the transport default.
    /// <para>传输超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>A task that completes when the buffer is on the wire. <para>缓冲区发出后完成的任务。</para></returns>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken, int? timeoutMilliseconds = null);

    /// <summary>Reads whatever bytes have arrived, up to the buffer size.
    /// <para>读取已到达的字节，最多读满缓冲区。</para></summary>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Read timeout, or <see langword="null"/> for the transport default.
    /// <para>读取超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>Bytes actually received, which may be zero on timeout.
    /// <para>实际收到的字节数；超时后可能为零。</para></returns>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken, int? timeoutMilliseconds = null);

    /// <summary>
    /// Sends the vendor download-mode control the USB gadget needs before the Boot ROM answers; serial transports
    /// have no such request and the default is a no-op.
    /// <para>发送 USB 下载部件在 Boot ROM 应答前需要的厂商下载模式控制传输；串口传输没有该请求，默认为空操作。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the transfer. <para>取消本次传输。</para></param>
    /// <returns>A task that completes when the control transfer has been issued.
    /// <para>控制传输发出后完成的任务。</para></returns>
    Task SendDownloadModeControlAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Adapter that runs the Unisoc sessions over an opened serial port, the transport the vendor download
/// drivers (SPRD U2S Diag, usbser) expose.
/// <para>把紫光展锐会话跑在已打开串口之上的适配器；厂商下载驱动（SPRD U2S Diag、usbser）暴露的就是该形态。</para>
/// </summary>
internal sealed class UnisocSerialTransport(SerialTransport serial) : IUnisocTransport
{
    /// <summary>Writes the buffer and fails when the port accepted only part of it.
    /// <para>写入缓冲区，并在串口只接受部分字节时失败。</para></summary>
    /// <param name="data">Bytes to write. <para>待写入的字节。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <param name="timeoutMilliseconds">Write timeout, or <see langword="null"/> for the transport default.
    /// <para>写入超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <exception cref="IOException">The port transferred fewer bytes than requested.
    /// <para>串口传输的字节少于请求量。</para></exception>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var written = await serial.WriteAsync(data.ToArray(), 0, data.Length,
            timeoutMilliseconds ?? UnisocConstants.DefaultCommandTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (written != data.Length)
        {
            throw new IOException($"The serial port accepted {written} of {data.Length} bytes.");
        }
    }

    /// <summary>Reads whatever the port has buffered. <para>读取串口已缓冲的字节。</para></summary>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Read timeout, or <see langword="null"/> for the transport default.
    /// <para>读取超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>Bytes received, zero on timeout. <para>收到的字节数；超时为零。</para></returns>
    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
        => serial.ReadAsync(buffer.ToArray(), 0, buffer.Length,
            timeoutMilliseconds ?? UnisocConstants.DefaultCommandTimeoutMs, cancellationToken);

    /// <summary>Disposes the underlying serial port. <para>释放底层串口。</para></summary>
    public void Dispose() => serial.Dispose();
}

/// <summary>
/// Adapter that runs the Unisoc sessions over an opened USB device session. The vendor tool also emits a zero
/// length packet after every full 512-byte multiple on 512-byte endpoints; this adapter has no endpoint descriptor
/// access, so it does not emit ZLPs and the USB path stays an explicit opt-in until that quirk is verified on
/// hardware.
/// <para>把紫光展锐会话跑在已打开 USB 设备会话之上的适配器。厂商工具还会在 512 字节端点上于每个整倍数后补发
/// 零长度包；本适配器拿不到端点描述符，因此不发 ZLP，USB 路径在该特例于真机验证前保持显式 opt-in。</para>
/// </summary>
internal sealed class UnisocUsbTransport(IUsbDeviceSession session) : IUnisocTransport
{
    /// <summary>Sends the CDC-class download-mode control (<c>0x21/0x22</c>, value <c>0x601</c>) the vendor tool
    /// issues before every Boot ROM exchange on the USB path.
    /// <para>发送厂商工具在 USB 路径上每次 Boot ROM 交互前发出的 CDC 类下载模式控制（<c>0x21/0x22</c>、值 <c>0x601</c>）。</para></summary>
    /// <param name="cancellationToken">Cancels the transfer. <para>取消本次传输。</para></param>
    public async Task SendDownloadModeControlAsync(CancellationToken cancellationToken)
    {
        var packet = new UsbSetupPacket
        {
            RequestType = 0x21,
            Request = 34,
            Value = 0x601,
            Index = 0,
            Length = 0
        };
        await session.ControlTransferAsync(packet, null, 0, 0, UnisocConstants.DefaultCommandTimeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the buffer, emitting a ZLP after full out-max-packet multiples on 512-byte endpoints.
    /// <para>写入缓冲区；512 字节端点上写满整倍数后补发 ZLP。</para></summary>
    /// <param name="data">Bytes to write. <para>待写入的字节。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <param name="timeoutMilliseconds">Transfer timeout, or <see langword="null"/> for the session default.
    /// <para>传输超时；<see langword="null"/> 表示使用会话默认值。</para></param>
    /// <exception cref="IOException">The backend transferred fewer bytes than requested.
    /// <para>后端传输的字节少于请求量。</para></exception>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var buffer = data.ToArray();
        var written = await session.WriteAsync(buffer, buffer.Length,
            timeoutMilliseconds ?? session.DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (written != buffer.Length)
        {
            throw new IOException($"The USB backend accepted {written} of {buffer.Length} bytes.");
        }
    }

    /// <summary>Reads one packet into the buffer. <para>把一个数据包读入缓冲区。</para></summary>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Read timeout, or <see langword="null"/> for the session default.
    /// <para>读取超时；<see langword="null"/> 表示使用会话默认值。</para></param>
    /// <returns>Bytes actually received. <para>实际收到的字节数。</para></returns>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var destination = new byte[buffer.Length];
        var read = await session.ReadIntoAsync(destination, 0, destination.Length,
            timeoutMilliseconds ?? session.DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
        destination.AsSpan(0, read).CopyTo(buffer.Span);
        return read;
    }

    /// <summary>Disposes the underlying USB device session. <para>释放底层 USB 设备会话。</para></summary>
    public void Dispose() => session.Dispose();
}