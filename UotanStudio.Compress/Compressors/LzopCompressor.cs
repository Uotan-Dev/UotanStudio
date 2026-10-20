using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Compress.Codecs.Lzo;
using UotanStudio.Compress.Internal;

namespace UotanStudio.Compress.Compressors;

/// <summary>
/// LZOP container codec: a nine-byte signature and big-endian header, then 256 KiB LZO1X-1 blocks, each capped at 64 MiB and closed by a zero-size block.
/// <para>LZOP 容器编解码器：九字节签名与大端头部，其后是 256 KiB 的 LZO1X-1 块，单块上限 64 MiB，并以零长度块结束。</para>
/// </summary>
internal static class LzopCompressor
{
    /// <summary>
    /// The nine-byte LZOP file signature, whose non-text trailing bytes reveal a payload that was altered in transit.
    /// <para>九字节的 LZOP 文件签名，其末尾的非文本字节可揭示数据在传输中被改写。</para></summary>
    /// <value>
    /// A read-only span of <c>89 4C 5A 4F 00 0D 0A 1A 0A</c> compared against the first bytes of a payload.
    /// <para>只读跨度，内容为 <c>89 4C 5A 4F 00 0D 0A 1A 0A</c>，用于与数据起始字节比对。</para></value>
    public static ReadOnlySpan<byte> Magic => [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int MagicSize = 9;

    private const uint MaximumBlockSize = 64 * 1024 * 1024;
    private const int M2MaxOffset = 0x0800;

    private const ushort LzopVersion = 0x0940;
    private const int CompressBlockSize = 256 * 1024;

    /// <summary>
    /// Growth soft limit for the pooled output buffer of the array-returning decompression overload: the buffer
    /// doubles until this size and grows by the required amount afterwards, so a small archive never rents a large
    /// block while a large one stops paying for repeated doublings.
    /// <para>返回数组的解压重载所用池化输出缓冲的增长软上限：缓冲倍增到该尺寸后改为按需增长，因此小归档不会租用大块，
    /// 大归档也不必反复支付倍增成本。</para>
    /// </summary>
    private const int ArrayOutputSoftLimit = 4 * 1024 * 1024;

    private const int HeaderContentLength = 25;

    private const uint FAdler32U = 0x00000001;
    private const uint FAdler32C = 0x00000002;
    private const uint FHCrc32 = 0x00001000;
    private const uint FCrc32U = 0x00000100;
    private const uint FCrc32C = 0x00000200;
    private const uint FFilter = 0x00000800;

    /// <summary>
    /// Checks whether the data starts with the LZOP magic bytes.
    /// <para>检查数据是否以 LZOP 魔数开头。</para>
    /// </summary>
    /// <param name="data">
    /// Leading bytes of a candidate payload; <see langword="null"/> and buffers shorter than nine bytes are rejected.
    /// <para>候选数据的前若干字节；为 <see langword="null"/> 或长度不足九字节时判定不通过。</para></param>
    /// <returns>
    /// <see langword="true"/> when the first nine bytes equal <see cref="Magic"/>; the header and block structure beyond it are not examined.
    /// <para>前九字节与 <see cref="Magic"/> 相同时为 <see langword="true"/>；其后的头部与块结构不做检查。</para></returns>
    public static bool IsLzopFormat(byte[]? data) => data is { Length: >= MagicSize } && data.AsSpan(0, MagicSize).SequenceEqual(Magic);

    /// <summary>Decompresses an LZOP file into its original bytes.
    /// <para>将 LZOP 文件解压为原始字节。</para></summary>
    /// <param name="data">Complete LZOP payload; this overload puts no cap on the expansion size.
    /// <para>完整的 LZOP 数据；本重载不限制解压后大小。</para></param>
    /// <returns>The decoded bytes of every block up to the terminating zero-size block.
    /// <para>直到终止零块之前所有块解码后的字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The magic, version, header checksum, block sizes or block checksums are invalid, an LZO filter is declared, or a block does not decode to its declared size.
    /// <para>魔数、版本、头部校验和、块大小或块校验和非法，声明了 LZO 过滤器，或块解码结果与声明大小不符。</para></exception>
    public static byte[] Decompress(byte[] data) => Decompress(data, long.MaxValue);

    /// <summary>Decompresses LZOP data with a total output limit.
    /// <para>解压 LZOP 数据并限制总输出大小。</para></summary>
    /// <param name="data">Complete LZOP payload, treated as untrusted input.
    /// <para>完整的 LZOP 数据，按不可信输入处理。</para></param>
    /// <param name="maximumOutputBytes">Inclusive cap on the decoded total, checked block by block before any write.
    /// <para>解压总量的含上限，在写入前逐块检查。</para></param>
    /// <returns>The decoded bytes.<para>解码后的字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is negative.
    /// <para><paramref name="maximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="CompressionException">The magic, version, header checksum, block sizes or block checksums are invalid, an LZO filter is declared, a block does not decode to its declared size, or the decoded total would pass <paramref name="maximumOutputBytes"/>.
    /// <para>魔数、版本、头部校验和、块大小或块校验和非法，声明了 LZO 过滤器，块解码结果与声明大小不符，或解压总量将超过 <paramref name="maximumOutputBytes"/>。</para></exception>
    public static byte[] Decompress(byte[] data, long maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var input = new MemoryStream(data, writable: false);
        using var output = new PooledWriteStream(ArrayOutputSoftLimit);
        Decompress(input, output, Math.Min(maximumOutputBytes, int.MaxValue));
        return output.Buffer.AsSpan(0, output.WrittenLength).ToArray();
    }

    /// <summary>Streams LZOP blocks to the output while enforcing a cumulative output limit.
    /// <para>逐块流式解压 LZOP 数据并限制累计输出。</para></summary>
    /// <param name="input">Readable payload positioned at its magic; header integers are big-endian and only the bytes up to the terminating zero block are consumed. The stream is not closed.
    /// <para>定位在魔数处的可读数据；头部整数按大端解析，只消费到终止零块为止的字节，不会关闭该流。</para></param>
    /// <param name="output">Writable destination receiving one verified block at a time, so a later failure leaves the earlier blocks written; it is not closed.
    /// <para>每次写入一个已通过校验的块的写入目标，因此后续失败时前面的块已写出；不会被本调用关闭。</para></param>
    /// <param name="maximumOutputBytes">Inclusive cap on the decoded total; both the declared block size and the remaining budget are checked before the block is read, so a payload needing more than the budget is refused without writing it.
    /// <para>解压总量的含上限；读取块之前同时检查声明的块大小与剩余预算，超出预算的数据在写出前即被拒绝。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is negative.
    /// <para><paramref name="maximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="CompressionException">The magic is wrong, the version is below 0x0900, a header field is truncated, the header checksum does not match, an LZO filter is declared, a block size is above 64 MiB or larger than its uncompressed size, a per-block Adler-32 or CRC-32 declared by the header flags mismatches, a block does not decode to its declared size, the output limit is reached, or the stream ends early.
    /// <para>魔数错误、版本低于 0x0900、头部字段被截断、头部校验和不匹配、声明了 LZO 过滤器、块大小超过 64 MiB 或大于其未压缩大小、头部标志声明的块级 Adler-32 或 CRC-32 不匹配、块解码结果与声明大小不符、达到输出上限，或流提前结束。</para></exception>
    public static void Decompress(Stream input, Stream output, long maximumOutputBytes)
        => Decompress(input, output, maximumOutputBytes, maxDegreeOfParallelism: null);

    /// <summary>Streams LZOP blocks to the output with an optional per-block parallelism degree.
    /// <para>逐块流式解压 LZOP 数据，并可指定逐块并行度。</para></summary>
    /// <param name="input">Readable payload positioned at its magic; blocks are read in ascending order and only the bytes up to the terminating zero block are consumed. The stream is not closed.
    /// <para>定位在魔数处的可读数据；块按升序读取，只消费到终止零块为止的字节，不会关闭该流。</para></param>
    /// <param name="output">Writable destination receiving blocks strictly in block order, so a later failure leaves the earlier blocks written; it is not closed.
    /// <para>严格按块顺序接收块的写入目标，因此后续失败时前面的块已写出；不会被本调用关闭。</para></param>
    /// <param name="maximumOutputBytes">Inclusive cap on the decoded total; the declared block size is checked against the remaining budget as each block is read, so a payload needing more than the budget is refused before its bytes are decoded.
    /// <para>解压总量的含上限；读取每个块时即用剩余预算检查其声明大小，超出预算的数据在解码前即被拒绝。</para></param>
    /// <param name="maxDegreeOfParallelism"><see langword="null"/> or 1 and below decodes blocks sequentially; above 1 decodes that many blocks concurrently, capped by the number of blocks staged in one batch, while the bytes written and therefore the output remain identical to the sequential run.
    /// <para>为 <see langword="null"/> 或小于等于 1 时串行解码各块；大于 1 时并发解码相应数量的块（不超过单个批次内的块数），写出的字节及其顺序与串行结果完全一致。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is negative.
    /// <para><paramref name="maximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="CompressionException">The magic is wrong, the version is below 0x0900, a header field is truncated, the header checksum does not match, an LZO filter is declared, a block size is above 64 MiB or larger than its uncompressed size, a per-block Adler-32 or CRC-32 declared by the header flags mismatches, a block does not decode to its declared size, the output limit is reached, or the stream ends early.
    /// <para>魔数错误、版本低于 0x0900、头部字段被截断、头部校验和不匹配、声明了 LZO 过滤器、块大小超过 64 MiB 或大于其未压缩大小、头部标志声明的块级 Adler-32 或 CRC-32 不匹配、块解码结果与声明大小不符、达到输出上限，或流提前结束。</para></exception>
    public static void Decompress(Stream input, Stream output, long maximumOutputBytes, int? maxDegreeOfParallelism)
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
                _ = ReadHeaderUInt32();
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
            var checks = new BlockChecks
            {
                HasUncompressedAdler32 = hasUncompressedAdler32,
                HasUncompressedCrc32 = hasUncompressedCrc32,
                HasCompressedAdler32 = hasCompressedAdler32,
                HasCompressedCrc32 = hasCompressedCrc32,
            };

            long totalOutput = 0;
            var degree = Parallelism.Resolve(maxDegreeOfParallelism, int.MaxValue);
            var staged = new List<DecompressedBlock>(degree);
            while (true)
            {
                staged.Clear();
                var ended = false;
                while (staged.Count < degree)
                {
                    var uncompressedSize = ReadUInt32(input, "block size");
                    if (uncompressedSize == 0)
                    {
                        ended = true;
                        break;
                    }

                    var compressedSize = ReadUInt32(input, "compressed block size");

                    if (compressedSize > uncompressedSize || compressedSize > MaximumBlockSize || uncompressedSize > MaximumBlockSize)
                        throw new CompressionException($"Invalid LZOP block size (supported maximum {MaximumBlockSize} bytes) 无效的 LZOP 块大小。");
                    var remainingOutputBytes = maximumOutputBytes - totalOutput;
                    if (compressedSize > remainingOutputBytes || uncompressedSize > remainingOutputBytes)
                        throw new CompressionException($"LZOP block exceeds the output limit of {maximumOutputBytes} bytes.");

                    var uncompressedAdler32 = hasUncompressedAdler32 ? ReadUInt32(input, "uncompressed Adler-32") : 0;
                    var uncompressedCrc32 = hasUncompressedCrc32 ? ReadUInt32(input, "uncompressed CRC-32") : 0;
                    var compressedAdler32 = hasCompressedAdler32 && compressedSize < uncompressedSize
                        ? ReadUInt32(input, "compressed Adler-32") : uncompressedAdler32;
                    var compressedCrc32 = hasCompressedCrc32 && compressedSize < uncompressedSize
                        ? ReadUInt32(input, "compressed CRC-32") : uncompressedCrc32;
                    var blockLength = (int)compressedSize;
                    var block = RentBlock(input, blockLength);

                    totalOutput = checked(totalOutput + uncompressedSize);
                    staged.Add(new DecompressedBlock(block, blockLength, (int)uncompressedSize, uncompressedAdler32, uncompressedCrc32, compressedAdler32, compressedCrc32));
                }

                if (staged.Count > 1)
                {
                    var slots = staged;
                    var plan = checks;
                    Parallel.For(
                        0, slots.Count,
                        new ParallelOptions { MaxDegreeOfParallelism = Math.Min(degree, slots.Count) },
                        i => slots[i].Decode(plan));
                }
                else if (staged.Count == 1)
                {
                    staged[0].Decode(checks);
                }

                try
                {
                    foreach (var slot in staged)
                    {
                        slot.ThrowIfFailed();
                        output.Write(slot.Decoded, 0, slot.DecodedLength);
                    }
                }
                finally
                {
                    foreach (var slot in staged)
                    {
                        slot.ReturnBuffers();
                    }
                }

                if (ended)
                {
                    return;
                }
            }
        }
        catch (CompressionException) { throw; }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OverflowException)
        {
            throw new CompressionException($"LZOP decompression failed: {ex.Message} LZOP 解压失败。", ex);
        }
    }

    /// <summary>Compresses data into an LZOP (lzop) byte array.
    /// <para>将数据压缩为 LZOP（lzop）格式的字节数组。</para></summary>
    /// <param name="data">Plaintext taken in full from the array; an empty array produces a header followed by the terminating zero block.
    /// <para>整体取自数组的明文字节；空数组产生头部加终止零块。</para></param>
    /// <returns>The magic, a big-endian header closed by its Adler-32 checksum, one block per 256 KiB of input, and the terminating zero block.
    /// <para>魔数、以大端写出并以 Adler-32 校验和收尾的头部、每 256 KiB 输入对应的一个块，以及终止零块。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The block encoder overflowed a size field or an in-memory write failed.
    /// <para>块编码器使大小字段溢出，或内存写入失败。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The encoding or buffering layer failed.
    /// <para>编码或缓冲层执行失败。</para></exception>
    public static byte[] Compress(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        Compress(input, output);
        return output.ToArray();
    }

    /// <summary>Compresses the input stream into the LZOP (lzop) format in 256 KiB blocks.
    /// <para>将输入流按 256 KiB 块压缩为 LZOP（lzop）格式并写入输出流。</para></summary>
    /// <param name="input">Readable source drained block by block until end of stream, so the plaintext is never held in memory as a whole; ownership stays with the caller.
    /// <para>逐块读取直至流末尾的可读源流，明文不会整体驻留内存；所有权归调用方。</para></param>
    /// <param name="output">Writable destination appended from its current position and left open; each block stores both sizes and an uncompressed Adler-32, with a compressed Adler-32 only when LZO1X-1 encoding is smaller than its input.
    /// <para>从当前位置追加并保持打开的写入目标；每块写出两个大小和原始数据 Adler-32，仅在 LZO1X-1 编码小于输入时附加压缩数据 Adler-32。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, writing or an arithmetic overflow while sizing the output failed.
    /// <para>读取、写入失败，或计算输出大小时发生算术溢出。</para></exception>
    public static void Compress(Stream input, Stream output)
        => Compress(input, output, maxDegreeOfParallelism: null);

    /// <summary>Compresses the input stream into the LZOP (lzop) format in 256 KiB blocks with an optional parallelism degree.
    /// <para>将输入流按 256 KiB 块压缩为 LZOP（lzop）格式，并可指定并行度。</para></summary>
    /// <param name="input">Readable source drained block by block until end of stream, so the plaintext is never held in memory as a whole beyond the staged batch; ownership stays with the caller.
    /// <para>逐块读取直至流末尾的可读源流，除已暂存的批次外明文不会整体驻留内存；所有权归调用方。</para></param>
    /// <param name="output">Writable destination appended from its current position and left open; blocks are emitted strictly in input order, so the container bytes are identical for every degree.
    /// <para>从当前位置追加并保持打开的写入目标；块严格按输入顺序写出，因此任何并行度下容器字节都完全一致。</para></param>
    /// <param name="maxDegreeOfParallelism"><see langword="null"/> or 1 and below encodes blocks sequentially; above 1 encodes that many blocks concurrently, capped by the number of blocks staged in one batch, while each block is still written in input order and the container bytes stay identical to the sequential result.
    /// <para>为 <see langword="null"/> 或小于等于 1 时串行编码各块；大于 1 时并发编码相应数量的块（不超过单个批次内的块数），各块仍按输入顺序写出，容器字节与串行结果完全一致。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, writing or an arithmetic overflow while sizing the output failed.
    /// <para>读取、写入失败，或计算输出大小时发生算术溢出。</para></exception>
    public static void Compress(Stream input, Stream output, int? maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            WriteHeader(output);
            var degree = Parallelism.Resolve(maxDegreeOfParallelism, int.MaxValue);
            var buffers = new byte[degree][];
            for (var slot = 0; slot < degree; slot++)
            {
                buffers[slot] = new byte[CompressBlockSize];
            }

            var encoded = degree == 1 ? null : new EncodedBlock[degree];
            while (true)
            {
                var pending = 0;
                while (pending < degree)
                {
                    var blockLength = ReadChunk(input, buffers[pending]);
                    if (blockLength == 0)
                    {
                        break;
                    }

                    if (degree == 1)
                    {
                        WriteBlock(output, buffers[0], blockLength);
                    }
                    else
                    {
                        encoded![pending] = new EncodedBlock(buffers[pending], blockLength);
                    }

                    pending++;
                }

                if (degree > 1 && pending > 0)
                {
                    var blocks = encoded!;
                    var count = pending;
                    Parallel.For(
                        0, count,
                        new ParallelOptions { MaxDegreeOfParallelism = Math.Min(degree, count) },
                        i => blocks[i].Encode());

                    for (var i = 0; i < count; i++)
                    {
                        var block = blocks[i];
                        block.ThrowIfFailed();
                        WriteBlock(output, block.Payload, block.PayloadLength, block.UncompressedLength, block.UncompressedAdler32);
                        blocks[i] = null!;
                    }
                }

                if (pending < degree)
                {
                    break;
                }
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
        BinaryPrimitives.WriteUInt16BigEndian(header[position..], LzopVersion); position += 2;
        header[position++] = 1;
        header[position++] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], FAdler32U | FAdler32C); position += 4;
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;
        BinaryPrimitives.WriteUInt32BigEndian(header[position..], 0); position += 4;
        header[position++] = 0;

        output.Write(header);
        WriteUInt32BigEndian(output, Hash.Adler32.Adler32.Compute(header));
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

        WriteBlock(output, block, blockLength, uncompressedLength, Adler32(uncompressed, 0, uncompressedLength));
    }

    /// <summary>
    /// Writes one LZOP block from an already chosen payload, emitting both sizes and the checksums the header flags require.
    /// <para>依据已选定的载荷写出一个 LZOP 块，输出两个大小以及头部标志要求的校验和。</para>
    /// </summary>
    /// <param name="output">Destination appended in place; it is not closed.
    /// <para>就地追加的写入目标，不会被本调用关闭。</para></param>
    /// <param name="block">Stored bytes, either the LZO1X-1 candidate or the original plaintext when it was not smaller.
    /// <para>实际存储的字节，即 LZO1X-1 候选结果，或其未更小时的原明文。</para></param>
    /// <param name="blockLength">Number of bytes stored from <paramref name="block"/>.
    /// <para><paramref name="block"/> 中实际存储的字节数。</para></param>
    /// <param name="uncompressedLength">Original plaintext length recorded in the block header.
    /// <para>写入块头的原始明文长度。</para></param>
    /// <param name="uncompressedAdler32">Adler-32 of the original plaintext, which is stored whether or not the block was compressed.
    /// <para>原始明文的 Adler-32；无论该块是否被压缩都会写入。</para></param>
    private static void WriteBlock(Stream output, byte[] block, int blockLength, int uncompressedLength, uint uncompressedAdler32)
    {
        WriteUInt32BigEndian(output, (uint)uncompressedLength);
        WriteUInt32BigEndian(output, (uint)blockLength);
        WriteUInt32BigEndian(output, uncompressedAdler32);
        if (blockLength < uncompressedLength)
        {
            WriteUInt32BigEndian(output, Adler32(block, 0, blockLength));
        }
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

    /// <summary>
    /// Reads one block body into a pooled buffer, returning it to the pool if the read fails so a truncated stream
    /// cannot strand a block-sized rental.
    /// <para>把一个块体读入池化缓冲；读取失败时立即归还，因此被截断的流不会遗漏一块大小的租用。</para>
    /// </summary>
    /// <param name="input">Source stream positioned at the block body.
    /// <para>定位在块体起始处的来源流。</para></param>
    /// <param name="count">Exact block body length in bytes; the trailing capacity of the rented buffer is unused.
    /// <para>块体的精确字节数；租用缓冲的尾部容量不使用。</para></param>
    /// <returns>The rented buffer holding the block body. <para>持有块体的租用缓冲。</para></returns>
    /// <exception cref="CompressionException">The stream ended before <paramref name="count"/> bytes.
    /// <para>在读完 <paramref name="count"/> 字节之前流已结束。</para></exception>
    private static byte[] RentBlock(Stream input, int count)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            input.ReadExactly(buffer, 0, count);
            return buffer;
        }
        catch (EndOfStreamException ex)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw new CompressionException("Invalid LZOP data: truncated block data.", ex);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static uint Checksum(byte[] data, bool crc32) => crc32 ? Crc32Hash(data, 0, data.Length) : Adler32(data, 0, data.Length);

    /// <summary>
    /// Performs bounds-checked LZO1X-1 decompression that never writes past the output buffer.
    /// <para>执行带边界检查的 LZO1X-1 解压，绝不越界写出输出缓冲区。</para>
    /// </summary>
    private static int Lzo1xDecompressSafe(byte[] input, int inputStart, int inputLen, byte[] output, int outputLength)
    {
        var ip = inputStart;
        var ipEnd = inputStart + inputLen;
        var op = 0;
        var opEnd = outputLength;
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
        => Hash.Adler32.Adler32.Compute(data, offset, length);

    private static uint Crc32Hash(byte[] data, int offset, int length)
        => Hash.Crc32Ieee.Crc32Ieee.Compute(data.AsSpan(offset, length));

    /// <summary>
    /// The per-block checksum declarations read from the LZOP header flags, shared by every block of one payload.
    /// <para>从 LZOP 头部标志解析出的逐块校验声明，为同一份数据的所有块共享。</para>
    /// </summary>
    private readonly record struct BlockChecks
    {
        /// <summary>Whether an Adler-32 over the decoded bytes is stored. <para>是否存储了解码字节的 Adler-32。</para></summary>
        public bool HasUncompressedAdler32 { get; init; }

        /// <summary>Whether a CRC-32 over the decoded bytes is stored. <para>是否存储了解码字节的 CRC-32。</para></summary>
        public bool HasUncompressedCrc32 { get; init; }

        /// <summary>Whether an Adler-32 over the stored bytes of a compressed block is present. <para>压缩块是否附有存储字节的 Adler-32。</para></summary>
        public bool HasCompressedAdler32 { get; init; }

        /// <summary>Whether a CRC-32 over the stored bytes of a compressed block is present. <para>压缩块是否附有存储字节的 CRC-32。</para></summary>
        public bool HasCompressedCrc32 { get; init; }
    }

    /// <summary>
    /// One staged LZOP input block: its raw bytes, declared sizes and checksums. Decoding and verification run on a
    /// worker thread and store their outcome for the ordered writer to rethrow, so whichever slot fails, the
    /// exception reaching the caller keeps its original type and message.
    /// <para>一个已暂存的 LZOP 输入块：原始字节、声明的大小与校验和。解码与校验在工作线程上执行并把结果留给有序
    /// 写出方重新抛出，因此无论哪个槽位失败，抵达调用方的异常都保持原有类型与消息。</para>
    /// </summary>
    private sealed class DecompressedBlock(byte[] block, int blockLength, int uncompressedSize, uint uncompressedAdler32, uint uncompressedCrc32, uint compressedAdler32, uint compressedCrc32)
    {
        private CompressionException? _failure;
        private bool _ownsDecoded;

        /// <summary>The decoded bytes, valid once <see cref="Decode"/> finished without storing a failure.
        /// <para>解码后的字节；<see cref="Decode"/> 未记录失败而完成时有效。</para></summary>
        public byte[] Decoded { get; private set; } = [];

        /// <summary>
        /// The number of decoded bytes in <see cref="Decoded"/>, which is shorter than the array when it came from the pool.
        /// <para><see cref="Decoded"/> 中有效解码字节数；当缓冲来自池时该值小于数组长度。</para>
        /// </summary>
        public int DecodedLength { get; private set; }

        /// <summary>
        /// Returns the rented input and decode buffers so the next batch reuses them instead of allocating another
        /// pair of block-sized arrays.
        /// <para>归还租用的输入缓冲与解码缓冲，使下一批次复用它们而不是再分配一对块大小的数组。</para>
        /// </summary>
        /// <remarks>
        /// The input buffer is returned unconditionally because the staging loop always rents it; the decode buffer is
        /// returned only when this block rented one, since a stored block decodes in place over its input.
        /// <para>输入缓冲总是归还，因为暂存循环总是租用它；解码缓冲仅在确实租用过时归还，因为存储块直接在其输入上就地解码。</para>
        /// </remarks>
        public void ReturnBuffers()
        {
            if (_ownsDecoded)
            {
                var decoded = Decoded;
                Decoded = [];
                DecodedLength = 0;
                _ownsDecoded = false;
                if (decoded.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(decoded);
                }
            }

            if (block.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(block);
            }
        }

        /// <summary>
        /// Verifies the stored-block checksums, decodes the block when it is compressed, and verifies the decoded checksums.
        /// <para>校验存储块的校验和，在块被压缩时解码，并校验解码后的校验和。</para>
        /// </summary>
        /// <param name="checks">Header-level declarations deciding which checksums apply.
        /// <para>头部级声明，决定应用哪些校验和。</para></param>
        public void Decode(BlockChecks checks)
        {
            try
            {
                if (checks.HasCompressedAdler32 && Adler32(block, 0, blockLength) != compressedAdler32)
                    throw new CompressionException("Invalid LZOP data: compressed Adler-32 mismatch.");
                if (checks.HasCompressedCrc32 && Crc32Hash(block, 0, blockLength) != compressedCrc32)
                    throw new CompressionException("Invalid LZOP data: compressed CRC-32 mismatch.");

                byte[] decoded;
                int decodedLength;
                if (blockLength == uncompressedSize)
                {
                    decoded = block;
                    decodedLength = blockLength;
                }
                else
                {
                    var rented = ArrayPool<byte>.Shared.Rent(uncompressedSize);
                    int count;
                    try
                    {
                        count = Lzo1xDecompressSafe(block, 0, blockLength, rented, uncompressedSize);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        throw;
                    }

                    if (count != uncompressedSize)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        throw new CompressionException($"Invalid LZOP data: block decompressed to {count} bytes, expected {uncompressedSize}.");
                    }

                    decoded = rented;
                    decodedLength = uncompressedSize;
                    _ownsDecoded = true;
                }

                Decoded = decoded;
                DecodedLength = decodedLength;

                if (checks.HasUncompressedAdler32 && Adler32(decoded, 0, decodedLength) != uncompressedAdler32)
                    throw new CompressionException("Invalid LZOP data: uncompressed Adler-32 mismatch.");
                if (checks.HasUncompressedCrc32 && Crc32Hash(decoded, 0, decodedLength) != uncompressedCrc32)
                    throw new CompressionException("Invalid LZOP data: uncompressed CRC-32 mismatch.");
            }
            catch (CompressionException ex)
            {
                _failure = ex;
            }
        }

        /// <summary>
        /// Rethrows the decode failure recorded for this block, if any, so the ordered writer surfaces it.
        /// <para>重新抛出该块记录的解码失败（若有），以便有序写出方上报。</para>
        /// </summary>
        /// <exception cref="CompressionException">The block failed verification or decoding.
        /// <para>该块校验或解码失败。</para></exception>
        public void ThrowIfFailed()
        {
            if (_failure is not null)
            {
                throw _failure;
            }
        }
    }

    /// <summary>
    /// One staged LZOP output block: the plaintext buffer holding a 256 KiB chunk together with the payload chosen
    /// by the encoder. The encoder runs on a worker thread and records its outcome for the ordered writer to
    /// rethrow, so whichever slot fails, the exception reaching the caller keeps its original type and message.
    /// <para>一个已暂存的 LZOP 输出块：持有 256 KiB 块的明文缓冲区，以及编码器选定的载荷。编码在工作线程上执行
    /// 并把结果留给有序写出方重新抛出，因此无论哪个槽位失败，抵达调用方的异常都保持原有类型与消息。</para>
    /// </summary>
    private sealed class EncodedBlock
    {
        private readonly byte[] _plaintext;
        private CompressionException? _failure;

        /// <summary>
        /// Captures the plaintext buffer holding one chunk together with the number of bytes read into it.
        /// <para>记录承载单个块的明文缓冲区以及读入其中的字节数。</para>
        /// </summary>
        /// <param name="plaintext">Buffer holding the chunk, also the fallback payload when compression does not shrink it.
        /// <para>承载该块的缓冲区，同时也是压缩未使其变小时的回退载荷。</para></param>
        /// <param name="uncompressedLength">Number of plaintext bytes read into <paramref name="plaintext"/>.
        /// <para>读入 <paramref name="plaintext"/> 的明文字节数。</para></param>
        public EncodedBlock(byte[] plaintext, int uncompressedLength)
        {
            _plaintext = plaintext;
            Payload = plaintext;
            PayloadLength = uncompressedLength;
            UncompressedLength = uncompressedLength;
        }

        /// <summary>The bytes to store, equal to the plaintext when the encoder failed or did not shrink the chunk.
        /// <para>实际存储的字节；编码失败或未能压缩该块时等于明文。</para></summary>
        public byte[] Payload { get; private set; }

        /// <summary>The stored byte count. <para>实际存储的字节数。</para></summary>
        public int PayloadLength { get; private set; }

        /// <summary>The plaintext byte count of this chunk. <para>该块的明文字节数。</para></summary>
        public int UncompressedLength { get; }

        /// <summary>The plaintext Adler-32, computed once because it is stored whether or not the block was compressed.
        /// <para>明文的 Adler-32；无论该块是否被压缩都会写入，因此只计算一次。</para></summary>
        public uint UncompressedAdler32 { get; private set; }

        /// <summary>
        /// Runs the LZO1X-1 encoder over the chunk, keeping the plaintext when the candidate is not smaller, and
        /// records a compression failure instead of letting it escape the worker thread.
        /// <para>对该块运行 LZO1X-1 编码器；候选结果未更小时保留明文，并把压缩失败记录下来而不是让它逃出工作线程。</para>
        /// </summary>
        public void Encode()
        {
            try
            {
                UncompressedAdler32 = Adler32(_plaintext, 0, UncompressedLength);
                var candidate = Lzo1x.Compress(_plaintext.AsSpan(0, UncompressedLength));
                if (candidate.Length < UncompressedLength)
                {
                    Payload = candidate;
                    PayloadLength = candidate.Length;
                }
            }
            catch (CompressionException ex)
            {
                _failure = ex;
            }
            catch (Exception)
            {
                Payload = _plaintext;
                PayloadLength = UncompressedLength;
            }
        }

        /// <summary>
        /// Rethrows the encoder failure recorded for this block, if any, so the ordered writer surfaces it.
        /// <para>重新抛出该块记录的编码失败（若有），以便有序写出方上报。</para>
        /// </summary>
        /// <exception cref="CompressionException">The block encoder failed with a compression error.
        /// <para>块编码器以压缩错误失败。</para></exception>
        public void ThrowIfFailed()
        {
            if (_failure is not null)
            {
                throw _failure;
            }
        }
    }
}