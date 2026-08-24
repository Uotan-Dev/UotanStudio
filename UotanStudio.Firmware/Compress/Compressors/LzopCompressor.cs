using System.Buffers.Binary;
using System.IO.Hashing;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// LZOP (lzop) file decompression: parses the lzop container header and inflates each block
/// with the LZO1X-1 decompressor (a faithful, bounds-checked port of minilzo's
/// <c>lzo1x_decompress_safe</c>). SharpZipLib provides no LZO support, so the LZO1X decoder is
/// implemented here in pure managed code.
/// <para>LZOP（lzop）文件解压：解析 lzop 容器头，并用 LZO1X-1 解压器（minilzo
/// <c>lzo1x_decompress_safe</c> 的带边界检查的忠实移植）逐块解压。SharpZipLib 不提供 LZO
/// 支持，因此 LZO1X 解码器在此以纯托管代码实现。</para>
/// </summary>
public static class LzopCompressor
{
    /// <summary>LZOP magic bytes. LZOP 魔数。</summary>
    public static ReadOnlySpan<byte> Magic => [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int MagicSize = 9;
    private const int M2MaxOffset = 0x0800;

    // lzop header flag bits.
    private const uint FAdler32U = 0x00000001;
    private const uint FAdler32C = 0x00000002;
    private const uint FHCrc32 = 0x00001000;
    private const uint FCrc32U = 0x00010000;
    private const uint FCrc32C = 0x00020000;
    private const uint FFilter = 0x00000800;

    /// <summary>
    /// Checks whether the data starts with the LZOP magic bytes.
    /// <para>检查数据是否以 LZOP 魔数开头。</para>
    /// </summary>
    /// <param name="data">The data to inspect. <para>待检测的数据。</para></param>
    /// <returns><c>true</c> when the magic matches. <para>魔数匹配时返回 <c>true</c>。</para></returns>
    public static bool IsLzopFormat(byte[]? data) => data is { Length: >= MagicSize } && data.AsSpan(0, MagicSize).SequenceEqual(Magic);

    /// <summary>
    /// Decompresses an LZOP file into its original bytes.
    /// <para>将 LZOP 文件解压为原始字节。</para>
    /// </summary>
    /// <param name="data">The LZOP container bytes. <para>LZOP 容器字节。</para></param>
    /// <returns>The decompressed original data. <para>解压后的原始数据。</para></returns>
    /// <exception cref="ArgumentNullException">data 为 null 时抛出。</exception>
    /// <exception cref="CompressionException">数据不是 LZOP、头校验失败或块解压失败时抛出。</exception>
    public static byte[] Decompress(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < MagicSize)
        {
            throw new CompressionException("Invalid LZOP data: too small 无效的 LZOP 数据：过小。");
        }

        if (!IsLzopFormat(data))
        {
            throw new CompressionException("Invalid LZOP data: wrong magic 无效的 LZOP 数据：魔数错误。");
        }

        try
        {
            var offset = MagicSize;

            // ---- Header ----
            if (offset + 2 > data.Length)
            {
                throw new CompressionException("Invalid LZOP header: truncated version 无效的 LZOP 头：版本截断。");
            }

            var version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            offset += 2;
            if (version < 0x0900)
            {
                throw new CompressionException($"Invalid LZOP header: unsupported version {version:X4} 无效的 LZOP 头：不支持的版本。");
            }

            offset += 2; // lib_version
            if (version >= 0x0940)
            {
                offset += 2; // version_needed_to_extract
            }

            if (offset + 1 > data.Length)
            {
                throw new CompressionException("Invalid LZOP header: truncated method 无效的 LZOP 头：方法字段截断。");
            }

            offset += 1; // method
            if (version >= 0x0940)
            {
                offset += 1; // level
            }

            if (offset + 4 > data.Length)
            {
                throw new CompressionException("Invalid LZOP header: truncated flags 无效的 LZOP 头：标志截断。");
            }

            var flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
            var useCrc32 = (flags & (FHCrc32 | FCrc32U | FCrc32C)) != 0;
            var hasUncompressedChecksum = (flags & (FAdler32U | FCrc32U)) != 0;
            var hasCompressedChecksum = (flags & (FAdler32C | FCrc32C)) != 0;

            if ((flags & FFilter) != 0)
            {
                offset += 4;
            }

            offset += 4; // mode
            offset += 4; // mtime_low
            if (version >= 0x0940)
            {
                offset += 4; // mtime_high
            }

            if (offset + 1 > data.Length)
            {
                throw new CompressionException("Invalid LZOP header: truncated name length 无效的 LZOP 头：文件名长度截断。");
            }

            var nameLen = data[offset++];
            if (offset + nameLen > data.Length)
            {
                throw new CompressionException("Invalid LZOP header: truncated name 无效的 LZOP 头：文件名截断。");
            }

            offset += nameLen;

            // ---- Header checksum (covers everything after the magic, excluding this field) ----
            if (offset + 4 > data.Length)
            {
                throw new CompressionException("Invalid LZOP header: truncated header checksum 无效的 LZOP 头：头校验和截断。");
            }

            var storedHeaderChecksum = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
            var headerLength = offset - MagicSize - 4;
            var computedHeaderChecksum = useCrc32
                ? Crc32Hash(data, MagicSize, headerLength)
                : Adler32(data, MagicSize, headerLength);
            if (storedHeaderChecksum != computedHeaderChecksum)
            {
                throw new CompressionException("Invalid LZOP data: header checksum mismatch 无效的 LZOP 数据：头校验和不匹配。");
            }

            using var output = new MemoryStream();

            // ---- Blocks ----
            while (offset + 4 <= data.Length)
            {
                var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (uncompressedSize == 0)
                {
                    break; // end marker
                }

                if (offset + 4 > data.Length)
                {
                    throw new CompressionException("Invalid LZOP data: truncated block header 无效的 LZOP 数据：块头截断。");
                }

                var compressedSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                offset += 4;

                uint uncompressedChecksum = 0;
                if (hasUncompressedChecksum)
                {
                    if (offset + 4 > data.Length)
                    {
                        throw new CompressionException("Invalid LZOP data: truncated uncompressed checksum 无效的 LZOP 数据：未压缩校验和截断。");
                    }

                    uncompressedChecksum = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                    offset += 4;
                }

                if (hasCompressedChecksum)
                {
                    offset += 4;
                }

                if (compressedSize > uncompressedSize)
                {
                    throw new CompressionException("Invalid LZOP data: compressed block larger than uncompressed 无效的 LZOP 数据：压缩块大于未压缩块。");
                }

                if (offset + (int)compressedSize > data.Length)
                {
                    throw new CompressionException("Invalid LZOP data: truncated block data 无效的 LZOP 数据：块数据截断。");
                }

                var blockOut = new byte[uncompressedSize];
                if (compressedSize == uncompressedSize)
                {
                    // Stored block.
                    data.AsSpan(offset, (int)uncompressedSize).CopyTo(blockOut);
                }
                else
                {
                    var decoded = Lzo1xDecompressSafe(data, offset, (int)compressedSize, blockOut);
                    if (decoded != (int)uncompressedSize)
                    {
                        throw new CompressionException(
                            $"Invalid LZOP data: block decompressed to {decoded} bytes, expected {uncompressedSize} 无效的 LZOP 数据：块解压结果长度不符。");
                    }
                }

                if (hasUncompressedChecksum)
                {
                    var actualChecksum = useCrc32
                        ? Crc32Hash(blockOut, 0, blockOut.Length)
                        : Adler32(blockOut, 0, blockOut.Length);
                    if (actualChecksum != uncompressedChecksum)
                    {
                        // Some lzop files store Adler32 even when CRC32 flags are set; try the other.
                        var otherChecksum = useCrc32
                            ? Adler32(blockOut, 0, blockOut.Length)
                            : Crc32Hash(blockOut, 0, blockOut.Length);
                        if (otherChecksum != uncompressedChecksum)
                        {
                            throw new CompressionException("Invalid LZOP data: uncompressed checksum mismatch 无效的 LZOP 数据：未压缩校验和不匹配。");
                        }
                    }
                }

                output.Write(blockOut, 0, blockOut.Length);
                offset += (int)compressedSize;
            }

            return output.ToArray();
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException($"LZOP decompression failed: {ex.Message} LZOP 解压失败。", ex);
        }
    }

