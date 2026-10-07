using System.Buffers.Binary;

namespace UotanStudio.Firmware.Compression.Codecs.Lzo;

/// <summary>
/// Outcome codes returned by the LZO1X codec.
/// <para>LZO1X 编解码器返回的结果状态码。</para>
/// </summary>
internal enum LzoStatus : byte
{
    /// <summary>The operation completed successfully. <para>操作成功完成。</para></summary>
    Ok,

    /// <summary>The bitstream ended while input bytes remained unconsumed. <para>位流已结束但仍有输入字节未被消费。</para></summary>
    InputNotConsumed,

    /// <summary>The bitstream demands more input bytes than are available. <para>位流需要的输入字节超出可用范围。</para></summary>
    InputOverrun,

    /// <summary>A decoded run exceeds the output buffer capacity. <para>解码输出超出输出缓冲区容量。</para></summary>
    OutputOverrun,

    /// <summary>A match references a position before the start of the output window. <para>匹配引用了输出窗口起点之前的位置。</para></summary>
    LookbehindOverrun,

    /// <summary>The bitstream is structurally invalid. <para>位流结构无效。</para></summary>
    Error,
}

internal static class Lzo1x
{
    private const int M2MaxOffset = 0x0800;
    private const int MinZeroRunLength = 4;
    private const int DBits = 13;
    private const int DSize = 1 << DBits;
    private const int DMask = DSize - 1;
    private const int M2MaxLen = 8;
    private const int M3MaxOffset = 0x4000;
    private const int M3MaxLen = 33;
    private const int M4MaxLen = 9;
    private const int M3Marker = 32;
    private const int M4Marker = 16;
    private const long Max255Count = (long.MaxValue / 255) - 2;

    /// <summary>
    /// Decompresses an LZO1X bitstream into output with full bounds checking (mirroring the kernel's safe variant).
    /// <para>解压 LZO1X 位流到 output，带完整边界检查（对齐内核 safe 变体）。</para>
    /// </summary>
    /// <param name="input">LZO1X bitstream to decode, treated as untrusted; fewer than three available
    /// bytes are reported as <see cref="LzoStatus.InputOverrun"/> and an optional two-byte version header
    /// is consumed when the stream starts with <c>17</c>.
    /// <para>待解码的 LZO1X 位流，按不可信输入处理；可用字节少于三个时报告 <see cref="LzoStatus.InputOverrun"/>，
    /// 位流以 <c>17</c> 开头时消费其前两字节的版本头。</para></param>
    /// <param name="output">Destination buffer whose length is the decode capacity; it is written only
    /// when the returned status is <see cref="LzoStatus.Ok"/> and is left untouched otherwise.
    /// <para>目标缓冲区，其长度即解码容量上限；仅当返回状态为 <see cref="LzoStatus.Ok"/> 时写入，否则保持不变。</para></param>
    /// <param name="bytesWritten">Bytes the decoder produced, reported under every status and <c>0</c> when
    /// it stopped before emitting anything; on failure this count is not mirrored into
    /// <paramref name="output"/>.
    /// <para>解码器产生的字节数，任何状态下都会给出，在尚未写出内容前停止时为 <c>0</c>；失败时该数值不会写入
    /// <paramref name="output"/>。</para></param>
    /// <returns>The status of the decode attempt.
    /// <para>本次解码尝试的状态。</para></returns>
    public static LzoStatus TryDecompress(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        bytesWritten = 0;


        var inArr = input.ToArray();
        var outArr = new byte[output.Length];
        var (status, written) = TryDecompressCore(inArr, outArr);
        if (status == LzoStatus.Ok)
        {
            outArr.AsSpan(0, written).CopyTo(output);
        }

        bytesWritten = written;
        return status;
    }

