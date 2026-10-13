namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Pure HDLC framing for DIAG streaming packets: the 0x7E flag, the 0x7D escape with 0x5E and
/// 0x5D un-escape pairs, and the reflected CRC16-CCITT table.
/// <para>流式 DIAG 报文的纯 HDLC 帧封装：0x7E 标志字节、0x7D 转义与 0x5E/0x5D 反转义对，以及
/// 反射 CRC16-CCITT 表。</para>
/// </summary>
/// <remarks>
/// This is the only place in the streaming stack that touches raw HDLC bytes; every buffer is
/// bounds-checked before it is read or written.
/// <para>流式协议栈中唯一触碰原始 HDLC 字节的地方；每个缓冲区在读写前都完成边界检查。</para>
/// </remarks>
internal static class DiagHdlcCodec
{
    internal const byte FrameFlag = 0x7E;
    internal const byte EscapeByte = 0x7D;
    private const byte EscapedFlag = 0x5E;
    private const byte EscapedEscape = 0x5D;

    /// <summary>
    /// The 256-entry CRC16 table.
    /// <para>256 项 CRC16 表。</para>
    /// </summary>
    private static readonly ushort[] CrcTable =
    [
        0x0000, 0x1189, 0x2312, 0x329b, 0x4624, 0x57ad, 0x6536, 0x74bf,
        0x8c48, 0x9dc1, 0xaf5a, 0xbed3, 0xca6c, 0xdbe5, 0xe97e, 0xf8f7,
        0x1081, 0x0108, 0x3393, 0x221a, 0x56a5, 0x472c, 0x75b7, 0x643e,
        0x9cc9, 0x8d40, 0xbfdb, 0xae52, 0xdaed, 0xcb64, 0xf9ff, 0xe876,
        0x2102, 0x308b, 0x0210, 0x1399, 0x6726, 0x76af, 0x4434, 0x55bd,
        0xad4a, 0xbcc3, 0x8e58, 0x9fd1, 0xeb6e, 0xfae7, 0xc87c, 0xd9f5,
        0x3183, 0x200a, 0x1291, 0x0318, 0x77a7, 0x662e, 0x54b5, 0x453c,
        0xbdcb, 0xac42, 0x9ed9, 0x8f50, 0xfbef, 0xea66, 0xd8fd, 0xc974,
        0x4204, 0x538d, 0x6116, 0x709f, 0x0420, 0x15a9, 0x2732, 0x36bb,
        0xce4c, 0xdfc5, 0xed5e, 0xfcd7, 0x8868, 0x99e1, 0xab7a, 0xbaf3,
        0x5285, 0x430c, 0x7197, 0x601e, 0x14a1, 0x0528, 0x37b3, 0x263a,
        0xdecd, 0xcf44, 0xfddf, 0xec56, 0x98e9, 0x8960, 0xbbfb, 0xaa72,
        0x6306, 0x728f, 0x4014, 0x519d, 0x2522, 0x34ab, 0x0630, 0x17b9,
        0xef4e, 0xfec7, 0xcc5c, 0xddd5, 0xa96a, 0xb8e3, 0x8a78, 0x9bf1,
        0x7387, 0x620e, 0x5095, 0x411c, 0x35a3, 0x242a, 0x16b1, 0x0738,
        0xffcf, 0xee46, 0xdcdd, 0xcd54, 0xb9eb, 0xa862, 0x9af9, 0x8b70,
        0x8408, 0x9581, 0xa71a, 0xb693, 0xc22c, 0xd3a5, 0xe13e, 0xf0b7,
        0x0840, 0x19c9, 0x2b52, 0x3adb, 0x4e64, 0x5fed, 0x6d76, 0x7cff,
        0x9489, 0x8500, 0xb79b, 0xa612, 0xd2ad, 0xc324, 0xf1bf, 0xe036,
        0x18c1, 0x0948, 0x3bd3, 0x2a5a, 0x5ee5, 0x4f6c, 0x7df7, 0x6c7e,
        0xa50a, 0xb483, 0x8618, 0x9791, 0xe32e, 0xf2a7, 0xc03c, 0xd1b5,
        0x2942, 0x38cb, 0x0a50, 0x1bd9, 0x6f66, 0x7eef, 0x4c74, 0x5dfd,
        0xb58b, 0xa402, 0x9699, 0x8710, 0xf3af, 0xe226, 0xd0bd, 0xc134,
        0x39c3, 0x284a, 0x1ad1, 0x0b58, 0x7fe7, 0x6e6e, 0x5cf5, 0x4d7c,
        0xc60c, 0xd785, 0xe51e, 0xf497, 0x8028, 0x91a1, 0xa33a, 0xb2b3,
        0x4a44, 0x5bcd, 0x6956, 0x78df, 0x0c60, 0x1de9, 0x2f72, 0x3efb,
        0xd68d, 0xc704, 0xf59f, 0xe416, 0x90a9, 0x8120, 0xb3bb, 0xa232,
        0x5ac5, 0x4b4c, 0x79d7, 0x685e, 0x1ce1, 0x0d68, 0x3ff3, 0x2e7a,
        0xe70e, 0xf687, 0xc41c, 0xd595, 0xa12a, 0xb0a3, 0x8238, 0x93b1,
        0x6b46, 0x7acf, 0x4854, 0x59dd, 0x2d62, 0x3ceb, 0x0e70, 0x1ff9,
        0xf78f, 0xe606, 0xd49d, 0xc514, 0xb1ab, 0xa022, 0x92b9, 0x8330,
        0x7bc7, 0x6a4e, 0x58d5, 0x495c, 0x3de3, 0x2c6a, 0x1ef1, 0x0f78
    ];

