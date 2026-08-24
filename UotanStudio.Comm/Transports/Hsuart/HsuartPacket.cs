namespace UotanStudio.Comm.Transports.Hsuart;

/// <summary>
/// Packet identifiers used by the HSUART protocol layer.
/// <para>HSUART 协议层使用的数据包标识。</para>
/// </summary>
public enum HsuartPacketId : byte
{
    /// <summary>
    /// Regular data (protocol) packet.
    /// <para>常规数据（协议）包。</para>
    /// </summary>
    ProtocolPacket = 0xF0,

    /// <summary>
    /// Marks the end of a transfer.
    /// <para>表示一次传输的结束。</para>
    /// </summary>
    EndOfTransferPacket = 0x55,

    /// <summary>
    /// Positive acknowledgement.
    /// <para>肯定确认。</para>
    /// </summary>
    AckPacket = 0x06,

    /// <summary>
    /// Negative acknowledgement.
    /// <para>否定确认。</para>
    /// </summary>
    NakPacket = 0x09,

    /// <summary>
    /// Version negotiation packet.
    /// <para>版本协商包。</para>
    /// </summary>
    VersionPacket = 0xAA,

    /// <summary>
    /// Signals the device is ready to read.
    /// <para>表示设备已准备好读取。</para>
    /// </summary>
    ReadyToReadPacket = 0x0F
}

/// <summary>
/// Error codes reported by the HSUART packet encoder/decoder.
/// <para>HSUART 数据包编码器/解码器返回的错误码。</para>
/// </summary>
public enum HsuartPacketError
{
    /// <summary>
    /// Operation completed successfully.
    /// <para>操作成功完成。</para>
    /// </summary>
    Success,

    /// <summary>
    /// An argument was invalid.
    /// <para>参数无效。</para>
    /// </summary>
    InvalidParameter,

    /// <summary>
    /// The CRC check failed, indicating corruption.
    /// <para>CRC 校验失败，表明数据损坏。</para>
    /// </summary>
    PacketCorruption,

    /// <summary>
    /// The underlying COBS codec reported an error.
    /// <para>底层 COBS 编解码器报告了错误。</para>
    /// </summary>
    CobsError
}

/// <summary>
/// Builds and parses HSUART protocol packets: header (packet id [+ version]), optional
/// payload, CRC-16, all wrapped in COBS framing. ACK/NAK are transmitted as a single
/// raw byte without COBS framing.
/// <para>构建并解析 HSUART 协议数据包：头（数据包标识 [+ 版本]）、可选负载、CRC-16，
/// 全部包裹在 COBS 帧中。ACK/NAK 以单个原始字节传输，不做 COBS 封装。</para>
/// </summary>
public static class HsuartPacket
{
    private const int PacketIdSize = 1;
    private const int VersionPacketSize = 2;
    private const byte PacketLayerMajorId = 1;
    private const byte PacketLayerMinorId = 0;

    /// <summary>
    /// The maximum payload (and internal working buffer) size for a single packet.
    /// <para>单个数据包的最大负载（及内部工作缓冲区）大小。</para>
    /// </summary>
    public const int MaxPacketSize = 4 * 1024;

    /// <summary>
    /// Serializes a CRC value as little-endian bytes.
    /// <para>将 CRC 值序列化为小端序字节。</para>
    /// </summary>
    /// <param name="crc">The checksum. <para>校验值。</para></param>
    /// <returns>A 2-byte little-endian representation. <para>2 字节小端序表示。</para></returns>
    private static byte[] GetCrcBytes(ushort crc) => [(byte)(crc & 0xFF), (byte)(crc >> 8)];