    /// <summary>
    /// Safe LZO1X-1 decompression, ported from minilzo's <c>lzo1x_decompress_safe</c> with bounds checks.
    /// <para>安全的 LZO1X-1 解压，移植自 minilzo 的 <c>lzo1x_decompress_safe</c>，带边界检查。</para>
    /// </summary>
    private static int Lzo1xDecompressSafe(byte[] input, int inputStart, int inputLen, byte[] output)
    {
        var ip = inputStart;
        var ipEnd = inputStart + inputLen;
        var op = 0;
        var opEnd = output.Length;
        var t = 0;

        if (ip >= ipEnd)
        {
            throw new CompressionException("LZO1X: empty input LZO1X：输入为空。");
        }

        // First byte > 17 starts an initial literal run; otherwise it is consumed by the main loop.
        var first = input[ip];
        if (first > 17)
        {
            ip++;
            t = first - 17;
            if (t < 4)
            {
                goto MatchNext;
            }

            EnsureOutput(input, ip, op, t, opEnd, ipEnd);
            for (var i = 0; i < t; i++)
            {
                output[op++] = input[ip++];
            }

            goto FirstLiteralRun;
        }

        // ---- Main loop ----
    MainLoop:
        if (ip >= ipEnd)
        {
            throw new CompressionException("LZO1X: EOF marker not found LZO1X：未找到结束标记。");
        }

        t = input[ip++];
        if (t >= 16)
        {
            goto Match;
        }

        // Literal run (t < 16).
        if (t == 0)
        {
            if (ip >= ipEnd)
            {
                throw new CompressionException("LZO1X: input overrun LZO1X：输入越界。");
            }

            while (input[ip] == 0)
            {
                t += 255;
                ip++;
                if (ip >= ipEnd)
                {
                    throw new CompressionException("LZO1X: input overrun LZO1X：输入越界。");
                }
            }

            t += 15 + input[ip++];
        }

        if (op + t + 3 > opEnd || ip + t + 3 > ipEnd)
        {
            throw new CompressionException("LZO1X: overrun copying literals LZO1X：复制字面量越界。");
        }

        Copy4(output, op, input, ip);
        op += 4;
        ip += 4;
        if (--t > 0)
        {
            if (t >= 4)
            {
                do
                {
                    Copy4(output, op, input, ip);
                    op += 4;
                    ip += 4;
                    t -= 4;
                }
                while (t >= 4);
                if (t > 0)
                {
                    for (var i = 0; i < t; i++)
                    {
                        output[op++] = input[ip++];
                    }
                }
            }
            else
            {
                for (var i = 0; i < t; i++)
                {
                    output[op++] = input[ip++];
                }
            }
        }

    FirstLiteralRun:
        if (ip >= ipEnd)
        {
            throw new CompressionException("LZO1X: input overrun LZO1X：输入越界。");
        }

        t = input[ip++];
        if (t >= 16)
        {
            goto Match;
        }

        // Short M2 match (2-3 bytes).
        {
            var mPos = op - 1 - M2MaxOffset - (t >> 2) - (input[ip++] << 2);
            EnsureMatch(output, mPos, op, 3, opEnd);
            output[op++] = output[mPos++];
            output[op++] = output[mPos++];
            output[op++] = output[mPos];
        }
        goto MatchDone;

    Match:
        {
            var matchOffset = 0;
            if (t >= 64)
            {
                matchOffset = op - 1 - ((t >> 2) & 7) - (input[ip++] << 3);
                t = (t >> 5) - 1;
                EnsureMatch(output, matchOffset, op, t + 2, opEnd);
                goto CopyMatch;
            }

            if (t >= 32)
            {
                t &= 31;
                if (t == 0)
                {
                    if (ip >= ipEnd)
                    {
                        throw new CompressionException("LZO1X: input overrun LZO1X：输入越界。");
                    }

                    while (input[ip] == 0)
                    {
                        t += 255;
                        ip++;
                        if (ip >= ipEnd)
                        {
                            throw new CompressionException("LZO1X: input overrun LZO1X：输入越界。");
                        }
                    }

                    t += 31 + input[ip++];
                }

                matchOffset = op - 1 - ((input[ip] | (input[ip + 1] << 8)) >> 2);
                ip += 2;
            }
            else if (t >= 16)
            {
                matchOffset = op - ((t & 8) << 11);
                t &= 7;
                if (t == 0)
                {
                    if (ip >= ipEnd)
                    {
                        throw new CompressionException("LZO1X: input overrun LZO1X：输入越界。");
                    }

                    while (input[ip] == 0)
                    {
                        t += 255;
                        ip++;
                        if (ip >= ipEnd)
                        {
                            throw new CompressionException("LZO1X: input overrun LZO1X：输入越界。");
                        }
                    }

                    t += 7 + input[ip++];
                }

                // Little-endian 16-bit offset field.
                matchOffset -= (input[ip] | (input[ip + 1] << 8)) >> 2;
                ip += 2;
                if (matchOffset == op)
                {
                    return op;
                }

                matchOffset -= 0x4000;
            }
            else
            {
                // Very short match (2 bytes).
                matchOffset = op - 1 - (t >> 2) - (input[ip++] << 2);
                EnsureMatch(output, matchOffset, op, 2, opEnd);
                output[op++] = output[matchOffset++];
                output[op++] = output[matchOffset];
                goto MatchDone;
            }

            EnsureMatch(output, matchOffset, op, t + 2, opEnd);

        CopyMatch:
            output[op++] = output[matchOffset++];
            output[op++] = output[matchOffset++];
            for (var i = 0; i < t; i++)
            {
                output[op++] = output[matchOffset++];
            }
        }

    MatchDone:
        t = input[ip - 2] & 3;
        if (t == 0)
        {
            goto MainLoop;
        }

    MatchNext:
        if (op + t > opEnd || ip + t > ipEnd)
        {
            throw new CompressionException("LZO1X: literal overrun after match LZO1X：匹配后字面量越界。");
        }

        if (t > 0)
        {
            output[op++] = input[ip++];
        }

        if (t > 1)
        {
            output[op++] = input[ip++];
        }

        if (t > 2)
        {
            output[op++] = input[ip++];
        }

        if (ip >= ipEnd)
        {
            throw new CompressionException("LZO1X: unexpected end before next instruction LZO1X：下一条指令前意外结束。");
        }

        t = input[ip++];
        goto Match;
    }