    /// <summary>
    /// Computes the CRC16 (init 0xFFFF, table walkthrough, final inversion) over the
    /// supplied bytes.
    /// <para>计算 CRC16（初值 0xFFFF、查表推进、最终取反）。</para>
    /// </summary>
    /// <param name="data">The bytes to checksum. <para>待校验的字节。</para></param>
    /// <returns>The CRC16 value. <para>CRC16 校验值。</para></returns>
    internal static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in data)
        {
            crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ value) & 0xFF]);
        }

        return (ushort)~crc;
    }

    /// <summary>
    /// Frames a DIAG packet: optionally append
    /// the little-endian CRC16 of the payload, escape every 0x7E and 0x7D byte, and terminate
    /// with the 0x7E flag. An oversized payload is rejected.
    /// <para>封装 DIAG 报文：可选地追加负载的小端 CRC16，把每个
    /// 0x7E 与 0x7D 字节转义，最后以 0x7E 标志结束。超长负载会被直接拒绝。</para>
    /// </summary>
    /// <param name="payload">The DIAG payload bytes to frame. <para>待封装的 DIAG 负载字节。</para></param>
    /// <param name="withCrc">Whether to append the little-endian CRC16 before escaping.
    /// <para>是否在转义前追加小端 CRC16。</para></param>
    /// <returns>The framed packet, ending with the trailing flag. <para>封装后的报文，以尾部标志结束。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the payload is empty or exceeds
    /// <see cref="StreamingConstants.MaxPacketBytes"/>. <para>负载为空或超过
    /// <see cref="StreamingConstants.MaxPacketBytes"/> 时抛出。</para></exception>
    internal static byte[] Encode(ReadOnlySpan<byte> payload, bool withCrc)
    {
        if (payload.IsEmpty || payload.Length > StreamingConstants.MaxPacketBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                "The DIAG payload must contain between 1 and the maximum packet length in bytes.");
        }

        var checksum = ComputeCrc16(payload);
        var source = new byte[payload.Length + (withCrc ? 2 : 0)];
        payload.CopyTo(source);
        var length = payload.Length;
        if (withCrc)
        {
            source[length] = (byte)(checksum & 0xFF);
            source[length + 1] = (byte)(checksum >> 8);
            length += 2;
        }

        var output = new byte[(length * 2) + 1];
        var index = 0;
        for (var position = 0; position < length; position++)
        {
            var value = source[position];
            if (value == FrameFlag)
            {
                output[index++] = EscapeByte;
                output[index++] = EscapedFlag;
            }
            else if (value == EscapeByte)
            {
                output[index++] = EscapeByte;
                output[index++] = EscapedEscape;
            }
            else
            {
                output[index++] = value;
            }
        }

        output[index++] = FrameFlag;
        Array.Resize(ref output, index);
        return output;
    }

    /// <summary>
    /// Un-frames a DIAG packet: un-escape the bytes,
    /// drop a leading 0x7E flag, require the trailing flag, and when the frame carries a CRC
    /// strip the two checksum bytes (optionally verifying them first). Decoding normalizes the
    /// no-CRC asymmetry so callers always see the payload starting at the DIAG response byte.
    /// <para>解除 DIAG 封装：反转义字节、去掉首部 0x7E 标志、要求尾部标志，并在帧
    /// 带校验时剥掉两个校验字节（可选择先验证）。解码统一无校验帧的差异，
    /// 使调用方看到的负载始终从 DIAG 响应字节开始。</para>
    /// </summary>
    /// <param name="frame">The raw framed bytes as read from the transport.
    /// <para>从传输读到的原始封装字节。</para></param>
    /// <param name="withCrc">Whether the frame carries a two-byte CRC trailer.
    /// <para>帧是否带有两字节 CRC 尾部。</para></param>
    /// <param name="verifyCrc">Whether a present CRC is verified before the payload is returned.
    /// <para>返回负载前是否验证存在的 CRC。</para></param>
    /// <param name="payload">The decoded DIAG payload starting at the response byte, or
    /// <see langword="null"/> when the frame is invalid. <para>从响应字节开始的解码 DIAG 负载；
    /// 帧无效时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the frame decoded successfully.
    /// <para>帧解码成功时为 <see langword="true"/>。</para></returns>
    internal static bool TryDecode(ReadOnlySpan<byte> frame, bool withCrc, bool verifyCrc, out byte[]? payload)
    {
        payload = null;
        if (frame.IsEmpty || frame.Length > (StreamingConstants.MaxPacketBytes * 2) + 3)
        {
            return false;
        }

        var buffer = new byte[frame.Length];
        var length = 0;
        var escaped = false;
        foreach (var value in frame)
        {
            if (escaped)
            {
                if (value == EscapedFlag)
                {
                    buffer[length++] = FrameFlag;
                }
                else if (value == EscapedEscape)
                {
                    buffer[length++] = EscapeByte;
                }
                else
                {
                    return false;
                }

                escaped = false;
            }
            else if (value == EscapeByte)
            {
                escaped = true;
            }
            else
            {
                buffer[length++] = value;
            }
        }

        if (escaped)
        {
            return false;
        }

        var data = buffer.AsSpan(0, length);
        if (!data.IsEmpty && data[0] == FrameFlag)
        {
            data = data[1..];
        }

        var trailer = withCrc ? 3 : 1;
        if (data.Length < trailer || data[^1] != FrameFlag)
        {
            return false;
        }

        var body = data[..^trailer];
        if (body.IsEmpty)
        {
            return false;
        }

        if (withCrc && verifyCrc)
        {
            var received = (ushort)(data[^3] | (data[^2] << 8));
            if (ComputeCrc16(body) != received)
            {
                return false;
            }
        }

        payload = body.ToArray();
        return true;
    }
}