    /// <summary>
    /// Reads a 2-byte little-endian unsigned value.
    /// <para>读取 2 字节小端序无符号值。</para>
    /// </summary>
    /// <param name="buffer">The source array. <para>源数组。</para></param>
    /// <param name="offset">The offset of the value. <para>值的偏移量。</para></param>
    /// <returns>The decoded value. <para>解码后的值。</para></returns>
    private static ushort ReadUInt16LittleEndian(byte[] buffer, int offset) => (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    /// <summary>
    /// Encodes a packet (with COBS framing except for ACK/NAK) into <paramref name="dst"/>.
    /// <para>将数据包编码（除 ACK/NAK 外均带 COBS 帧）到 <paramref name="dst"/>。</para>
    /// </summary>
    /// <param name="packetId">The packet identifier. <para>数据包标识。</para></param>
    /// <param name="src">The payload for data packets (empty allowed for <see cref="HsuartPacketId.EndOfTransferPacket"/>). <para>数据包的负载（<see cref="HsuartPacketId.EndOfTransferPacket"/> 允许为空）。</para></param>
    /// <param name="dst">The destination buffer; should provide at least <see cref="MaxPacketSize"/> + 16 bytes. <para>目标缓冲区；应至少提供 <see cref="MaxPacketSize"/> + 16 字节。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>The number of bytes written; 0 on error. <para>写入的字节数；出错返回 0。</para></returns>
    public static int Encode(HsuartPacketId packetId, ReadOnlySpan<byte> src, Span<byte> dst, out HsuartPacketError error)
    {
        error = HsuartPacketError.Success;

        switch (packetId)
        {
            case HsuartPacketId.AckPacket:
            case HsuartPacketId.NakPacket:
                if (dst.Length < PacketIdSize)
                {
                    error = HsuartPacketError.InvalidParameter;
                    return 0;
                }
                dst[0] = (byte)packetId;
                return PacketIdSize;
            case HsuartPacketId.ProtocolPacket:
                break;
            case HsuartPacketId.EndOfTransferPacket:
                break;
            case HsuartPacketId.VersionPacket:
                break;
            case HsuartPacketId.ReadyToReadPacket:
                break;
            default:
                break;
        }

        var tempBuffer = new byte[MaxPacketSize];
        // Ack/Nak 分支不写入 encodedLength，默认初始化是控制流必需的。
        // The Ack/Nak arms never assign encodedLength; the default init is required by control flow.
        var encodedLength = 0;

        switch (packetId)
        {
            case HsuartPacketId.VersionPacket:
                tempBuffer[0] = (byte)HsuartPacketId.VersionPacket;
                tempBuffer[1] = PacketLayerMajorId;
                tempBuffer[2] = PacketLayerMinorId;
                var versionCrc = Crc16.Calculate(tempBuffer.AsSpan(0, PacketIdSize + VersionPacketSize));
                var versionCrcBytes = GetCrcBytes(versionCrc);
                Array.Copy(versionCrcBytes, 0, tempBuffer, PacketIdSize + VersionPacketSize, 2);
                encodedLength = PacketIdSize + VersionPacketSize + 2;
                break;

            case HsuartPacketId.ReadyToReadPacket:
                tempBuffer[0] = (byte)HsuartPacketId.ReadyToReadPacket;
                var readyCrc = Crc16.Calculate(tempBuffer.AsSpan(0, PacketIdSize));
                var readyCrcBytes = GetCrcBytes(readyCrc);
                Array.Copy(readyCrcBytes, 0, tempBuffer, PacketIdSize, 2);
                encodedLength = PacketIdSize + 2;
                break;

            case HsuartPacketId.ProtocolPacket:
            case HsuartPacketId.EndOfTransferPacket:
                // EndOfTransfer 允许空负载（仅含标识 + CRC）；ProtocolPacket 必须携带负载。
                // EndOfTransfer may carry an empty payload (id + CRC only); ProtocolPacket requires a payload.
                if (src.IsEmpty && packetId == HsuartPacketId.ProtocolPacket)
                {
                    error = HsuartPacketError.InvalidParameter;
                    return 0;
                }
                if (PacketIdSize + src.Length + 2 > tempBuffer.Length)
                {
                    error = HsuartPacketError.InvalidParameter;
                    return 0;
                }
                tempBuffer[0] = (byte)packetId;
                if (!src.IsEmpty)
                {
                    src.CopyTo(tempBuffer.AsSpan(PacketIdSize));
                }

                encodedLength = PacketIdSize + src.Length;
                var dataCrc = Crc16.Calculate(tempBuffer.AsSpan(0, encodedLength));
                var dataCrcBytes = GetCrcBytes(dataCrc);
                Array.Copy(dataCrcBytes, 0, tempBuffer, encodedLength, 2);
                encodedLength += 2;
                break;
            case HsuartPacketId.AckPacket:
                break;
            case HsuartPacketId.NakPacket:
                break;
            default:
                error = HsuartPacketError.InvalidParameter;
                return 0;
        }

        var result = CobsCodec.StuffBytes(tempBuffer.AsSpan(0, encodedLength), dst, out var cobsError);
        if (cobsError != CobsError.Success)
        {
            error = HsuartPacketError.CobsError;
            return 0;
        }
        return result;
    }

    /// <summary>
    /// Decodes a received packet (COBS frame or raw ACK/NAK byte), verifies its CRC, and
    /// copies the payload into <paramref name="dst"/>.
    /// <para>解码接收到的数据包（COBS 帧或原始 ACK/NAK 字节），校验其 CRC，
    /// 并将负载复制到 <paramref name="dst"/>。</para>
    /// </summary>
    /// <param name="src">The received bytes. <para>接收到的字节。</para></param>
    /// <param name="packetId">The decoded packet identifier. <para>解码出的数据包标识。</para></param>
    /// <param name="dst">The destination buffer for the payload. <para>负载的目标缓冲区。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>The payload length copied to <paramref name="dst"/>; 0 on error or for control packets. <para>复制到 <paramref name="dst"/> 的负载长度；出错或控制包返回 0。</para></returns>
    public static int Decode(ReadOnlySpan<byte> src, out HsuartPacketId packetId, Span<byte> dst, out HsuartPacketError error)
    {
        error = HsuartPacketError.Success;

        if (src.IsEmpty)
        {
            packetId = 0;
            error = HsuartPacketError.InvalidParameter;
            return 0;
        }

        packetId = (HsuartPacketId)src[0];

        if (src.Length == 1)
        {
            if (packetId is HsuartPacketId.AckPacket or HsuartPacketId.NakPacket)
            {
                return 0;
            }

            error = HsuartPacketError.InvalidParameter;
            return 0;
        }

        var tempBuffer = new byte[MaxPacketSize];
        var unstuffedLength = CobsCodec.UnstuffBytes(src, tempBuffer.AsSpan(), out var cobsError);

        if (cobsError != CobsError.Success)
        {
            error = cobsError == CobsError.InvalidStuffing
                ? HsuartPacketError.CobsError
                : HsuartPacketError.InvalidParameter;
            return 0;
        }

        packetId = (HsuartPacketId)tempBuffer[0];

        switch (packetId)
        {
            case HsuartPacketId.AckPacket:
            case HsuartPacketId.NakPacket:
                return 0;

            case HsuartPacketId.VersionPacket:
                if (unstuffedLength < PacketIdSize + VersionPacketSize + 2)
                {
                    error = HsuartPacketError.InvalidParameter;
                    return 0;
                }
                {
                    var receivedCrc = Crc16.Calculate(tempBuffer.AsSpan(0, PacketIdSize + VersionPacketSize));
                    var expectedCrc = ReadUInt16LittleEndian(tempBuffer, PacketIdSize + VersionPacketSize);
                    if (receivedCrc != expectedCrc)
                    {
                        error = HsuartPacketError.PacketCorruption;
                        return 0;
                    }
                }
                return 0;

            case HsuartPacketId.ProtocolPacket:
            case HsuartPacketId.EndOfTransferPacket:
                if (unstuffedLength < PacketIdSize + 2)
                {
                    error = HsuartPacketError.InvalidParameter;
                    return 0;
                }
                {
                    var dataLength = unstuffedLength - PacketIdSize - 2;
                    var receivedCrc = Crc16.Calculate(tempBuffer.AsSpan(0, unstuffedLength - 2));
                    var expectedCrc = ReadUInt16LittleEndian(tempBuffer, unstuffedLength - 2);
                    if (receivedCrc != expectedCrc)
                    {
                        error = HsuartPacketError.PacketCorruption;
                        return 0;
                    }
                    if (dst.Length < dataLength)
                    {
                        error = HsuartPacketError.InvalidParameter;
                        return 0;
                    }
                    if (dataLength > 0)
                    {
                        tempBuffer.AsSpan(PacketIdSize, dataLength).CopyTo(dst);
                    }

                    return dataLength;
                }

            case HsuartPacketId.ReadyToReadPacket:
                if (unstuffedLength < PacketIdSize + 2)
                {
                    error = HsuartPacketError.InvalidParameter;
                    return 0;
                }
                {
                    var receivedCrc = Crc16.Calculate(tempBuffer.AsSpan(0, PacketIdSize));
                    var expectedCrc = ReadUInt16LittleEndian(tempBuffer, PacketIdSize);
                    if (receivedCrc != expectedCrc)
                    {
                        error = HsuartPacketError.PacketCorruption;
                        return 0;
                    }
                }
                return 0;

            default:
                error = HsuartPacketError.InvalidParameter;
                return 0;
        }
    }
}