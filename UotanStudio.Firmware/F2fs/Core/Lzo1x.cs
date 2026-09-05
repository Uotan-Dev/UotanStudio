using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// LZO1X 解压结果状态。
/// <para>LZO1X decompression result status.</para>
/// </summary>
public enum LzoStatus : byte
{
    /// <summary>成功。<para>Success.</para></summary>
    Ok,

    /// <summary>输入流未被完整消费（EOF 标记后仍有数据）。<para>The input was not fully consumed (data remains after the EOF marker).</para></summary>
    InputNotConsumed,

    /// <summary>输入越界。<para>The input was overrun.</para></summary>
    InputOverrun,

    /// <summary>输出越界。<para>The output was overrun.</para></summary>
    OutputOverrun,

    /// <summary>回溯越界（匹配偏移指向输出起点之前）。<para>A match points before the start of the output.</para></summary>
    LookbehindOverrun,

    /// <summary>数据非法。<para>The data is invalid.</para></summary>
    Error,
}

/// <summary>
/// LZO1X 纯托管实现（解压对齐 Linux 内核 <c>lzo1x_decompress_safe</c>，压缩对齐
/// <c>lzo1x_1_compress</c>，位流版本 0）。f2fs 的 LZO（算法 2）与 LZO-RLE（算法 3）
/// 压缩簇都经 <c>lzo1x_decompress_safe</c> 解压，因此本实现的解压侧同时覆盖两种算法；
/// 压缩侧输出标准 LZO1X 位流（无 bitstream 版本前缀），可被内核与 f2fs-tools 直接读取。
/// 全部实现无反射与 P/Invoke，AOT 兼容。
/// <para>
/// Fully managed LZO1X implementation: decompression mirrors the Linux kernel
/// <c>lzo1x_decompress_safe</c> and compression mirrors <c>lzo1x_1_compress</c> (bitstream
/// version 0). f2fs decompresses both LZO (algorithm 2) and LZO-RLE (algorithm 3) clusters
/// through <c>lzo1x_decompress_safe</c>, so the decompressor covers both; the compressor emits
/// a standard LZO1X bitstream (no bitstream-version prefix) that the kernel and f2fs-tools read
/// directly. No reflection or P/Invoke; AOT compatible.</para>
/// </summary>
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
    /// 解压 LZO1X 位流到 <paramref name="output"/>，带完整边界检查（对齐内核 safe 变体）。
    /// <para>Decompresses an LZO1X bitstream into <paramref name="output"/> with full bounds
    /// checking (mirroring the kernel's safe variant).</para>
    /// </summary>
    /// <param name="input">压缩位流。<para>The compressed bitstream.</para></param>
    /// <param name="output">输出缓冲区。<para>The output buffer.</para></param>
    /// <param name="bytesWritten">实际写出的字节数。<para>The number of bytes actually written.</para></param>
    /// <returns>解压状态。<para>The decompression status.</para></returns>
    public static LzoStatus TryDecompress(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        bytesWritten = 0;

        // 局部函数不能捕获 ref struct（CS9108）：核心逻辑改用数组承载输入输出，
        // 成功时再一次性拷回输出 span。位流与簇数据均为 ≤ 64 KB 的小缓冲。
        // Local functions cannot capture ref structs (CS9108): the core runs on arrays and
        // copies back once on success. Both the bitstream and clusters are ≤ 64 KB buffers.
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

        // 内核位流版本前缀：首字节 17 标记带零游程编码的位流（LZO-RLE 写入器使用）。
        // Kernel bitstream-version prefix: a leading 17 marks a zero-run-encoded bitstream
        // (produced by the LZO-RLE writer).
        if (inLen >= 5 && input[0] == 17)
        {
            bitstreamVersion = input[1];
            ip = 2;
        }

        // 字面量拷贝：拷贝 t 字节后 state 置 4（对齐内核 copy_literal_run）。
        // Literal copy: copies t bytes then sets state to 4 (kernel copy_literal_run).
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

        // match_next：把 state 个随后字节拷入输出（对齐内核 match_next；之后主循环继续）。
        // match_next: copies the following state bytes into the output (kernel match_next;
        // the main loop continues afterwards).
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

        // 匹配拷贝：t ≥ 2（所有匹配分支的最小长度都是 2）。
        // Match copy: t ≥ 2 (every match branch has a minimum length of 2).
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

        // 入口分支（*ip > 17）与主循环共用：内核入口 goto 到 copy_literal_run / match_next
        // 后同样回到 for(;;) 主循环。
        // The entry branch (*ip > 17) flows back into the main loop, like the kernel's goto
        // copy_literal_run / match_next from the entry.
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
                    // LZO-RLE 零游程：原样置零一段输出。
                    // LZO-RLE zero run: clears a stretch of the output with zeros.
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
                    // EOF 标记：t 必须为 3 且输入恰好耗尽。
                    // EOF marker: t must be 3 and the input must be fully consumed.
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

            // state==4 且 t<16 的 M2 分支：t 已置 3，落入共享匹配拷贝。
            // The state==4 M2 branch with t<16: t is 3 here, falling through to the shared
            // match copy.
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
    /// 解压并要求精确的输出大小；任何失败（含长度不符）抛出 <see cref="InvalidDataException"/>。
    /// <para>Decompresses requiring an exact output size; any failure (including a size mismatch)
    /// throws <see cref="InvalidDataException"/>.</para>
    /// </summary>
    /// <param name="input">压缩位流。<para>The compressed bitstream.</para></param>
    /// <param name="expectedSize">期望输出字节数。<para>The expected output size in bytes.</para></param>
    /// <returns>解压后的数据。<para>The decompressed data.</para></returns>
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
    /// 以 lzo1x_1（位流版本 0）压缩输入。输出保证可被 <see cref="TryDecompress"/> 与
    /// 内核/f2fs-tools 的 LZO1X 解压器读取。
    /// <para>Compresses the input with lzo1x_1 (bitstream version 0). The output is guaranteed
    /// readable by <see cref="TryDecompress"/> and by the kernel/f2fs-tools LZO1X decompressors.</para>
    /// </summary>
    /// <param name="input">原始数据。<para>The raw data.</para></param>
    /// <returns>压缩位流。<para>The compressed bitstream.</para></returns>
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
        const int m4MaxOffset = 0xbfff; // M4_MAX_OFFSET_V0（位流版本 0）。(M4_MAX_OFFSET_V0 for bitstream version 0.)

        // stateOffset = -2：上一条匹配指令的操作码字节位于 op-2。
        // stateOffset = -2: the previous match opcode byte sits at op-2.
        const int stateOffset = -2;
        var hasOpcode = false;

        var ip = 0;
        var l = inLen;
        var ti = 0; // 上一块遗留的待发字面量字节数。Pending literal bytes carried from the previous block.
        var litStart = 0; // 待发字面量的绝对起点。Absolute start of the pending literal.

        while (l > 20)
        {
            var ll = Math.Min(l, m4MaxOffset + 1);
            // 内核在每个外层块处理前清空哈希表：字典条目按块起始相对存储，跨块复用会指向错误位置。
            // The kernel clears the hash table before each outer block: entries are block-relative,
            // so reusing them across blocks would produce wrong matches.
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
            // 与内核一致：ii 起始于块首，首次命中时执行 ii -= ti 重定基到跨块字面量的绝对起点。
            // As in the kernel: ii starts at the block head; the first match applies the
            // ii -= ti rebase to the absolute start of the cross-block pending literal.
            var ii = blockStart;
            var ti = pendingTi;
            var blockIp = blockStart + (ti < 4 ? 4 - ti : 0);

            while (true)
            {
                // literal 步进：随字面量长度指数加速。
                // literal step: accelerate with the pending literal length.
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

                // 命中：先发字面量（含上一块遗留的 ti 字节）。
                // Match hit: emit the literal first (including the previous block's ti bytes).
                ii -= ti;
                ti = 0;
                var litLen = blockIp - ii;
                if (litLen != 0)
                {
                    if (litLen <= 3)
                    {
                        if (!hasOpcode)
                        {
                            // 首个操作码之前不可能出现 t≤3 的字面量（入口步进保证 ≥4）；
                            // 保险起见按首字面量编码。
                            // A literal of ≤3 cannot precede the first opcode (the entry step
                            // guarantees ≥4); encode as a leading literal for safety.
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

                // 计算匹配长度（上限 ipEnd，与内核一致）。
                // Compute the match length (capped at ipEnd, as in the kernel).
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
