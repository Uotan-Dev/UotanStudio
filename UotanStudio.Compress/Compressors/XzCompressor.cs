using UotanStudio.Compress.Codecs.Xz;
using UotanStudio.Compress.Internal;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.XzCrc64;

namespace UotanStudio.Compress.Compressors;

/// <summary>
/// XZ container codec: LZMA2 payload wrapped in the standard stream header, block, padding, index and footer layout with a CRC-32 or CRC-64 check over the uncompressed data.
/// <para>XZ 容器编解码器：将 LZMA2 负载包装为标准的流头、块、填充、索引与尾部布局，并对未压缩数据计算 CRC-32 或 CRC-64 校验。</para>
/// </summary>
public static class XzCompressor
{
    private static readonly byte[] Lzma2EosMarker = [0x00];

    /// <summary>XZ compression.<para>XZ 压缩。</para></summary>
    /// <param name="data">Plaintext encoded in full from the array; an empty array produces a valid container holding no block data.
    /// <para>整体取自数组并完整编码的明文字节；空数组生成不含块数据但合法的容器。</para></param>
    /// <param name="dictionarySize">LZMA2 dictionary window in bytes, also recorded in the block's LZMA2 filter properties so a reader can size its buffer; the default is 8 MiB.
    /// <para>LZMA2 字典窗口大小（字节），同时写入块的 LZMA2 过滤器属性供读取器分配缓冲；默认为 8 MiB。</para></param>
    /// <param name="checkType">Check identifier stored in the stream footer, <c>0x01</c> for CRC-32 (default) or <c>0x04</c> for CRC-64; the value is written without further validation.
    /// <para>写入流尾部的校验类型标识，<c>0x01</c> 为 CRC-32（默认），<c>0x04</c> 为 CRC-64；该值不做额外校验即写出。</para></param>
    /// <param name="maxDegreeOfParallelism"><see langword="null"/> or 1 and below encodes sequentially; above 1 encodes independent windows concurrently, with the bytes identical to the sequential result.
    /// <para>为 <see langword="null"/> 或小于等于 1 时串行编码；大于 1 时并发编码相互独立的窗口，输出字节与串行结果一致。</para></param>
    /// <returns>A complete single-block <c>.xz</c> stream ending with the CRC check, padding and index.
    /// <para>完整的单块 <c>.xz</c> 流，末尾包含 CRC 校验、填充与索引。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The LZMA2 encoder or the container writer failed.
    /// <para>LZMA2 编码器或容器写入失败。</para></exception>
    public static byte[] Compress(byte[] data, uint dictionarySize = Lzma2Encoder.DefaultDictionarySize, byte checkType = XzContainer.CheckTypeCrc32, int? maxDegreeOfParallelism = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            if (data.Length == 0)
            {
                return XzContainer.Wrap(Lzma2EosMarker, data, dictionarySize, checkType);
            }

            var lzma2 = Lzma2Encoder.Encode(data, dictionarySize, maxDegreeOfParallelism);
            return XzContainer.Wrap(lzma2, data, dictionarySize, checkType);
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 压缩失败", ex);
        }
    }

    /// <summary>Compresses bounded 64 KiB input windows and writes the container in place when the destination allows it, spooling the LZMA2 payload otherwise.
    /// <para>按有界 64 KiB 窗口压缩输入；目标允许时就地写出容器，否则暂存 LZMA2 载荷。</para></summary>
    /// <param name="input">Readable source drained window by window from its current position; the uncompressed bytes are hashed as they are read and the plaintext is never held in memory as a whole. Ownership stays with the caller.
    /// <para>从当前位置逐窗口读取的可读源流；读取时即对未压缩字节计算校验值，明文不会整体驻留内存。所有权归调用方。</para></param>
    /// <param name="output">Writable destination that receives the finished container; it is not closed. When the stream is also readable and positioned at its end, the block header is reserved, the payload is written directly and the sizes and index are backfilled in place; otherwise the LZMA2 payload is spooled to a temporary file in the system temp directory which is deleted when the encoding handle is released, whether the call succeeds or fails.
    /// <para>接收完整容器的写入目标，不会被本调用关闭。流同时可读且定位在末尾时，预留块头、直接写出载荷，并在原位回填大小与索引；否则 LZMA2 负载暂存于系统临时目录中的临时文件，编码句柄释放时无论成功失败都会被删除。</para></param>
    /// <param name="dictionarySize">LZMA2 dictionary window in bytes recorded in the block filter properties; the default is 8 MiB.
    /// <para>写入块过滤器属性的 LZMA2 字典窗口大小（字节），默认为 8 MiB。</para></param>
    /// <param name="checkType">Check identifier stored in the stream footer, <c>0x01</c> for CRC-32 (default) or <c>0x04</c> for CRC-64; any other value writes a four-byte CRC-32 check while advertising that value.
    /// <para>写入流尾部的校验类型标识，<c>0x01</c> 为 CRC-32（默认），<c>0x04</c> 为 CRC-64；其他取值仍会写出四字节的 CRC-32 校验值，但宣告的是该取值。</para></param>
    /// <param name="maxDegreeOfParallelism"><see langword="null"/> or 1 and below encodes sequentially; above 1 that many windows are read and encoded concurrently while staying in input order.
    /// <para>为 <see langword="null"/> 或小于等于 1 时串行编码；大于 1 时并发读取并编码相应数量的窗口，窗口仍按输入顺序写出。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, encoding, creating or spooling the temporary file, moving the in-place payload, or writing the container failed. An in-place destination is truncated back to its starting length before the failure is reported when the stream supports it.
    /// <para>读取、编码、临时文件的创建或暂存写入、就地移动载荷或容器写出失败。就地写出的目标在流支持时会于报告失败前截断回起始长度。</para></exception>
    public static void Compress(Stream input, Stream output, uint dictionarySize = Lzma2Encoder.DefaultDictionarySize,
        byte checkType = XzContainer.CheckTypeCrc32, int? maxDegreeOfParallelism = null)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        try
        {
            if (CanWriteInPlace(output))
            {
                CompressInPlace(input, output, dictionarySize, checkType, maxDegreeOfParallelism);
            }
            else
            {
                CompressSpooled(input, output, dictionarySize, checkType, maxDegreeOfParallelism);
            }
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 压缩失败", ex);
        }
    }

    private static bool CanWriteInPlace(Stream output)
    {
        if (!output.CanRead || !output.CanSeek || !output.CanWrite)
        {
            return false;
        }

        try
        {
            return output.Position == output.Length;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void CompressSpooled(Stream input, Stream output, uint dictionarySize, byte checkType,
        int? maxDegreeOfParallelism)
    {
        using var lzma2 = new FileStream(ScratchFile.CreateScratchPath("uotan-xz"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);

        var crc64 = new XzCrc64();
        var crc32 = new Crc32IeeeAccumulator();
        void OnWindow(byte[] window, int offset, int count)
        {
            if (checkType == XzContainer.CheckTypeCrc64)
            {
                crc64.Append(new ReadOnlySpan<byte>(window, offset, count));
            }
            else
            {
                crc32.Append(new ReadOnlySpan<byte>(window, offset, count));
            }
        }

        var total = Lzma2Encoder.Encode(input, lzma2, dictionarySize, maxDegreeOfParallelism, OnWindow);
        var check = CreateCheck(checkType, crc64, crc32);
        var compressedSize = lzma2.Length;
        lzma2.Position = 0;
        XzContainer.WriteContainer(output, lzma2, compressedSize, (ulong)total, check, dictionarySize, checkType);
    }

    private static void CompressInPlace(Stream input, Stream output, uint dictionarySize, byte checkType,
        int? maxDegreeOfParallelism)
    {
        var start = output.Position;
        try
        {
            var streamFlags = XzContainer.WriteStreamHeader(output, checkType);
            Span<byte> reserved = stackalloc byte[XzContainer.MaximumBlockHeaderSize];
            reserved.Clear();
            output.Write(reserved);
            var payloadStart = output.Position;

            var crc64 = new XzCrc64();
            var crc32 = new Crc32IeeeAccumulator();
            void OnWindow(byte[] window, int offset, int count)
            {
                if (checkType == XzContainer.CheckTypeCrc64)
                {
                    crc64.Append(new ReadOnlySpan<byte>(window, offset, count));
                }
                else
                {
                    crc32.Append(new ReadOnlySpan<byte>(window, offset, count));
                }
            }

            var total = Lzma2Encoder.Encode(input, output, dictionarySize, maxDegreeOfParallelism, OnWindow);
            var payloadEnd = output.Position;
            var compressedSize = payloadEnd - payloadStart;
            var blockHeader = XzContainer.BuildBlockHeader(compressedSize, (ulong)total, dictionarySize);
            if (blockHeader.Length > XzContainer.MaximumBlockHeaderSize)
            {
                throw new InvalidOperationException("The XZ block header exceeded its reserved size.");
            }

            if (blockHeader.Length < XzContainer.MaximumBlockHeaderSize)
            {
                payloadEnd = XzContainer.MoveLeft(output, payloadStart, payloadEnd,
                    XzContainer.MaximumBlockHeaderSize - blockHeader.Length);
            }

            output.Position = payloadStart - XzContainer.MaximumBlockHeaderSize;
            output.Write(blockHeader, 0, blockHeader.Length);
            output.Position = payloadEnd;
            XzContainer.WriteContainerTail(output, compressedSize, blockHeader.Length, (ulong)total,
                CreateCheck(checkType, crc64, crc32), streamFlags);
        }
        catch
        {
            TryRewind(output, start);
            throw;
        }
    }

    private static byte[] CreateCheck(byte checkType, XzCrc64 crc64, Crc32IeeeAccumulator crc32)
    {
        if (checkType == XzContainer.CheckTypeCrc64)
        {
            var crc = crc64.GetCurrentHashAsUInt64();
            var check = new byte[8];
            for (var i = 0; i < 8; i++)
            {
                check[i] = (byte)((crc >> (8 * i)) & 0xFF);
            }

            return check;
        }

        var crc32Value = crc32.GetCurrentHash();
        var check32 = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            check32[i] = (byte)((crc32Value >> (8 * i)) & 0xFF);
        }

        return check32;
    }

    private static void TryRewind(Stream output, long start)
    {
        try
        {
            output.SetLength(start);
            output.Position = start;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>XZ decompression.<para>XZ 解压。</para></summary>
    /// <param name="data">Complete <c>.xz</c> stream, treated as untrusted input; the whole expansion is buffered in memory with no output cap.
    /// <para>完整的 <c>.xz</c> 流，按不可信输入处理；解压结果整体驻留内存且没有上限。</para></param>
    /// <returns>The decoded bytes of every block in the container.<para>容器中所有块解码后的字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The container is malformed or truncated, a stored CRC does not match, the filter or check type is unsupported, or any other read or decode failure occurred; the original error is kept as <see cref="Exception.InnerException"/>.
    /// <para>容器非法或被截断、存储的 CRC 不匹配、过滤器或校验类型不受支持，或发生其他读取/解码失败；原始异常保留在 <see cref="Exception.InnerException"/> 中。</para></exception>
    public static byte[] Decompress(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            Decompress(input, output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 解压失败", ex);
        }
    }

    /// <summary>Streaming XZ decompression: the underlying XZStream is inherently streaming, so large inputs are piped without full buffering.
    /// <para>流式 XZ 解压：底层 XZStream 本身是流式的，无需整块缓冲，适合大输入。</para></summary>
    /// <param name="input">Readable <c>.xz</c> payload positioned at its magic; only the bytes the container consumes are read and the stream is not closed.
    /// <para>定位在魔数处的可读 <c>.xz</c> 数据；只读取容器消费的字节，不会关闭该流。</para></param>
    /// <param name="output">Writable destination fed as blocks decode, with no cap on the total size and no closing; a failure partway leaves the bytes already written.
    /// <para>随块解码即写入的写入目标，总大小不设上限、不会被本调用关闭；中途失败时已写字节会保留。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The container is malformed or truncated, a stored CRC does not match, the filter or check type is unsupported, or writing to <paramref name="output"/> failed.
    /// <para>容器非法或被截断、存储的 CRC 不匹配、过滤器或校验类型不受支持，或写入 <paramref name="output"/> 失败。</para></exception>
    public static void Decompress(Stream input, Stream output)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        try
        {
            using var xzStream = new XzDecoderStream(input, leaveOpen: true);
            ExactCopy.ToEnd(xzStream, output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 解压失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental XZ reader over a caller-owned container.
    /// <para>在调用方持有的容器数据上创建增量 XZ 读取器。</para>
    /// </summary>
    /// <param name="input">Readable <c>.xz</c> payload positioned at its magic; the reader takes the stream header, the blocks and the index as it goes.
    /// <para>定位在魔数处的可读 <c>.xz</c> 数据；读取器按需取得流头、数据块与索引。</para></param>
    /// <returns>A readable stream of decoded bytes that verifies the stored CRC as the container ends and leaves <paramref name="input"/> open when disposed.
    /// <para>解码字节的可读流，在容器末尾校验存储的 CRC，释放时不关闭 <paramref name="input"/>。</para></returns>
    internal static Stream CreateDecompressionStream(Stream input)
        => new XzDecoderStream(input, leaveOpen: true);

    /// <summary>
    /// Detects whether the data is in XZ format (magic FD 37 7A 58 5A 00).
    /// <para>检测数据是否为 XZ 格式（魔数 FD 37 7A 58 5A 00）。</para>
    /// </summary>
    /// <param name="data">
    /// Leading bytes of a candidate payload; must be a non-<see langword="null"/> array.
    /// <para>候选数据的前若干字节；必须是非 <see langword="null"/> 的数组。</para></param>
    /// <returns>
    /// <see langword="true"/> when six or more bytes match the full magic, including the trailing format-flag byte; the stream flags and block structure are not examined.
    /// <para>字节数不少于六个且与完整魔数（含末尾的格式标志字节）一致时为 <see langword="true"/>；不检查流标志与块结构。</para></returns>
    /// <exception cref="NullReferenceException"><paramref name="data"/> is <see langword="null"/>; the length is read without a null check.
    /// <para><paramref name="data"/> 为 <see langword="null"/>；读取长度前未做空值检查。</para></exception>
    public static bool IsXzFormat(byte[] data)
    {
        return data.Length >= 6 &&
               data[0] == 0xFD && data[1] == 0x37 && data[2] == 0x7A &&
               data[3] == 0x58 && data[4] == 0x5A && data[5] == 0x00;
    }
}