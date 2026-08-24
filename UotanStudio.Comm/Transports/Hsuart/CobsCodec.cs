namespace UotanStudio.Comm.Transports.Hsuart;

/// <summary>
/// Error codes reported by the COBS encoder/decoder.
/// <para>COBS 编码器/解码器返回的错误码。</para>
/// </summary>
public enum CobsError
{
    /// <summary>
    /// Operation completed successfully.
    /// <para>操作成功完成。</para>
    /// </summary>
    Success,

    /// <summary>
    /// The source buffer reference is null.
    /// <para>源缓冲区引用为空。</para>
    /// </summary>
    NullSourceBuffer,

    /// <summary>
    /// The destination buffer reference is null.
    /// <para>目标缓冲区引用为空。</para>
    /// </summary>
    NullDestinationBuffer,

    /// <summary>
    /// The input length is invalid (e.g. empty input).
    /// <para>输入长度无效（例如输入为空）。</para>
    /// </summary>
    InvalidLength,

    /// <summary>
    /// The encoded stream violates COBS stuffing rules.
    /// <para>编码流违反 COBS 填充规则。</para>
    /// </summary>
    InvalidStuffing,

    /// <summary>
    /// The destination buffer is too small for the result.
    /// <para>目标缓冲区不足以容纳结果。</para>
    /// </summary>
    DestinationBufferOverflow
}

/// <summary>
/// COBS (Consistent Overhead Byte Stuffing) codec used by the HSUART packet layer.
/// <para>HSUART 数据包层使用的 COBS（一致开销字节填充）编解码器。</para>
/// COBS guarantees a bounded worst-case overhead of one byte per 254 payload bytes and
/// makes 0x00 an unambiguous packet delimiter.
/// <para>COBS 保证最坏情况下每 254 个负载字节仅增加一个字节的开销，
/// 并使得 0x00 成为无歧义的数据包分隔符。</para>
/// </summary>
public static class CobsCodec
{
    /// <summary>
    /// Encodes a source span using COBS, terminating the output with a trailing 0x00.
    /// <para>使用 COBS 对源跨度进行编码，并以尾部 0x00 结束输出。</para>
    /// </summary>
    /// <param name="src">The input bytes (must be non-empty). <para>输入字节（必须非空）。</para></param>
    /// <param name="dst">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>The number of bytes written to <paramref name="dst"/>; 0 on error. <para>写入 <paramref name="dst"/> 的字节数；出错返回 0。</para></returns>
    public static int StuffBytes(ReadOnlySpan<byte> src, Span<byte> dst, out CobsError error)
    {
        error = CobsError.Success;

        if (src.IsEmpty)
        {
            error = CobsError.InvalidLength;
            return 0;
        }

        if (dst.Length < 2)
        {
            error = CobsError.NullDestinationBuffer;
            return 0;
        }

        var srcIndex = 0;
        var dstIndex = 1;
        var codeIndex = 0;
        byte code = 1;

        while (srcIndex < src.Length)
        {
            if (src[srcIndex] == 0x00)
            {
                if (codeIndex >= dst.Length)
                {
                    error = CobsError.DestinationBufferOverflow;
                    return 0;
                }
                dst[codeIndex] = code;
                code = 1;
                codeIndex = dstIndex;
                dstIndex++;
                srcIndex++;
            }
            else
            {
                if (dstIndex >= dst.Length)
                {
                    error = CobsError.DestinationBufferOverflow;
                    return 0;
                }
                dst[dstIndex] = src[srcIndex];
                dstIndex++;
                srcIndex++;
                code++;

                if (code == 0xFF)
                {
                    if (codeIndex >= dst.Length)
                    {
                        error = CobsError.DestinationBufferOverflow;
                        return 0;
                    }
                    dst[codeIndex] = code;
                    code = 1;
                    codeIndex = dstIndex;
                    dstIndex++;
                }
            }
        }

        if (codeIndex >= dst.Length)
        {
            error = CobsError.DestinationBufferOverflow;
            return 0;
        }
        dst[codeIndex] = code;

        if (dstIndex >= dst.Length)
        {
            error = CobsError.DestinationBufferOverflow;
            return 0;
        }
        dst[dstIndex] = 0x00;
        dstIndex++;

        return dstIndex;
    }

