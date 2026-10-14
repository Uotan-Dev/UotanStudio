using System.Buffers;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using UotanStudio.Compress.Codecs.Lz4;
using UotanStudio.Compress.Internal;

namespace UotanStudio.Compress.Compressors;

/// <summary>
/// LZ4 codec covering both on-disk layouts: the standard frame produced by the K4os stream implementation and the legacy block frame made of a magic followed by little-endian 4-byte length prefixed blocks with no frame descriptor, checksum or end marker.
/// <para>LZ4 编解码器，覆盖两种落盘布局：由 K4os 流实现生成的标准帧，以及传统块帧——魔数后跟小端 4 字节长度前缀的若干块，不含帧描述符、校验和与结束标记。</para>
/// </summary>
internal static class Lz4Compressor
{
    private const int BlockChunkSize = 0x10000;


    /// <summary>
    /// Standard LZ4 frame compression.
    /// <para>标准 LZ4 帧压缩。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext compressed in one pass.
    /// <para>一次性压缩的明文字节。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read and clamped into the LZ4 0-12 range, 0 (fast) when unset; the other members are ignored and the data is not chunked.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/> 并夹取到 LZ4 的 0-12 范围，未设置时为 0（快速）；其他成员被忽略，数据也不分块。</para></param>
    /// <returns>
    /// A complete LZ4 frame, from the <c>04 22 4D 18</c> magic through the end marker.
    /// <para>完整的 LZ4 帧，从 <c>04 22 4D 18</c> 魔数直到结束标记。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The frame encoder failed.
    /// <para>帧编码器执行失败。</para></exception>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {


