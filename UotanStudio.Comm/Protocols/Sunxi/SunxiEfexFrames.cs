using System.Buffers.Binary;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// Byte-exact builders and parsers of the eFEX frames: the 32-byte bulk request header that wraps
/// every FEL exchange, the 16-byte inner FEL request, the 20-byte FES transfer header, the 13-byte
/// bulk response, and the fixed-size identity and verification payloads.
/// <para>eFEX 帧的逐字节构造与解析：包裹每次 FEL 交互的 32 字节批量请求头、16 字节内层 FEL 请求、
/// 20 字节 FES 传输头、13 字节批量响应，以及定长的身份与校验负载。</para>
/// </summary>
internal static class SunxiEfexFrames
{
    /// <summary>Bulk request type byte of a host-to-device data stage. <para>主机到设备数据阶段的批量请求类型字节。</para></summary>
    public const byte WriteRequest = 0x12;

    /// <summary>Bulk request type byte of a device-to-host data stage. <para>设备到主机数据阶段的批量请求类型字节。</para></summary>
    public const byte ReadRequest = 0x11;

    /// <summary>
    /// Writes the 32-byte bulk request header. The device reads the payload length from offset 8, the
    /// command-package length from offset 15 and the request type from the first command byte.
    /// <para>写入 32 字节批量请求头。设备从偏移 8 读取负载长度、从偏移 15 读取命令包长度、从命令首字节读取请求类型。</para>
    /// </summary>
    /// <param name="destination">Destination span of at least 32 bytes. <para>至少 32 字节的目标跨度。</para></param>
    /// <param name="request">Request type, <see cref="WriteRequest"/> or <see cref="ReadRequest"/>.
    /// <para>请求类型，取 <see cref="WriteRequest"/> 或 <see cref="ReadRequest"/>。</para></param>
    /// <param name="length">Payload bytes that follow the header. <para>头部之后跟随的负载字节数。</para></param>
    public static void WriteRequestHeader(Span<byte> destination, byte request, int length)
    {
        var header = destination[..SunxiEfexConstants.RequestHeaderSize];
        header.Clear();
        SunxiEfexConstants.RequestMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)length);
        header[15] = SunxiEfexConstants.CommandLength;
        header[16] = request;
    }

    /// <summary>
    /// Writes the 16-byte inner FEL request sent as the payload of a write stage.
    /// <para>写入作为写阶段负载发送的 16 字节内层 FEL 请求。</para>
    /// </summary>
    /// <param name="destination">Destination span of at least 16 bytes. <para>至少 16 字节的目标跨度。</para></param>
    /// <param name="command">FEL command number. <para>FEL 命令号。</para></param>
    /// <param name="address">Target address. <para>目标地址。</para></param>
    /// <param name="length">Transfer length the command applies to. <para>该命令作用的传输长度。</para></param>
    public static void WriteFelRequest(Span<byte> destination, SunxiEfexCommand command, uint address, uint length)
    {
        var request = destination[..SunxiEfexConstants.FelRequestSize];
        request.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(request, (ushort)command);
        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], address);
        BinaryPrimitives.WriteUInt32LittleEndian(request[8..], length);
    }

    /// <summary>
    /// Writes the 20-byte FES transfer header: command, tag, a 12-byte inline payload and the bulk magic.
    /// <para>写入 20 字节 FES 传输头：命令、标签、12 字节内联负载与批量魔数。</para>
    /// </summary>
    /// <param name="destination">Destination span of at least 20 bytes. <para>至少 20 字节的目标跨度。</para></param>
    /// <param name="command">FES command number. <para>FES 命令号。</para></param>
    /// <param name="payload">Inline payload of at most 12 bytes; shorter payloads leave the tail zeroed.
    /// <para>至多 12 字节的内联负载；更短的负载会把尾部保留为 0。</para></param>
    /// <exception cref="ArgumentException">The payload exceeds the 12-byte inline window.
    /// <para>负载超出 12 字节内联窗口。</para></exception>
    public static void WriteFesTransfer(Span<byte> destination, SunxiEfexCommand command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 12)
        {
            throw new ArgumentException("A FES transfer header carries at most 12 inline bytes.", nameof(payload));
        }

        var header = destination[..SunxiEfexConstants.FesTransferSize];
        header.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)command);
        payload.CopyTo(header[4..]);
        SunxiEfexConstants.RequestMagic.CopyTo(header[16..]);
    }

    /// <summary>
    /// Parses and validates the 13-byte bulk response.
    /// <para>解析并校验 13 字节批量响应。</para>
    /// </summary>
    /// <param name="source">Response bytes, at least 13. <para>响应字节，至少 13 个。</para></param>
    /// <returns>The parsed response. <para>解析出的响应。</para></returns>
    /// <exception cref="InvalidDataException">The magic is not <c>AWUS</c>. <para>魔数不是 <c>AWUS</c>。</para></exception>
    public static SunxiUsbResponse ReadResponse(ReadOnlySpan<byte> source)
    {
        if (source.Length < SunxiEfexConstants.ResponseSize
            || !source[..4].SequenceEqual(SunxiEfexConstants.ResponseMagic))
        {
            throw new InvalidDataException("The Allwinner bulk response does not start with the AWUS magic.");
        }

        return new SunxiUsbResponse(
            BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            source[12]);
    }

    /// <summary>
    /// Parses and validates the 32-byte identity payload.
    /// <para>解析并校验 32 字节身份负载。</para>
    /// </summary>
    /// <param name="source">Identity bytes, at least 32. <para>身份字节，至少 32 个。</para></param>
    /// <returns>The parsed identity. <para>解析出的身份信息。</para></returns>
    /// <exception cref="InvalidDataException">The payload is shorter than 32 bytes or its magic is not <c>AWUSBEFE</c>.
    /// <para>负载短于 32 字节，或其魔数不是 <c>AWUSBEFE</c>。</para></exception>
    public static SunxiDeviceIdentity ReadIdentity(ReadOnlySpan<byte> source)
    {
        if (source.Length < SunxiEfexConstants.IdentitySize
            || !source[..8].SequenceEqual(SunxiEfexConstants.IdentityMagic))
        {
            throw new InvalidDataException("The Allwinner identity payload does not start with the AWUSBEF magic.");
        }

        return new SunxiDeviceIdentity(
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[12..]),
            (SunxiDeviceMode)BinaryPrimitives.ReadUInt16LittleEndian(source[16..]),
            source[18],
            source[19],
            BinaryPrimitives.ReadUInt32LittleEndian(source[20..]));
    }

    /// <summary>
    /// Parses the 12-byte verification payload into its flag and CRC words.
    /// <para>把 12 字节校验负载解析为标志与 CRC 字。</para>
    /// </summary>
    /// <param name="source">Verification bytes, at least 12. <para>校验字节，至少 12 个。</para></param>
    /// <returns>The parsed verification result. <para>解析出的校验结果。</para></returns>
    /// <exception cref="ArgumentException">The payload is shorter than 12 bytes. <para>负载短于 12 字节。</para></exception>
    public static SunxiFesVerifyResult ReadVerifyResult(ReadOnlySpan<byte> source)
    {
        if (source.Length < 12)
        {
            throw new ArgumentException("A FES verification payload is 12 bytes.", nameof(source));
        }

        return new SunxiFesVerifyResult(
            BinaryPrimitives.ReadUInt32LittleEndian(source),
            BinaryPrimitives.ReadInt32LittleEndian(source[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[8..]));
    }

    /// <summary>Builds the 12-byte inline payload of a FES transfer request. <para>构造 FES 传输请求的 12 字节内联负载。</para></summary>
    /// <param name="address">Transfer address in the unit the tag implies. <para>标签所隐含单位下的传输地址。</para></param>
    /// <param name="length">Chunk length in bytes. <para>分块字节长度。</para></param>
    /// <param name="flags">Data tag combined with transfer flags. <para>与传输标志组合的数据标签。</para></param>
    /// <returns>The inline payload. <para>内联负载。</para></returns>
    public static byte[] BuildFesTransferPayload(uint address, uint length, uint flags)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, address);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), flags);
        return payload;
    }

    /// <summary>Builds the 12-byte inline payload of a FES value verification request. <para>构造 FES 值校验请求的 12 字节内联负载。</para></summary>
    /// <param name="address">Storage address to verify. <para>待校验的存储地址。</para></param>
    /// <param name="size">Byte count to verify. <para>待校验的字节数。</para></param>
    /// <returns>The inline payload. <para>内联负载。</para></returns>
    public static byte[] BuildFesVerifyValuePayload(uint address, ulong size)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, address);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(4), size);
        return payload;
    }

    /// <summary>Builds the 12-byte inline payload of a FES status verification request. <para>构造 FES 状态校验请求的 12 字节内联负载。</para></summary>
    /// <param name="tag">Status tag of the transfer to query. <para>待查询传输的状态标签。</para></param>
    /// <returns>The inline payload. <para>内联负载。</para></returns>
    public static byte[] BuildFesVerifyStatusPayload(uint tag)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), tag);
        return payload;
    }

    /// <summary>
    /// Reports whether a data tag addresses its target in bytes instead of 512-byte sectors.
    /// <para>判断数据标签按字节而不是 512 字节扇区寻址。</para>
    /// </summary>
    /// <param name="tag">Data tag of the transfer. <para>传输的数据标签。</para></param>
    /// <returns><see langword="true"/> for byte addressing, <see langword="false"/> for sector addressing.
    /// <para>按字节寻址时为 <see langword="true"/>，按扇区寻址时为 <see langword="false"/>。</para></returns>
    public static bool IsByteAddressed(SunxiFesDataTag tag) => ((uint)tag & SunxiEfexConstants.DataTagMask) != 0;
}

