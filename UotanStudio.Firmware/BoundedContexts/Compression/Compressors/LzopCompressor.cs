using UotanStudio.Firmware.BoundedContexts.Compression.Codecs.Lzo;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace UotanStudio.Firmware.BoundedContexts.Compression.Compressors;

public static class LzopCompressor
{
    public static ReadOnlySpan<byte> Magic => [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int MagicSize = 9;

    private const uint MaximumBlockSize = 64 * 1024 * 1024;
    private const int M2MaxOffset = 0x0800;


    private const ushort LzopVersion = 0x0940;
    private const int CompressBlockSize = 256 * 1024;


    private const int HeaderContentLength = 25;


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
    public static bool IsLzopFormat(byte[]? data) => data is { Length: >= MagicSize } && data.AsSpan(0, MagicSize).SequenceEqual(Magic);

    /// <summary>
    /// Decompresses an LZOP file into its original bytes.
    /// <para>将 LZOP 文件解压为原始字节。</para>
    /// </summary>
    public static byte[] Decompress(byte[] data) => Decompress(data, long.MaxValue);

    /// <summary>
    /// Decompresses LZOP data with a total output limit.
    /// <para>解压 LZOP 数据并限制总输出大小。</para>
    /// </summary>
    public static byte[] Decompress(byte[] data, long maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        Decompress(input, output, maximumOutputBytes);
        return output.ToArray();
    }

    /// <summary>
    /// Streams LZOP blocks to the output while enforcing a cumulative output limit.
    /// <para>逐块流式解压 LZOP 数据并限制累计输出。</para>
    /// </summary>
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
            _ = ReadHeaderUInt16();
            if (version >= 0x0940) _ = ReadHeaderUInt16();
            _ = ReadHeaderByte();
            if (version >= 0x0940) _ = ReadHeaderByte();
            var flags = ReadHeaderUInt32();
            if ((flags & FFilter) != 0)
            {
                _ = ReadHeaderUInt32(); // consume filter ID
                throw new CompressionException("LZOP filter (F_H_FILTER) is not supported. The input file uses an external filter preprocessor that cannot be decompressed with standard LZO1X.");
            }
            _ = ReadHeaderUInt32();
            _ = ReadHeaderUInt32();
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


                if (compressedSize > uncompressedSize || compressedSize > MaximumBlockSize || uncompressedSize > MaximumBlockSize)
                    throw new CompressionException($"Invalid LZOP block size (supported maximum {MaximumBlockSize} bytes) 无效的 LZOP 块大小。");
                var remainingOutputBytes = maximumOutputBytes - totalOutput;
                if (compressedSize > remainingOutputBytes || uncompressedSize > remainingOutputBytes)
                    throw new CompressionException($"LZOP block exceeds the output limit of {maximumOutputBytes} bytes.");


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


    /// <summary>
    /// Compresses data into an LZOP (lzop) byte array.
    /// <para>将数据压缩为 LZOP（lzop）格式的字节数组。</para>
    /// </summary>
    public static byte[] Compress(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        Compress(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// Compresses the input stream into the LZOP (lzop) format in 256 KiB blocks.
    /// <para>将输入流按 256 KiB 块压缩为 LZOP（lzop）格式并写入输出流。</para>
    /// </summary>
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


        Span<byte> header = stackalloc byte[HeaderContentLength];
        var position = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header[position..], LzopVersion); position += 2;
        BinaryPrimitives.WriteUInt16BigEndian(header[position..], LzopVersion); position += 2;
        BinaryPrimitives.WriteUInt16BigEndian(header[position..], 0); position += 2;
        header[position++] = 1;
        header[position++] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], FAdler32U | FAdler32C); position += 4;
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;
        header[position++] = 0;

        output.Write(header);
        WriteUInt32BigEndian(output, SharedKernel.Checksums.Adler32.Compute(header));
    }

    private static void WriteBlock(Stream output, byte[] uncompressed, int uncompressedLength)
    {


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

        }


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
    /// Safe LZO1X-1 decompression, ported from minilzo's lzo1x_decompress_safe with bounds checks.
    /// <para>安全的 LZO1X-1 解压，移植自 minilzo 的 lzo1x_decompress_safe，带边界检查。</para>
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
        => SharedKernel.Checksums.Adler32.Compute(data, offset, length);

    private static uint Crc32Hash(byte[] data, int offset, int length)
        => Crc32.HashToUInt32(data.AsSpan(offset, length));
}