    /// <summary>
    /// Decodes a COBS-encoded source span back to the original bytes.
    /// <para>将 COBS 编码的源跨度解码回原始字节。</para>
    /// </summary>
    /// <param name="src">The encoded input. <para>编码后的输入。</para></param>
    /// <param name="dst">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>The number of bytes written to <paramref name="dst"/>; 0 on error. <para>写入 <paramref name="dst"/> 的字节数；出错返回 0。</para></returns>
    public static int UnstuffBytes(ReadOnlySpan<byte> src, Span<byte> dst, out CobsError error)
    {
        error = CobsError.Success;

        if (src.IsEmpty)
        {
            error = CobsError.InvalidLength;
            return 0;
        }

        if (dst.IsEmpty)
        {
            error = CobsError.NullDestinationBuffer;
            return 0;
        }

        var srcIndex = 0;
        var dstIndex = 0;

        while (srcIndex < src.Length)
        {
            var code = src[srcIndex];
            srcIndex++;

            if (code == 0x00)
            {
                break;
            }

            var blockLen = code - 1;

            if (srcIndex + blockLen > src.Length)
            {
                error = CobsError.InvalidStuffing;
                return 0;
            }

            if (dstIndex + blockLen > dst.Length)
            {
                error = CobsError.DestinationBufferOverflow;
                return 0;
            }

            for (var i = 0; i < blockLen; i++)
            {
                dst[dstIndex] = src[srcIndex];
                dstIndex++;
                srcIndex++;
            }

            if (code != 0xFF && srcIndex < src.Length && src[srcIndex] != 0x00)
            {
                if (dstIndex >= dst.Length)
                {
                    error = CobsError.DestinationBufferOverflow;
                    return 0;
                }
                dst[dstIndex] = 0x00;
                dstIndex++;
            }
        }

        return dstIndex;
    }

    /// <summary>
    /// Encodes a byte-array slice using COBS (array overload).
    /// <para>使用 COBS 对字节数组切片进行编码（数组重载）。</para>
    /// </summary>
    /// <param name="src">The input array. <para>输入数组。</para></param>
    /// <param name="srcLength">The number of input bytes. <para>输入字节数。</para></param>
    /// <param name="dst">The destination array. <para>目标数组。</para></param>
    /// <param name="dstLength">The capacity of <paramref name="dst"/>. <para><paramref name="dst"/> 的容量。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>The number of bytes written; 0 on error. <para>写入的字节数；出错返回 0。</para></returns>
    public static int StuffBytes(byte[] src, int srcLength, byte[] dst, int dstLength, out CobsError error) => StuffBytes(src.AsSpan(0, srcLength), dst.AsSpan(0, dstLength), out error);

    /// <summary>
    /// Decodes a byte-array slice using COBS (array overload).
    /// <para>使用 COBS 对字节数组切片进行解码（数组重载）。</para>
    /// </summary>
    /// <param name="src">The encoded input array. <para>编码后的输入数组。</para></param>
    /// <param name="srcLength">The number of input bytes. <para>输入字节数。</para></param>
    /// <param name="dst">The destination array. <para>目标数组。</para></param>
    /// <param name="dstLength">The capacity of <paramref name="dst"/>. <para><paramref name="dst"/> 的容量。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>The number of bytes written; 0 on error. <para>写入的字节数；出错返回 0。</para></returns>
    public static int UnstuffBytes(byte[] src, int srcLength, byte[] dst, int dstLength, out CobsError error) => UnstuffBytes(src.AsSpan(0, srcLength), dst.AsSpan(0, dstLength), out error);
}