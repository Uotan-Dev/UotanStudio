using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

public enum LzoStatus : byte
{
    Ok,

    InputNotConsumed,

    InputOverrun,

    OutputOverrun,

    LookbehindOverrun,

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
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        var worst = input.Length + (input.Length / 16) + 64 + 3;
        var output = new byte[worst];
        var status = TryCompress(input.ToArray(), output, out var written);
        if (status != LzoStatus.Ok)
        {
            throw new InvalidOperationException(
                $"LZO1X compression failed ({status}).  LZO1X 压缩失败（{status}）。");
        }

        return output.AsSpan(0, written).ToArray();
    }

    private static LzoStatus TryCompress(byte[] input, byte[] output, out int bytesWritten)
    {
        bytesWritten = 0;
        var inLen = input.Length;
        var op = 0;
        var opEnd = output.Length;
        var dataStart = op;
        const int m4MaxOffset = 0xbfff;


        const int stateOffset = -2;
        var hasOpcode = false;

        var ip = 0;
        var l = inLen;
        var ti = 0;
        var litStart = 0;

        while (l > 20)
        {
            var ll = Math.Min(l, m4MaxOffset + 1);


            var status = CompressBlock(ip, ll, new ushort[DSize], ref ti, ref litStart);
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

                output[op++] = (byte)(17 + finalT);
            }
            else if (finalT <= 3)
            {
                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                output[op + stateOffset] |= (byte)finalT;
            }
            else if (finalT <= 18)
            {
                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                output[op++] = (byte)(finalT - 3);
            }
            else
            {
                var tt = finalT - 18;
                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                output[op++] = 0;
                while (tt > 255)
                {
                    tt -= 255;
                    if (op + 1 > opEnd)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    output[op++] = 0;
                }

                if (op + 1 > opEnd)
                {
                    return LzoStatus.OutputOverrun;
                }

                output[op++] = (byte)tt;
            }

            if (finalT > opEnd - op)
            {
                return LzoStatus.OutputOverrun;
            }

            Array.Copy(input, ii, output, op, finalT);
            op += finalT;
        }

        if (op + 3 > opEnd)
        {
            return LzoStatus.OutputOverrun;
        }

        output[op++] = M4Marker | 1;
        output[op++] = 0;
        output[op++] = 0;

        bytesWritten = op;
        return LzoStatus.Ok;

        LzoStatus CompressBlock(int blockStart, int blockLen, ushort[] dict, ref int pendingTi, ref int literalStart)
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

                var dv = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(blockIp, 4));
                var h = (int)(((dv * 0x1824429dU) >> (32 - DBits)) & DMask);
                var mPos = blockStart + dict[h];
                dict[h] = (ushort)(blockIp - blockStart);
                if (dv != BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(mPos, 4)))
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

                            output[op++] = (byte)(17 + litLen);
                        }
                        else
                        {
                            output[op + stateOffset] |= (byte)litLen;
                        }
                    }
                    else if (litLen <= 18)
                    {
                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        output[op++] = (byte)(litLen - 3);
                    }
                    else
                    {
                        var tt = litLen - 18;
                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        output[op++] = 0;
                        while (tt > 255)
                        {
                            tt -= 255;
                            if (op + 1 > opEnd)
                            {
                                return LzoStatus.OutputOverrun;
                            }

                            output[op++] = 0;
                        }

                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        output[op++] = (byte)tt;
                    }

                    if (litLen > opEnd - op)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    Array.Copy(input, ii, output, op, litLen);
                    op += litLen;
                }


                var mLen = 4;
                while (blockIp + mLen < ipEnd && input[blockIp + mLen] == input[mPos + mLen])
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

                    output[op++] = (byte)(((mLen - 1) << 5) | ((off & 7) << 2));
                    output[op++] = (byte)(off >> 3);
                }
                else if (mOff <= M3MaxOffset)
                {
                    var off = mOff - 1;
                    if (mLen <= M3MaxLen)
                    {
                        output[op++] = (byte)(M3Marker | (mLen - 2));
                    }
                    else
                    {
                        var rem = mLen - M3MaxLen;
                        output[op++] = M3Marker;
                        while (rem > 255)
                        {
                            rem -= 255;
                            if (op + 1 > opEnd)
                            {
                                return LzoStatus.OutputOverrun;
                            }

                            output[op++] = 0;
                        }

                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        output[op++] = (byte)rem;
                    }

                    if (op + 2 > opEnd)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    output[op++] = (byte)(off << 2);
                    output[op++] = (byte)(off >> 6);
                }
                else
                {
                    var off = mOff - 0x4000;
                    if (mLen <= M4MaxLen)
                    {
                        output[op++] = (byte)(M4Marker | ((off >> 11) & 8) | (mLen - 2));
                    }
                    else
                    {
                        output[op++] = (byte)(M4Marker | ((off >> 11) & 8));
                        var rem = mLen - M4MaxLen;
                        while (rem > 255)
                        {
                            rem -= 255;
                            if (op + 1 > opEnd)
                            {
                                return LzoStatus.OutputOverrun;
                            }

                            output[op++] = 0;
                        }

                        if (op + 1 > opEnd)
                        {
                            return LzoStatus.OutputOverrun;
                        }

                        output[op++] = (byte)rem;
                    }

                    if (op + 2 > opEnd)
                    {
                        return LzoStatus.OutputOverrun;
                    }

                    output[op++] = (byte)(off << 2);
                    output[op++] = (byte)(off >> 6);
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