            using var output = new MemoryStream();
            using (var encoder = LZ4Stream.Encode(output, ToLz4Level(options?.Level), leaveOpen: true))
            {
                encoder.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 压缩失败", ex);
        }
    }

    /// <summary>
    /// Standard LZ4 frame decompression.
    /// <para>标准 LZ4 帧解压。</para>
    /// </summary>
    /// <param name="data">
    /// Complete LZ4 frame, treated as untrusted input; the whole expansion is buffered in memory with no output cap.
    /// <para>完整的 LZ4 帧，按不可信输入处理；解压结果整体驻留内存且没有上限。</para></param>
    /// <returns>
    /// The decoded bytes of the frame.
    /// <para>该帧解码后的字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The frame magic, descriptor, block data or content checksum is invalid, or the frame is truncated.
    /// <para>帧魔数、描述符、块数据或内容校验和无效，或帧被截断。</para></exception>
    public static byte[] Decompress(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var input = new MemoryStream(data);
            using var decoder = LZ4Stream.Decode(input);
            using var output = new MemoryStream();
            decoder.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming standard LZ4 frame compression.
    /// <para>流式标准 LZ4 帧压缩。</para>
    /// </summary>
    /// <param name="input">
    /// Readable source copied into the frame encoder in fixed buffers until end of stream; ownership stays with the caller.
    /// <para>按固定缓冲逐块拷入帧编码器的可读源流，直至流末尾；所有权归调用方。</para></param>
    /// <param name="output">
    /// Writable destination left open after the frame end marker is written; the frame starts at its current position.
    /// <para>写出帧结束标记后保持打开的写入目标；帧数据从其当前位置开始。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read and clamped into the LZ4 0-12 range, 0 (fast) when unset.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/> 并夹取到 LZ4 的 0-12 范围，未设置时为 0（快速）。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, encoding or writing failed.
    /// <para>读取、编码或写入失败。</para></exception>
    public static void Compress(Stream input, Stream output, CompressionOptions? options = null)
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
            using var encoder = LZ4Stream.Encode(output, ToLz4Level(options?.Level), leaveOpen: true);
            input.CopyTo(encoder);
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 压缩失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental LZ4 frame writer appending its frame to a caller-owned destination.
    /// <para>在调用方持有的目标流上创建增量 LZ4 帧写出器，帧追加到该流。</para>
    /// </summary>
    /// <param name="output">Writable destination; the frame starts at its current position.
    /// <para>可写目标流；帧从其当前位置开始。</para></param>
    /// <param name="options">Only <see cref="CompressionOptions.Level"/> is read and clamped into the LZ4 0-12 range.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/> 并夹取到 LZ4 的 0-12 范围。</para></param>
    /// <returns>A writable frame encoder stream whose dispose writes the end marker without closing <paramref name="output"/>.
    /// <para>可写帧编码器流；释放时写出结束标记且不关闭 <paramref name="output"/>。</para></returns>
    internal static Stream CreateCompressionStream(Stream output, CompressionOptions? options)
        => LZ4Stream.Encode(new NonDisposingStream(output), ToLz4Level(options?.Level), leaveOpen: true);

    /// <summary>
    /// Streaming standard LZ4 frame decompression.
    /// <para>流式标准 LZ4 帧解压。</para>
    /// </summary>
    /// <param name="input">
    /// Readable LZ4 frame consumed block by block; the stream is left open at the offset the frame ended at, so a concatenated payload continues from there.
    /// <para>逐块读取的可读 LZ4 帧；流保持打开，并停在当前帧结束的位置，串接的数据可从该处继续。</para></param>
    /// <param name="output">
    /// Writable destination with no size cap, so an oversized payload is written out in full; it is not closed.
    /// <para>不设上限的写入目标，因此过大的数据会被完整写出；不会被本调用关闭。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The frame is malformed, truncated, or writing to <paramref name="output"/> failed.
    /// <para>帧格式非法、被截断，或写入 <paramref name="output"/> 失败。</para></exception>
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
            using var decoder = LZ4Stream.Decode(input, leaveOpen: true);
            decoder.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 解压失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental LZ4 frame reader over a caller-owned frame.
    /// <para>在调用方持有的帧数据上创建增量 LZ4 帧读取器。</para>
    /// </summary>
    /// <param name="input">Readable LZ4 frame positioned at its magic; only the bytes the frame requires are consumed.
    /// <para>定位在魔数处的可读 LZ4 帧；仅消费帧所需的字节。</para></param>
    /// <returns>A readable stream of decoded bytes that stops at the end of the current frame and leaves <paramref name="input"/> open when disposed.
    /// <para>解码字节的可读流，在当前帧末尾停止，释放时不关闭 <paramref name="input"/>。</para></returns>
    internal static Stream CreateDecompressionStream(Stream input)
        => LZ4Stream.Decode(new NonDisposingStream(input), leaveOpen: true);

    /// <summary>
    /// Streaming standard LZ4 frame decompression with output limit (decompression bomb protection).
    /// <para>流式标准 LZ4 帧解压，带输出上限（防解压炸弹）。</para>
    /// </summary>
    /// <param name="source">
    /// Readable LZ4 frame decoded through an 80 KiB pooled buffer; the stream is left open.
    /// <para>可读的 LZ4 帧，使用 80 KiB 池化缓冲逐块解码；流保持打开。</para></param>
    /// <param name="destination">
    /// Writable destination; the cumulative check runs before each write, so bytes beyond the budget never reach it.
    /// <para>写入目标；每次写入前先检查累计字节数，超出预算的字节不会落入该流。</para></param>
    /// <param name="maxOutputBytes">
    /// Inclusive cap on the decoded total; <c>0</c> allows an empty result only.
    /// <para>解压总量的含上限；<c>0</c> 表示只允许空结果。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="destination"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxOutputBytes"/> is negative.
    /// <para><paramref name="maxOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="CompressionException">The decoded size would exceed <paramref name="maxOutputBytes"/>, the frame is malformed, or writing failed.
    /// <para>解压结果将超过 <paramref name="maxOutputBytes"/>、帧格式非法，或写入失败。</para></exception>
    public static void Decompress(Stream source, Stream destination, long maxOutputBytes)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (maxOutputBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        }

        try
        {
            using var decoder = LZ4Stream.Decode(source, leaveOpen: true);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalWritten = 0;
                int bytesRead;
                while ((bytesRead = decoder.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (totalWritten + bytesRead > maxOutputBytes)
                    {
                        throw new CompressionException(
                            $"LZ4 解压输出超过上限 ({maxOutputBytes} 字节) / Decompressed output exceeds limit ({maxOutputBytes} bytes)");
                    }

                    destination.Write(buffer, 0, bytesRead);
                    totalWritten += bytesRead;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 解压失败", ex);
        }
    }


    /// <summary>
    /// LZ4 legacy compression (0x02 0x21 0x4C 0x18 magic + 4-byte block length prefixes).
    /// <para>LZ4 传统格式压缩（0x02 0x21 0x4C 0x18 魔数 + 4 字节块长前缀）。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext compressed in one pass; an empty array yields the magic alone.
    /// <para>一次性压缩的明文字节；空数组只产生魔数。</para></param>
    /// <param name="options">
    /// <see cref="CompressionOptions.Level"/> is clamped into the LZ4 0-12 range, 0 (fast) when unset; <see cref="CompressionOptions.MaxDegreeOfParallelism"/> bounds how many 64 KiB blocks the pipeline encodes concurrently and leaves the bytes unchanged.
    /// <para><see cref="CompressionOptions.Level"/> 夹取到 LZ4 的 0-12 范围，未设置时为 0（快速）；<see cref="CompressionOptions.MaxDegreeOfParallelism"/> 限制流水线并发压缩的 64 KiB 块数量，输出字节保持不变。</para></param>
    /// <returns>
    /// The legacy block frame, whose blocks carry no checksum and no end marker.
    /// <para>传统块帧；其块不带校验和，也没有结束标记。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The block encoder failed.
    /// <para>块编码器执行失败。</para></exception>
    public static byte[] CompressLegacy(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var output = new MemoryStream();
            CompressLegacy(new MemoryStream(data, writable: false), output, options);
            return output.ToArray();
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4_LEGACY 压缩失败", ex);
        }
    }

    /// <summary>
    /// Streaming LZ4 legacy compression: reads 64 KiB chunks, compresses and writes them in a bounded-memory pipeline directly to the output stream.
    /// <para>流式 LZ4 传统格式压缩：按 64 KiB 块边读边压（有界内存），直接写入输出流。</para>
    /// </summary>
    /// <param name="input">
    /// Readable source drained in 64 KiB blocks until end of stream; ownership stays with the caller.
    /// <para>按 64 KiB 块读取直至流末尾的可读源流；所有权归调用方。</para></param>
    /// <param name="output">
    /// Writable destination receiving the magic and then, per block, a little-endian 4-byte compressed length and the encoded bytes; it is never closed here.
    /// <para>写入目标：先写魔数，随后每块写入小端 4 字节压缩长度和编码字节；本调用不会关闭该流。</para></param>
    /// <param name="options">
    /// <see cref="CompressionOptions.Level"/> is clamped into the LZ4 0-12 range, 0 (fast) when unset; <see cref="CompressionOptions.MaxDegreeOfParallelism"/> above 1 lets that many blocks be read and encoded concurrently, and blocks are still written in input order, byte-identical to the sequential run.
    /// <para><see cref="CompressionOptions.Level"/> 夹取到 LZ4 的 0-12 范围，未设置时为 0（快速）；<see cref="CompressionOptions.MaxDegreeOfParallelism"/> 大于 1 时允许并发读取并编码相应数量的块，块仍按输入顺序写出，与串行结果逐字节一致。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, encoding or writing failed.
    /// <para>读取、编码或写入失败。</para></exception>
    public static void CompressLegacy(Stream input, Stream output, CompressionOptions? options = null)
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
            CompressBlockFrame(input, output, [0x02, 0x21, 0x4C, 0x18], ToLz4Level(options?.Level), options?.MaxDegreeOfParallelism);
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4_LEGACY 压缩失败", ex);
        }
    }

    /// <summary>
    /// LZ4 legacy decompression.
    /// <para>LZ4 传统格式解压。</para>
    /// </summary>
    /// <param name="data">
    /// Complete legacy payload, treated as untrusted input; the whole expansion is buffered in memory.
    /// <para>完整的传统格式数据，按不可信输入处理；解压结果整体驻留内存。</para></param>
    /// <returns>
    /// The decoded bytes of every block up to the end of the payload.
    /// <para>直到数据末尾各块解码后的字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The magic is missing or truncated, a block header or body is incomplete, a block length is illegal or beyond the 8 MiB per-block safety cap, or a block fails to decode.
    /// <para>魔数缺失或被截断、块头或块体不完整、块长度非法或超过 8 MiB 的单块安全上限，或块解码失败。</para></exception>
    public static byte[] DecompressLegacy(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var output = new MemoryStream();
            DecompressLegacy(new MemoryStream(data, writable: false), output);
            return output.ToArray();
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4_LEGACY 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming LZ4 legacy decompression: reads and decodes block by block (bounded memory), writing directly to the output stream.
    /// <para>流式 LZ4 传统格式解压：按块读取并解码（有界内存），直接写入输出流。</para>
    /// </summary>
    /// <param name="input">
    /// Readable legacy payload positioned at its magic; blocks are read one at a time, so the caller owns the stream and it is not closed. A length header read as zero bytes ends the payload cleanly, a partially readable header ends it as an error.
    /// <para>定位在魔数处的可读传统格式数据；逐块读取，流由调用方持有且不会关闭。长度头读到 0 字节表示数据正常结束，只能读到不完整的头则按错误处理。</para></param>
    /// <param name="output">
    /// Writable destination fed block by block; decoded bytes are written as soon as each block is valid, so a late failure leaves the earlier blocks in place. There is no cap on the total output, only the 8 MiB per-block bound.
    /// <para>逐块写入的目标；每块通过校验后立即写出，因此后续块失败时前面的块已保留。总输出没有上限，仅有 8 MiB 的单块上限。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The magic is missing or truncated, a block header or body is incomplete, a block length is illegal or beyond the 8 MiB per-block safety cap, or a block fails to decode.
    /// <para>魔数缺失或被截断、块头或块体不完整、块长度非法或超过 8 MiB 的单块安全上限，或块解码失败。</para></exception>
    public static void DecompressLegacy(Stream input, Stream output)
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

            var magic = new byte[4];
            var magicRead = ReadUpTo(input, magic);
            if (magicRead < 4)
            {
                throw new CompressionException("LZ4_LEGACY 数据格式无效：魔数截断");
            }

            if (magic[0] != 0x02 || magic[1] != 0x21 || magic[2] != 0x4C || magic[3] != 0x18)
            {
                throw new CompressionException("LZ4_LEGACY 数据格式无效：缺少魔数");
            }

            var lengthBuf = new byte[4];
            byte[]? pooled = null;
            byte[]? block = null;
            try
            {
                while (true)
                {
                    var lenRead = ReadUpTo(input, lengthBuf);
                    if (lenRead == 0)
                    {
                        break;
                    }
                    if (lenRead < 4)
                    {
                        throw new CompressionException("LZ4_LEGACY 数据格式无效：块长度头截断");
                    }

                    var blockLength = lengthBuf[0] | (lengthBuf[1] << 8) | (lengthBuf[2] << 16) | (lengthBuf[3] << 24);
                    if (blockLength <= 0)
                    {
                        throw new CompressionException("LZ4_LEGACY 数据格式无效：块长度非法");
                    }
                    const int maxDecompressed = 8 * 1024 * 1024;
                    var maximumBlockLength = Lz4BlockCodec.MaximumOutputSize(maxDecompressed);
                    if (blockLength > maximumBlockLength)
                    {
                        throw new CompressionException($"LZ4_LEGACY 数据格式无效：块长度超过安全上限 {maximumBlockLength}");
                    }


                    if (block == null || block.Length < blockLength)
                    {
                        if (block != null)
                        {
                            ArrayPool<byte>.Shared.Return(block);
                        }

                        block = ArrayPool<byte>.Shared.Rent(blockLength);
                    }
                    var blockRead = ReadExactly(input, block, blockLength);
                    if (blockRead < blockLength)
                    {
                        throw new CompressionException("LZ4_LEGACY 数据格式无效：块数据截断");
                    }


                    if (pooled == null || pooled.Length < maxDecompressed)
                    {
                        if (pooled != null)
                        {
                            ArrayPool<byte>.Shared.Return(pooled);
                        }

                        pooled = ArrayPool<byte>.Shared.Rent(maxDecompressed);
                    }
                    var decoded = Lz4BlockCodec.Decode(block.AsSpan(0, blockLength), pooled.AsSpan(0, maxDecompressed));
                    if (decoded <= 0)
                    {
                        throw new CompressionException("LZ4_LEGACY 数据格式无效：块解码失败");
                    }

                    output.Write(pooled, 0, decoded);
                }
            }
            finally
            {
                if (pooled != null)
                {
                    ArrayPool<byte>.Shared.Return(pooled);
                }

                if (block != null)
                {
                    ArrayPool<byte>.Shared.Return(block);
                }
            }
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4_LEGACY 解压失败", ex);
        }
    }


    /// <summary>
    /// Detects whether the data is a standard LZ4 frame (magic 04 22 4D 18).
    /// <para>检测数据是否为标准 LZ4 帧（魔数 04 22 4D 18）。</para>
    /// </summary>
    /// <param name="data">
    /// Leading bytes of a candidate payload.
    /// <para>候选数据的前若干字节。</para></param>
    /// <returns>
    /// <see langword="true"/> when four or more bytes start with the frame magic; the frame descriptor, block data and checksum are not examined, so a positive result is a hint rather than a validation.
    /// <para>字节数不少于四个且以帧魔数开头时为 <see langword="true"/>；不检查帧描述符、块数据和校验和，因此命中只是提示而非校验通过。</para></returns>
    public static bool IsLz4Format(byte[] data)
    {
        return data is { Length: >= 4 } &&
               data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18;
    }

    /// <summary>
    /// Detects whether the data is in LZ4 legacy format (magic 02 21 4C 18).
    /// <para>检测数据是否为 LZ4 传统格式（魔数 02 21 4C 18）。</para>
    /// </summary>
    /// <param name="data">
    /// Leading bytes of a candidate payload.
    /// <para>候选数据的前若干字节。</para></param>
    /// <returns>
    /// <see langword="true"/> when four or more bytes start with the legacy magic; block headers beyond the magic are not examined.
    /// <para>字节数不少于四个且以传统格式魔数开头时为 <see langword="true"/>；魔数之后的块头不做检查。</para></returns>
    public static bool IsLz4LegacyFormat(byte[] data)
    {
        return data is { Length: >= 4 } &&
               data[0] == 0x02 && data[1] == 0x21 && data[2] == 0x4C && data[3] == 0x18;
    }

    private static LZ4Level ToLz4Level(int? level)
    {
        if (!level.HasValue)
        {
            return LZ4Level.L00_FAST;
        }

        var v = level.Value;
        return (LZ4Level)(v < 0 ? 0 : v > 12 ? 12 : v);
    }

    /// <summary>
    /// Shared LZ4 block-frame streaming compression: writes the magic then reads and compresses 64 KiB chunks in a bounded-memory pipeline.
    /// <para>共享的 LZ4 块帧流式压缩：写魔数后按 64 KiB 块边读边压（有界内存）， 每块独立压缩（无跨块依赖），支持有界窗口并行；按块序拼接，输出与串行逐字节一致。</para>
    /// </summary>
    private static void CompressBlockFrame(Stream input, Stream output, byte[] magic, LZ4Level level, int? maxDegreeOfParallelism)
    {
        output.Write(magic, 0, magic.Length);

        var dop = Parallelism.Resolve(maxDegreeOfParallelism, int.MaxValue);
        var chunks = new List<byte[]>(dop);

        while (true)
        {

            chunks.Clear();
            while (chunks.Count < dop)
            {
                var chunk = new byte[BlockChunkSize];
                var read = ReadUpTo(input, chunk);
                if (read == 0)
                {
                    break;
                }

                if (read < chunk.Length)
                {
                    Array.Resize(ref chunk, read);
                }

                chunks.Add(chunk);
            }
            if (chunks.Count == 0)
            {
                break;
            }


            var compressed = new byte[chunks.Count][];
            var sizes = new int[chunks.Count];
            if (chunks.Count > 1 && dop > 1)
            {
                Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
                {
                    var buf = new byte[Lz4BlockCodec.MaximumOutputSize(chunks[i].Length)];
                    sizes[i] = Lz4BlockCodec.Encode(chunks[i], buf, (int)level);
                    compressed[i] = buf;
                });
            }
            else
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    var buf = new byte[Lz4BlockCodec.MaximumOutputSize(chunks[i].Length)];
                    sizes[i] = Lz4BlockCodec.Encode(chunks[i], buf, (int)level);
                    compressed[i] = buf;
                }
            }


            for (var i = 0; i < chunks.Count; i++)
            {
                var size = sizes[i];
                output.WriteByte((byte)(size & 0xFF));
                output.WriteByte((byte)((size >> 8) & 0xFF));
                output.WriteByte((byte)((size >> 16) & 0xFF));
                output.WriteByte((byte)((size >> 24) & 0xFF));
                output.Write(compressed[i], 0, size);
            }
        }
    }

    /// <summary>
    /// Reads up to buffer.Length bytes; returns the actual count read (may be short at EOF).
    /// <para>尽力读满 buffer（遇到 EOF 时返回实际读取字节数）。</para>
    /// </summary>
    private static int ReadUpTo(Stream input, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = input.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read;
    }

    /// <summary>
    /// Reads exactly up to count bytes; returns the actual count read (may be short at EOF).
    /// <para>精确读取至多 count 字节（遇到 EOF 时返回实际读取字节数）。</para>
    /// </summary>
    private static int ReadExactly(Stream input, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = input.Read(buffer, read, count - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read;
    }
}