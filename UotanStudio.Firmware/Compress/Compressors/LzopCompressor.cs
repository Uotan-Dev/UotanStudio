using System.Buffers.Binary;
using System.IO.Hashing;
using UotanStudio.Firmware.F2fs;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// LZOP (lzop) file compression and decompression: writes/parses the lzop container header and
/// compresses/inflates each 256 KiB block with the LZO1X-1 codec (a faithful, bounds-checked
/// port of minilzo's <c>lzo1x_1_compress</c> / <c>lzo1x_decompress_safe</c>). SharpZipLib
/// provides no LZO support, so the LZO1X codec is implemented here in pure managed code.
/// <para>LZOP（lzop）文件压缩/解压：写/解析 lzop 容器头，并用 LZO1X-1 编解码器（minilzo
/// <c>lzo1x_1_compress</c> / <c>lzo1x_decompress_safe</c> 的带边界检查的忠实移植）按 256 KiB
/// 块压缩/解压。SharpZipLib 不提供 LZO 支持，因此 LZO1X 编解码器在此以纯托管代码实现。</para>
/// </summary>
public static class LzopCompressor
{
    /// <summary>LZOP magic bytes. LZOP 魔数。</summary>
    public static ReadOnlySpan<byte> Magic => [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int MagicSize = 9;
    // Supported interoperability ceiling; lzop's common 256 KiB block size is not a format limit.
    private const uint MaximumBlockSize = 64 * 1024 * 1024;
    private const int M2MaxOffset = 0x0800;

    // 压缩端约定：lzop 版本 0x0940（引入 extra 字段与 mtime_high），块大小 256 KiB。
    // Compressor conventions: lzop version 0x0940 (introducing the extra fields and mtime_high)
    // with lzop's conventional 256 KiB block size.
    private const ushort LzopVersion = 0x0940;
    private const int CompressBlockSize = 256 * 1024;
    // 头部内容长度：magic 之后、头校验和之前的所有字段（version 到 name_length，含 name 字节）。
    // Header content length: every field after the magic and before the header checksum
    // (from version through name_length, including any name bytes).
    private const int HeaderContentLength = 25;

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

    /// <summary>Decompresses an LZOP file into its original bytes.</summary>
    public static byte[] Decompress(byte[] data) => Decompress(data, long.MaxValue);

    /// <summary>Decompresses LZOP data while enforcing a total output limit.</summary>
    public static byte[] Decompress(byte[] data, long maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        Decompress(input, output, maximumOutputBytes);
        return output.ToArray();
    }

    /// <summary>逐块流式解压 LZOP 数据并限制累计输出。<para>Streams LZOP blocks to the output while enforcing a cumulative output limit.</para></summary>
    public static void Decompress(Stream input, Stream output, long maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);

        try
        {
            var magic = ReadBytes(input, MagicSize, "magic");
            if (!magic.AsSpan().SequenceEqual(Magic))
                throw new CompressionException("Invalid LZOP data: wrong magic 无效的 LZOP 数据：魔数错误。");

            using var header = new MemoryStream();
            ushort ReadHeaderUInt16()
            {
                var bytes = ReadBytes(input, 2, "header");
                header.Write(bytes);
                return BinaryPrimitives.ReadUInt16BigEndian(bytes);
            }
            uint ReadHeaderUInt32()
            {
                var bytes = ReadBytes(input, 4, "header");
                header.Write(bytes);
                return BinaryPrimitives.ReadUInt32BigEndian(bytes);
            }
            byte ReadHeaderByte()
            {
                var value = input.ReadByte();
                if (value < 0) throw new CompressionException("Invalid LZOP header: truncated 无效的 LZOP 头：截断。");
                header.WriteByte((byte)value);
                return (byte)value;
            }

            var version = ReadHeaderUInt16();
            if (version < 0x0900)
                throw new CompressionException($"Invalid LZOP header: unsupported version {version:X4}.");
            _ = ReadHeaderUInt16(); // library version
            if (version >= 0x0940) _ = ReadHeaderUInt16();
            _ = ReadHeaderByte(); // method
            if (version >= 0x0940) _ = ReadHeaderByte();
            var flags = ReadHeaderUInt32();
            if ((flags & FFilter) != 0) _ = ReadHeaderUInt32();
            _ = ReadHeaderUInt32(); // mode
            _ = ReadHeaderUInt32(); // mtime low
            if (version >= 0x0940) _ = ReadHeaderUInt32();
            var nameLength = ReadHeaderByte();
            header.Write(ReadBytes(input, nameLength, "file name"));

            var storedHeaderChecksum = ReadUInt32(input, "header checksum");
            var headerBytes = header.GetBuffer();
            var computedHeaderChecksum = (flags & FHCrc32) != 0
                ? Crc32Hash(headerBytes, 0, checked((int)header.Length))
                : Adler32(headerBytes, 0, checked((int)header.Length));
            if (storedHeaderChecksum != computedHeaderChecksum)
                throw new CompressionException("Invalid LZOP data: header checksum mismatch 无效的 LZOP 数据：头校验和不匹配。");

            var hasUncompressedAdler32 = (flags & FAdler32U) != 0;
            var hasUncompressedCrc32 = (flags & FCrc32U) != 0;
            var hasCompressedAdler32 = (flags & FAdler32C) != 0;
            var hasCompressedCrc32 = (flags & FCrc32C) != 0;
            long totalOutput = 0;
            while (true)
            {
                var uncompressedSize = ReadUInt32(input, "block size");
                if (uncompressedSize == 0) return;
                var compressedSize = ReadUInt32(input, "compressed block size");
                // 256 KiB is merely lzop's conventional block size, not a format limit. Apply
                // the supported ceiling and caller limit to both declarations before allocating
                // either the compressed or decompressed block buffer.
                if (compressedSize > uncompressedSize || compressedSize > MaximumBlockSize || uncompressedSize > MaximumBlockSize)
                    throw new CompressionException($"Invalid LZOP block size (supported maximum {MaximumBlockSize} bytes) 无效的 LZOP 块大小。");
                var remainingOutputBytes = maximumOutputBytes - totalOutput;
                if (compressedSize > remainingOutputBytes || uncompressedSize > remainingOutputBytes)
                    throw new CompressionException($"LZOP block exceeds the output limit of {maximumOutputBytes} bytes.");

                // Checksums occur independently in format order: uncompressed Adler/CRC,
                // followed by compressed Adler/CRC. A stored block may carry all four.
                var uncompressedAdler32 = hasUncompressedAdler32 ? ReadUInt32(input, "uncompressed Adler-32") : 0;
                var uncompressedCrc32 = hasUncompressedCrc32 ? ReadUInt32(input, "uncompressed CRC-32") : 0;
                var compressedAdler32 = hasCompressedAdler32 ? ReadUInt32(input, "compressed Adler-32") : 0;
                var compressedCrc32 = hasCompressedCrc32 ? ReadUInt32(input, "compressed CRC-32") : 0;
                var block = ReadBytes(input, (int)compressedSize, "block data");
                if (hasCompressedAdler32 && Adler32(block, 0, block.Length) != compressedAdler32)
                    throw new CompressionException("Invalid LZOP data: compressed Adler-32 mismatch.");
                if (hasCompressedCrc32 && Crc32Hash(block, 0, block.Length) != compressedCrc32)
                    throw new CompressionException("Invalid LZOP data: compressed CRC-32 mismatch.");

                byte[] decoded;
                if (compressedSize == uncompressedSize)
                {
                    decoded = block;
                }
                else
                {
                    decoded = new byte[(int)uncompressedSize];
                    var count = Lzo1xDecompressSafe(block, 0, block.Length, decoded);
                    if (count != decoded.Length)
                        throw new CompressionException($"Invalid LZOP data: block decompressed to {count} bytes, expected {uncompressedSize}.");
                }

                if (hasUncompressedAdler32 && Adler32(decoded, 0, decoded.Length) != uncompressedAdler32)
                    throw new CompressionException("Invalid LZOP data: uncompressed Adler-32 mismatch.");
                if (hasUncompressedCrc32 && Crc32Hash(decoded, 0, decoded.Length) != uncompressedCrc32)
                    throw new CompressionException("Invalid LZOP data: uncompressed CRC-32 mismatch.");
                output.Write(decoded);
                totalOutput = checked(totalOutput + decoded.Length);
            }
        }
        catch (CompressionException) { throw; }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OverflowException)
        {
            throw new CompressionException($"LZOP decompression failed: {ex.Message} LZOP 解压失败。", ex);
        }
    }

    // Compression / 压缩端

    /// <summary>
    /// 将数据压缩为 LZOP（lzop）格式的字节数组。
    /// <para>Compresses data into an LZOP (lzop) byte array.</para>
    /// </summary>
    /// <param name="data">待压缩的数据。<para>The data to compress.</para></param>
    /// <returns>lzop 容器字节流。<para>The lzop container byte stream.</para></returns>
    /// <exception cref="ArgumentNullException">data 为 null 时抛出。<para>Thrown when data is null.</para></exception>
    /// <exception cref="CompressionException">压缩失败时抛出。<para>Thrown when compression fails.</para></exception>
    public static byte[] Compress(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        Compress(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// 将输入流按 256 KiB 块压缩为 LZOP（lzop）格式并写入输出流。大输入按块流式处理，
    /// 内存占用与块大小成正比，不将整个文件读入内存。
    /// <para>Compresses the input stream into the LZOP (lzop) format in 256 KiB blocks. Large
    /// inputs stream block by block with memory proportional to the block size; the whole file is
    /// never buffered in memory.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入 lzop 数据的输出流。<para>The output stream receiving lzop data.</para></param>
    /// <exception cref="ArgumentNullException">input 或 output 为 null 时抛出。<para>Thrown when input or output is null.</para></exception>
    /// <exception cref="CompressionException">压缩失败时抛出。<para>Thrown when compression fails.</para></exception>
    public static void Compress(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            WriteHeader(output);
            var buffer = new byte[CompressBlockSize];
            while (true)
            {
                var blockLength = ReadChunk(input, buffer);
                if (blockLength == 0)
                {
                    break;
                }

                WriteBlock(output, buffer, blockLength);
            }

            // 文件末尾：uncompressed_size=0 的结束标记。
            // End-of-file marker: an uncompressed size of zero.
            WriteUInt32BigEndian(output, 0);
        }
        catch (CompressionException) { throw; }
        catch (Exception ex) when (ex is IOException or OverflowException)
        {
            throw new CompressionException($"LZOP compression failed: {ex.Message} LZOP 压缩失败。", ex);
        }
    }

    private static void WriteHeader(Stream output)
    {
        output.Write(Magic);

        // 头内容（magic 之后、校验和之前）：与解压端 ReadHeaderUInt16/ReadHeaderUInt32/
        // ReadHeaderByte 收集的字节一致。Header content (between the magic and the checksum)
        // matches the bytes the decompressor collects via ReadHeaderUInt16/ReadHeaderUInt32/
        // ReadHeaderByte.
        Span<byte> header = stackalloc byte[HeaderContentLength];
        var position = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header[position..], LzopVersion); position += 2;  // version
        BinaryPrimitives.WriteUInt16BigEndian(header[position..], LzopVersion); position += 2;  // library version
        BinaryPrimitives.WriteUInt16BigEndian(header[position..], 0); position += 2;            // extra u16（version >= 0x0940）
        header[position++] = 1;                                                                  // method：LZO1X_1
        header[position++] = 0;                                                                  // extra byte（version >= 0x0940）
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], FAdler32U | FAdler32C); position += 4; // flags
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;            // mode
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;            // mtime_low
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;            // mtime_high（version >= 0x0940）
        header[position++] = 0;                                                                  // name_length

        output.Write(header);
        WriteUInt32BigEndian(output, Common.Checksums.Adler32.Compute(header));
    }

    private static void WriteBlock(Stream output, byte[] uncompressed, int uncompressedLength)
    {
        // 先尝试 LZO1X-1；输出不小于原块或压缩失败时，按 lzop 规范存原始块
        // （compressed_size == uncompressed_size，数据原样）。
        // Try LZO1X-1 first; store the raw block (compressed_size == uncompressed_size) when the
        // result is not smaller or compression fails, per the lzop specification.
        var block = uncompressed;
        var blockLength = uncompressedLength;
        try
        {
            var candidate = Lzo1x.Compress(uncompressed.AsSpan(0, uncompressedLength));
            if (candidate.Length < uncompressedLength)
            {
                block = candidate;
                blockLength = candidate.Length;
            }
        }
        catch (Exception)
        {
            // 压缩失败 → 回退存储原始块。 Compression failure → fall back to a stored block.
        }

        // 块布局（全部 u32 BE）：uncompressed_size；compressed_size；uncompressed_adler32；
        // compressed_adler32；块数据。顺序与解压端一致：先 uncompressed 校验和后 compressed 校验和。
        // Block layout (all u32 BE): uncompressed_size; compressed_size; uncompressed_adler32;
        // compressed_adler32; block data. Order matches the decompressor: the uncompressed
        // checksum first, then the compressed checksum.
        WriteUInt32BigEndian(output, (uint)uncompressedLength);
        WriteUInt32BigEndian(output, (uint)blockLength);
        WriteUInt32BigEndian(output, Adler32(uncompressed, 0, uncompressedLength));
        WriteUInt32BigEndian(output, Adler32(block, 0, blockLength));
        output.Write(block, 0, blockLength);
    }

    private static int ReadChunk(Stream input, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = input.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void WriteUInt32BigEndian(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static byte[] ReadBytes(Stream input, int count, string field)
    {
        var bytes = new byte[count];
        try { input.ReadExactly(bytes); }
        catch (EndOfStreamException ex) { throw new CompressionException($"Invalid LZOP data: truncated {field}.", ex); }
        return bytes;
    }

    private static uint ReadUInt32(Stream input, string field) => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(input, 4, field));

    private static uint Checksum(byte[] data, bool crc32) => crc32 ? Crc32Hash(data, 0, data.Length) : Adler32(data, 0, data.Length);

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
        => Common.Checksums.Adler32.Compute(data, offset, length);

    private static uint Crc32Hash(byte[] data, int offset, int length)
        => Crc32.HashToUInt32(data.AsSpan(offset, length));
}