    /// <summary>
    /// Decompresses one LZO1X bitstream with full bounds checking. This is a faithful port of the
    /// upstream minilzo safe-variant decoder; the cursor names (<c>ip</c>, <c>op</c>, <c>state</c>,
    /// <c>t</c>, <c>mPos</c>) intentionally mirror that implementation so future updates can be diffed
    /// against upstream, so do not rename them when editing the control flow.
    /// <para>带完整边界检查地解压单个 LZO1X 位流。本方法忠实移植上游 minilzo 的 safe 变体解码器；
    /// 游标名（<c>ip</c>、<c>op</c>、<c>state</c>、<c>t</c>、<c>mPos</c>）刻意与上游实现保持一致，
    /// 便于后续升级时与上游逐行比对，修改控制流时不要改名。</para>
    /// </summary>
    private static (LzoStatus, int) TryDecompressCore(byte[] input, byte[] output)
    {
        var inLen = input.Length;
        if (inLen < 3)
        {
            return (LzoStatus.InputOverrun, 0);
        }

        var ip = 0;
        var op = 0;
        var ipEnd = inLen;
        var opEnd = output.Length;
        uint state = 0;
        uint next = 0;
        var bitstreamVersion = 0;


        if (inLen >= 5 && input[0] == 17)
        {
            bitstreamVersion = input[1];
            ip = 2;
        }


        LzoStatus CopyLiteralRun(uint t)
        {
            if (t > (uint)(opEnd - op))
            {
                return LzoStatus.OutputOverrun;
            }

            if (t + 3 > (uint)(ipEnd - ip))
            {
                return LzoStatus.InputOverrun;
            }

            Array.Copy(input, ip, output, op, t);
            ip += (int)t;
            op += (int)t;
            state = 4;
            return LzoStatus.Ok;
        }


        LzoStatus MatchNext()
        {
            var t = state;
            if (t + 3 > (uint)(ipEnd - ip))
            {
                return LzoStatus.InputOverrun;
            }

            if (t > (uint)(opEnd - op))
            {
                return LzoStatus.OutputOverrun;
            }

            Array.Copy(input, ip, output, op, t);
            ip += (int)t;
            op += (int)t;
            return LzoStatus.Ok;
        }


        LzoStatus CopyMatch(int mPos, uint t)
        {
            if (mPos < 0)
            {
                return LzoStatus.LookbehindOverrun;
            }

            if (t > (uint)(opEnd - op))
            {
                return LzoStatus.OutputOverrun;
            }

            var end = op + (int)t;
            output[op++] = output[mPos];
            output[op++] = output[mPos + 1];
            mPos += 2;
            while (op < end)
            {
                output[op++] = output[mPos++];
            }

            return LzoStatus.Ok;
        }


        var entryHandled = false;
        while (true)
        {
            if (!entryHandled)
            {
                entryHandled = true;
                if (input[ip] > 17)
                {
                    var entryT = (uint)(input[ip++] - 17);
                    if (entryT < 4)
                    {
                        state = entryT;
                        var entryMatch = MatchNext();
                        if (entryMatch != LzoStatus.Ok)
                        {
                            return (entryMatch, op);
                        }

                        continue;
                    }

                    var entryLiteral = CopyLiteralRun(entryT);
                    if (entryLiteral != LzoStatus.Ok)
                    {
                        return (entryLiteral, op);
                    }

                    continue;
                }
            }

            if (ip >= ipEnd)
            {
                return (LzoStatus.InputOverrun, op);
            }

            var t = (uint)input[ip++];
            var mPosShared = 0;
            if (t < 16)
            {
                if (state == 0)
                {
                    if (t == 0)
                    {
                        var ipLast = ip;
                        while (ip < ipEnd && input[ip] == 0)
                        {
                            ip++;
                        }

                        if (ip >= ipEnd)
                        {
                            return (LzoStatus.InputOverrun, op);
                        }

                        var offset = (long)(ip - ipLast);
                        if (offset > Max255Count)
                        {
                            return (LzoStatus.Error, op);
                        }

                        offset = (offset << 8) - offset;
                        t += (uint)(offset + 15 + input[ip++]);
                    }

                    t += 3;
                    var literal = CopyLiteralRun(t);
                    if (literal != LzoStatus.Ok)
                    {
                        return (literal, op);
                    }

                    continue;
                }

                if (state != 4)
                {
                    next = t & 3;
                    var mPos = op - 1 - (int)(t >> 2) - (input[ip++] << 2);
                    if (mPos < 0)
                    {
                        return (LzoStatus.LookbehindOverrun, op);
                    }

                    if (opEnd - op < 2)
                    {
                        return (LzoStatus.OutputOverrun, op);
                    }

                    output[op++] = output[mPos];
                    output[op++] = output[mPos + 1];
                    state = next;
                    var matchNext = MatchNext();
                    if (matchNext != LzoStatus.Ok)
                    {
                        return (matchNext, op);
                    }

                    continue;
                }

                next = t & 3;
                mPosShared = op - (1 + M2MaxOffset) - (int)(t >> 2) - (input[ip++] << 2);
                t = 3;
            }
            else if (t >= 64)
            {
                if (ip >= ipEnd)
                {
                    return (LzoStatus.InputOverrun, op);
                }

                next = t & 3;
                var mPosM3 = op - 1 - (int)((t >> 2) & 7) - (input[ip++] << 3);
                t = (t >> 5) - 1 + 2;
                var m3Match = CopyMatch(mPosM3, t);
                if (m3Match != LzoStatus.Ok)
                {
                    return (m3Match, op);
                }

                state = next;
                var m3Next = MatchNext();
                if (m3Next != LzoStatus.Ok)
                {
                    return (m3Next, op);
                }

                continue;
            }
            else if (t >= 32)
            {
                t = (t & 31) + 2;
                if (t == 2)
                {
                    var ipLast = ip;
                    while (ip < ipEnd && input[ip] == 0)
                    {
                        ip++;
                    }

                    if (ip >= ipEnd)
                    {
                        return (LzoStatus.InputOverrun, op);
                    }

                    var offset = (long)(ip - ipLast);
                    if (offset > Max255Count)
                    {
                        return (LzoStatus.Error, op);
                    }

                    offset = (offset << 8) - offset;
                    t += (uint)(offset + 31 + input[ip++]);
                    if (ip + 2 > ipEnd)
                    {
                        return (LzoStatus.InputOverrun, op);
                    }
                }

                if (ip + 2 > ipEnd)
                {
                    return (LzoStatus.InputOverrun, op);
                }

                var mPosM3Long = op - 1;
                next = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(ip, 2));
                ip += 2;
                mPosM3Long -= (int)(next >> 2);
                next &= 3;
                var m3LongMatch = CopyMatch(mPosM3Long, t);
                if (m3LongMatch != LzoStatus.Ok)
                {
                    return (m3LongMatch, op);
                }

                state = next;
                var m3LongNext = MatchNext();
                if (m3LongNext != LzoStatus.Ok)
                {
                    return (m3LongNext, op);
                }

                continue;
            }
            else
            {
                if (ip + 2 > ipEnd)
                {
                    return (LzoStatus.InputOverrun, op);
                }

                next = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(ip, 2));
                if ((next & 0xfffc) == 0xfffc && (t & 0xf8) == 0x18 && bitstreamVersion != 0)
                {


                    if (ip + 3 > ipEnd)
                    {
                        return (LzoStatus.InputOverrun, op);
                    }

                    t &= 7;
                    t |= (uint)input[ip + 2] << 3;
                    t += MinZeroRunLength;
                    if (t > (uint)(opEnd - op))
                    {
                        return (LzoStatus.OutputOverrun, op);
                    }

                    Array.Clear(output, op, (int)t);
                    op += (int)t;
                    next &= 3;
                    ip += 3;
                    state = next;
                    var zeroNext = MatchNext();
                    if (zeroNext != LzoStatus.Ok)
                    {
                        return (zeroNext, op);
                    }

                    continue;
                }

                var mPosM4 = op - (int)((t & 8) << 11);
                t = (t & 7) + 2;
                if (t == 2)
                {
                    var ipLast = ip;
                    while (ip < ipEnd && input[ip] == 0)
                    {
                        ip++;
                    }

                    if (ip >= ipEnd)
                    {
                        return (LzoStatus.InputOverrun, op);
                    }

                    var offset = (long)(ip - ipLast);
                    if (offset > Max255Count)
                    {
                        return (LzoStatus.Error, op);
                    }

                    offset = (offset << 8) - offset;
                    t += (uint)(offset + 7 + input[ip++]);
                    if (ip + 2 > ipEnd)
                    {
                        return (LzoStatus.InputOverrun, op);
                    }

                    next = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(ip, 2));
                }

