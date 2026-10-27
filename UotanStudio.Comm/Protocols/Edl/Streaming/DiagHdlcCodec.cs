using UotanStudio.Hash.Crc16CcittReversed;

namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Pure HDLC framing for DIAG streaming packets: the 0x7E flag, the 0x7D escape with 0x5E and
/// 0x5D un-escape pairs, and the reflected CRC16-CCITT checksum.
/// <para>流式 DIAG 报文的纯 HDLC 帧封装：0x7E 标志字节、0x7D 转义与 0x5E/0x5D 反转义对，以及
/// 反射 CRC16-CCITT 校验。</para>
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
    /// Computes the reflected CRC16-CCITT the DIAG HDLC frames carry. The polynomial, the
    /// <c>0xFFFF</c> seed and the final inversion are the ones the <c>UotanStudio.Hash</c>
    /// <c>Crc16CcittReversed.Compute</c> helper applies, so this codec keeps no table of its own.
    /// <para>计算 DIAG HDLC 帧携带的反射 CRC16-CCITT。多项式、<c>0xFFFF</c> 初值与最终取反沿用
    /// <c>UotanStudio.Hash</c> 的 <c>Crc16CcittReversed.Compute</c>，因此本编码器不自持查找表。</para>
    /// </summary>
    /// <param name="data">The bytes to checksum. <para>待校验的字节。</para></param>
    /// <returns>The CRC16 value. <para>CRC16 校验值。</para></returns>
    internal static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        return Crc16CcittReversed.Compute(data);
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