/// <summary>
/// Minimal bulk transfer surface the eFEX sessions need, so the protocol is testable without a device.
/// <para>eFEX 会话所需的最小批量传输面，使协议无需设备即可测试。</para>
/// </summary>
internal interface ISunxiEfexTransport
{
    /// <summary>Writes one complete buffer to the OUT endpoint. <para>把一个完整缓冲区写入 OUT 端点。</para></summary>
    /// <param name="data">Bytes to write. <para>待写入的字节。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <param name="timeoutMilliseconds">Transfer timeout, or <see langword="null"/> for the transport default.
    /// <para>传输超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>A task that completes when the buffer is on the wire. <para>缓冲区发出后完成的任务。</para></returns>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken, int? timeoutMilliseconds = null);

    /// <summary>Reads up to one buffer from the IN endpoint. <para>从 IN 端点最多读取一个缓冲区。</para></summary>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Transfer timeout, or <see langword="null"/> for the transport default.
    /// <para>传输超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>Bytes actually received, which may be fewer than requested. <para>实际收到的字节数，可能少于请求量。</para></returns>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken, int? timeoutMilliseconds = null);
}

/// <summary>
/// Adapter that runs the eFEX sessions over an opened USB device session.
/// <para>把 eFEX 会话跑在已打开的 USB 设备会话之上的适配器。</para>
/// </summary>
internal sealed class SunxiEfexUsbTransport(IUsbDeviceSession session) : ISunxiEfexTransport
{
    /// <summary>
    /// Writes the buffer and fails when the backend accepts only part of it.
    /// <para>写入缓冲区，并在后端只接受部分字节时失败。</para>
    /// </summary>
    /// <param name="data">Bytes to write. <para>待写入的字节。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <param name="timeoutMilliseconds">Transfer timeout, or <see langword="null"/> for the session default.
    /// <para>传输超时；<see langword="null"/> 表示使用会话默认值。</para></param>
    /// <returns>A task that completes when the buffer is on the wire. <para>缓冲区发出后完成的任务。</para></returns>
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

    /// <summary>
    /// Reads one packet into the buffer.
    /// <para>把一个数据包读入缓冲区。</para>
    /// </summary>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Transfer timeout, or <see langword="null"/> for the session default.
    /// <para>传输超时；<see langword="null"/> 表示使用会话默认值。</para></param>
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
}