                ip += 2;
                mPosM4 -= (int)(next >> 2);
                next &= 3;
                if (mPosM4 == op)
                {


                    if (t != 3)
                    {
                        return (LzoStatus.Error, op);
                    }

                    return (ip == ipEnd ? LzoStatus.Ok : LzoStatus.InputNotConsumed, op);
                }

                mPosM4 -= 0x4000;
                var m4Match = CopyMatch(mPosM4, t);
                if (m4Match != LzoStatus.Ok)
                {
                    return (m4Match, op);
                }

                state = next;
                var m4Next = MatchNext();
                if (m4Next != LzoStatus.Ok)
                {
                    return (m4Next, op);
                }

                continue;
            }


            var m2Match = CopyMatch(mPosShared, t);
            if (m2Match != LzoStatus.Ok)
            {
                return (m2Match, op);
            }

            state = next;
            var m2Next = MatchNext();
            if (m2Next != LzoStatus.Ok)
            {
                return (m2Next, op);
            }
        }
    }

    /// <summary>
    /// Decompresses requiring an exact output size; any failure (including a size mismatch) throws InvalidDataException.
    /// <para>解压并要求精确的输出大小；任何失败（含长度不符）抛出 InvalidDataException。</para>
    /// </summary>
    /// <param name="input">LZO1X bitstream to decode, treated as untrusted; an empty or otherwise
    /// sub-three-byte input fails with <see cref="LzoStatus.InputOverrun"/> even when
    /// <paramref name="expectedSize"/> is <c>0</c>.
    /// <para>待解码的 LZO1X 位流，按不可信输入处理；空输入或不足三字节的输入都会以
    /// <see cref="LzoStatus.InputOverrun"/> 失败，即使 <paramref name="expectedSize"/> 为 <c>0</c>。</para></param>
    /// <param name="expectedSize">Exact decompressed size in bytes; a buffer of this length is allocated
    /// up front and a produced byte count that differs from it is a failure.
    /// <para>精确的解压后大小（字节）；会预先分配该长度的缓冲区，实际产出字节数与之不同即视为失败。</para></param>
    /// <returns>The fully filled buffer of <paramref name="expectedSize"/> bytes.
    /// <para>填满的缓冲区，长度为 <paramref name="expectedSize"/> 字节。</para></returns>
    /// <exception cref="InvalidDataException">The bitstream failed to decode, or the decoded byte count
    /// differs from <paramref name="expectedSize"/>.
    /// <para>位流解码失败，或解码得到的字节数与 <paramref name="expectedSize"/> 不一致。</para></exception>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int expectedSize)
    {
        var result = new byte[expectedSize];
        var status = TryDecompress(input, result, out var written);
        if (status != LzoStatus.Ok)
        {
            throw new InvalidDataException(
                $"LZO1X decompression failed ({status}, wrote {written} of {expectedSize} bytes).  LZO1X 解压失败（{status}，写出 {written}/{expectedSize} 字节）。");
        }

        if (written != expectedSize)
        {
            throw new InvalidDataException(
                $"LZO1X decompression produced {written} bytes but {expectedSize} were expected.  LZO1X 解压输出 {written} 字节，期望 {expectedSize} 字节。");
        }

        return result;
    }

    /// <summary>
    /// Compresses the input with lzo1x_1 (bitstream version 0).
    /// <para>以 lzo1x_1（位流版本 0）压缩输入。</para>
    /// </summary>
    /// <param name="input">Uncompressed bytes to encode with the lzo1x_1 variant; an empty span is valid
    /// and yields only the three-byte end marker. The span length also sizes the intermediate worst-case
    /// buffer as <c>length + length / 16 + 64 + 3</c> bytes.
    /// <para>要以 lzo1x_1 变体编码的未压缩字节；空区间合法且仅产出三字节的结束标记。区间长度同时按
    /// <c>长度 + 长度 / 16 + 64 + 3</c> 字节估算中间的 worst-case 缓冲区。</para></param>
    /// <returns>The LZO1X bitstream, trimmed to the number of bytes the encoder wrote.
    /// <para>LZO1X 位流，已按编码器实际写出的字节数裁剪。</para></returns>
    /// <exception cref="InvalidOperationException">The encoder reported a status other than
    /// <see cref="LzoStatus.Ok"/>. <para>编码器返回了 <see cref="LzoStatus.Ok"/> 之外的状态。</para></exception>
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        var worst = input.Length + (input.Length / 16) + 64 + 3;
        var output = new byte[worst];
        var status = TryCompress(input, output, out var written);
        if (status != LzoStatus.Ok)
        {
            throw new InvalidOperationException(
                $"LZO1X compression failed ({status}).  LZO1X 压缩失败（{status}）。");
        }

        return output.AsSpan(0, written).ToArray();
    }

    private static LzoStatus TryCompress(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        var inSpan = input;
        var outSpan = output;
        bytesWritten = 0;
        var inLen = inSpan.Length;
        var op = 0;
        var opEnd = outSpan.Length;
        var dataStart = op;
        const int m4MaxOffset = 0xbfff;


        const int stateOffset = -2;
        var hasOpcode = false;

        var ip = 0;
        var l = inLen;
        var ti = 0;
        var litStart = 0;

        var dict = new ushort[DSize];
        while (l > 20)
        {
            var ll = Math.Min(l, m4MaxOffset + 1);


            Array.Clear(dict, 0, dict.Length);
            var status = CompressBlock(ip, ll, dict, inSpan, outSpan, ref ti, ref litStart);
            if (status != LzoStatus.Ok)
            {
                return status;
            }

            ip += ll;
            l -= ll;
        }

        var finalT = ti + l;
        if (finalT > 0)
        {
            var ii = inLen - finalT;
            if (op == dataStart && finalT <= 238)
            {
                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                outSpan[op++] = (byte)(17 + finalT);
            }
            else if (finalT <= 3)
            {
                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                outSpan[op + stateOffset] |= (byte)finalT;
            }
            else if (finalT <= 18)
            {
                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                outSpan[op++] = (byte)(finalT - 3);
            }
            else
            {
                var tt = finalT - 18;
                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                outSpan[op++] = 0;
                while (tt > 255)
                {
                    tt -= 255;
                    if (op + 1 > opEnd)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    outSpan[op++] = 0;
                }

                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                outSpan[op++] = (byte)tt;
            }

            if (finalT > opEnd - op)
            {
                return LzoStatus.OutputOverrun;
            }

            inSpan.Slice(ii, finalT).CopyTo(outSpan.Slice(op));
            op += finalT;
        }

        if (op + 3 > opEnd)
        {
            return LzoStatus.OutputOverrun;
        }

        outSpan[op++] = M4Marker | 1;
        outSpan[op++] = 0;
        outSpan[op++] = 0;

        bytesWritten = op;
        return LzoStatus.Ok;

        LzoStatus CompressBlock(int blockStart, int blockLen, ushort[] dict, ReadOnlySpan<byte> inSpan,
            Span<byte> outSpan, ref int pendingTi, ref int literalStart)
        {
            var inEnd = blockStart + blockLen;
            var ipEnd = inEnd - 20;


            var ii = blockStart;
            var ti = pendingTi;
            var blockIp = blockStart + (ti < 4 ? 4 - ti : 0);

            while (true)
            {


                blockIp += 1 + ((blockIp - ii) >> 5);
                if (blockIp >= ipEnd)
                {
                    break;
                }

                var dv = BinaryPrimitives.ReadUInt32LittleEndian(inSpan.Slice(blockIp, 4));
                var h = (int)(((dv * 0x1824429dU) >> (32 - DBits)) & DMask);
                var mPos = blockStart + dict[h];
                dict[h] = (ushort)(blockIp - blockStart);
                if (dv != BinaryPrimitives.ReadUInt32LittleEndian(inSpan.Slice(mPos, 4)))
                {
                    continue;
                }


                ii -= ti;
                ti = 0;
                var litLen = blockIp - ii;
                if (litLen != 0)
                {
                    if (litLen <= 3)
                    {
                        if (!hasOpcode)
                        {


                            if (op + 1 > opEnd)
                            {
                                return LzoStatus.OutputOverrun;
                            }

                            outSpan[op++] = (byte)(17 + litLen);
                        }
                        else
                        {
                            outSpan[op + stateOffset] |= (byte)litLen;
                        }
                    }
                    else if (litLen <= 18)
                    {
                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        outSpan[op++] = (byte)(litLen - 3);
                    }
                    else
                    {
                        var tt = litLen - 18;
                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        outSpan[op++] = 0;
                        while (tt > 255)
                        {
                            tt -= 255;
                            if (op + 1 > opEnd)
                            {
                                return LzoStatus.OutputOverrun;
                            }

                            outSpan[op++] = 0;
                        }

                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        outSpan[op++] = (byte)tt;
                    }

                    if (litLen > opEnd - op)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    inSpan.Slice(ii, litLen).CopyTo(outSpan.Slice(op));
                    op += litLen;
                }


                var mLen = 4;
                while (blockIp + mLen < ipEnd && inSpan[blockIp + mLen] == inSpan[mPos + mLen])
                {
                    mLen++;
                }

                var mOff = blockIp - mPos;
                blockIp += mLen;

                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                if (mLen <= M2MaxLen && mOff <= M2MaxOffset)
                {
                    var off = mOff - 1;
                    if (op + 2 > opEnd)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    outSpan[op++] = (byte)(((mLen - 1) << 5) | ((off & 7) << 2));
                    outSpan[op++] = (byte)(off >> 3);
                }
                else if (mOff <= M3MaxOffset)
                {
                    var off = mOff - 1;
                    if (mLen <= M3MaxLen)
                    {
                        outSpan[op++] = (byte)(M3Marker | (mLen - 2));
                    }
                    else
                    {
                        var rem = mLen - M3MaxLen;
                        outSpan[op++] = M3Marker;
                        while (rem > 255)
                        {
                            rem -= 255;
                            if (op + 1 > opEnd)
                            {
                                return LzoStatus.OutputOverrun;
                            }

                            outSpan[op++] = 0;
                        }

                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        outSpan[op++] = (byte)rem;
                    }

                    if (op + 2 > opEnd)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    outSpan[op++] = (byte)(off << 2);
                    outSpan[op++] = (byte)(off >> 6);
                }
                else
                {
                    var off = mOff - 0x4000;
                    if (mLen <= M4MaxLen)
                    {
                        outSpan[op++] = (byte)(M4Marker | ((off >> 11) & 8) | (mLen - 2));
                    }
                    else
                    {
                        outSpan[op++] = (byte)(M4Marker | ((off >> 11) & 8));
                        var rem = mLen - M4MaxLen;
                        while (rem > 255)
                        {
                            rem -= 255;
                            if (op + 1 > opEnd)
                            {
                                return LzoStatus.OutputOverrun;
                            }

                            outSpan[op++] = 0;
                        }

                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        outSpan[op++] = (byte)rem;
                    }

                    if (op + 2 > opEnd)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    outSpan[op++] = (byte)(off << 2);
                    outSpan[op++] = (byte)(off >> 6);
                }

                hasOpcode = true;
                ii = blockIp;
            }

            pendingTi = inEnd - (ii - ti);
            literalStart = ii - ti;
            return LzoStatus.Ok;
        }
    }
}