    private static void EnsureOutput(byte[] input, int ip, int op, int count, int opEnd, int ipEnd)
    {
        if (op + count > opEnd || ip + count > ipEnd)
        {
            throw new CompressionException("LZO1X: output/input overrun LZO1X：输出/输入越界。");
        }
    }

    private static void EnsureMatch(byte[] output, int matchOffset, int op, int length, int opEnd)
    {
        if (matchOffset < 0 || op + length > opEnd)
        {
            throw new CompressionException("LZO1X: lookbehind/output overrun LZO1X：回看/输出越界。");
        }
    }

    private static void Copy4(byte[] dst, int dstOffset, byte[] src, int srcOffset)
    {
        dst[dstOffset] = src[srcOffset];
        dst[dstOffset + 1] = src[srcOffset + 1];
        dst[dstOffset + 2] = src[srcOffset + 2];
        dst[dstOffset + 3] = src[srcOffset + 3];
    }

    private static uint Adler32(byte[] data, int offset, int length)
    {
        const uint modAdler = 65521;
        uint a = 1;
        uint b = 0;
        for (var i = 0; i < length; i++)
        {
            a = (a + data[offset + i]) % modAdler;
            b = (b + a) % modAdler;
        }

        return (b << 16) | a;
    }

    private static uint Crc32Hash(byte[] data, int offset, int length)
        => Crc32.HashToUInt32(data.AsSpan(offset, length));
}
