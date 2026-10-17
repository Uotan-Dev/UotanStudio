using System.Buffers;
using System.IO.Compression;
using UotanStudio.Compress.Internal;
using SCCompressionMode = SharpCompress.Compressors.CompressionMode;
using SCDeflateStream = SharpCompress.Compressors.Deflate.DeflateStream;

namespace UotanStudio.Compress.Compressors;

/// <summary>
/// Raw DEFLATE codec (RFC 1951 bit stream with no zlib or gzip wrapper) that reads concatenated members back-to-back.
/// <para>原始 DEFLATE 编解码器（RFC 1951 比特流，不含 zlib 或 gzip 包装），读取时会连续处理串接的成员。</para>
/// </summary>
public static class DeflateCompressor
{
    /// <summary>
    /// DEFLATE compression.
    /// <para>DEFLATE 压缩。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext compressed in one pass.
    /// <para>一次性压缩的明文字节。</para></param>
    /// <param name="options">
    /// <see cref="CompressionOptions.Level"/> selects the BCL level band (0 and below no compression, 1-3 fastest, 4-8 optimal, 9 and above smallest size, unset optimal); <see cref="CompressionOptions.MaxDegreeOfParallelism"/> above 1 encodes independent 1 MiB chunks in parallel and concatenates them as separate members.
    /// <para><see cref="CompressionOptions.Level"/> 决定 BCL 等级档位（0 及以下不压缩、1-3 最快、4-8 最优、9 及以上最小，未设置按最优）；<see cref="CompressionOptions.MaxDegreeOfParallelism"/> 大于 1 时并行压缩相互独立的 1 MiB 块，并作为多个成员串接。</para></param>
    /// <returns>
    /// A raw deflate stream, or several concatenated streams when the parallel chunk path was taken.
    /// <para>原始 deflate 流；走并行分块路径时为多个串接的流。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The encoder failed.
    /// <para>编码器执行失败。</para></exception>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {

            var parallel = ParallelCompression.TryCompressChunks(data, options?.MaxDegreeOfParallelism,
                (start, count, destination) => CompressChunk(data, start, count, options, destination));
            if (parallel != null)
            {
                return parallel;
            }

            using var output = new MemoryStream(Math.Min(data.Length, ParallelCompression.DefaultChunkSize));
            using (var deflate = new DeflateStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 压缩失败", ex);
        }
    }

    /// <summary>
    /// Compresses [start, start+count) into an independent raw deflate stream (for parallel chunking).
    /// <para>把 [start, start+count) 压缩为独立的 raw deflate 流（并行分块用）。</para>
    /// </summary>
    private static void CompressChunk(byte[] data, int start, int count, CompressionOptions? options, Stream destination)
    {
        using (var deflate = new DeflateStream(destination, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true))
        {
            deflate.Write(data, start, count);
        }
    }

    /// <summary>
    /// DEFLATE decompression.
    /// <para>DEFLATE 解压。</para>
    /// </summary>
    /// <param name="data">
    /// One or more concatenated raw deflate members; the whole expansion is buffered in memory with no output cap.
    /// <para>一个或多个串接的原始 deflate 成员；解压结果整体驻留内存且没有上限。</para></param>
    /// <returns>
    /// The decoded bytes of every member, in member order.
    /// <para>按成员顺序拼接的全部解码字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">A member is corrupt, truncated, or consumes no input byte, which leaves the member boundary undetermined.
    /// <para>某个成员损坏、截断，或未消耗任何输入字节因而无法确定成员边界。</para></exception>
    public static byte[] Decompress(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var output = new MemoryStream();
            var offset = 0;


            while (offset < data.Length)
            {
                using var sub = new MemoryStream(data, offset, data.Length - offset, writable: false);
                using var deflate = new SCDeflateStream(sub, SCCompressionMode.Decompress);
                deflate.CopyTo(output);

                var consumed = deflate.TotalIn;
                if (consumed <= 0)
                {
                    throw new CompressionException("DEFLATE 数据格式无效：无法定位成员边界");
                }

                offset += (int)consumed;
            }

            return output.ToArray();
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming DEFLATE compression.
    /// <para>流式 DEFLATE 压缩。</para>
    /// </summary>
    /// <param name="input">
    /// Readable source copied into the encoder in fixed buffers until end of stream; ownership stays with the caller.
    /// <para>按固定缓冲逐块拷入编码器的可读源流，直至流末尾；所有权归调用方。</para></param>
    /// <param name="output">
    /// Writable destination left open after the terminating deflate block is flushed; the encoded bytes start at its current position.
    /// <para>写出结束 deflate 块并刷新后保持打开的写入目标；编码数据从其当前位置开始写出。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read; a single stream is produced, so <see cref="CompressionOptions.MaxDegreeOfParallelism"/> is ignored.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/>；流式路径只生成单个流，因此忽略 <see cref="CompressionOptions.MaxDegreeOfParallelism"/>。</para></param>
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
            using var deflate = new DeflateStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true);
            input.CopyTo(deflate);
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 压缩失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental raw deflate writer appending its stream to a caller-owned destination.
    /// <para>在调用方持有的目标流上创建增量原始 deflate 写出器，编码流追加到该流。</para>
    /// </summary>
    /// <param name="output">
    /// Writable destination; the encoded bytes start at its current position.
    /// <para>可写目标流；编码字节从其当前位置开始。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read; the other members have no effect because one incremental stream cannot be chunked.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/>；单个增量流无法分块，其他成员不生效。</para></param>
    /// <returns>
    /// A writable encoder stream whose dispose flushes the terminating deflate block into <paramref name="output"/> without closing it.
    /// <para>可写编码器流；释放时把结束 deflate 块刷入 <paramref name="output"/> 但不关闭该流。</para></returns>
    internal static Stream CreateCompressionStream(Stream output, CompressionOptions? options)
        => new DeflateStream(new NonDisposingStream(output), CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true);

    /// <summary>
    /// Streaming DEFLATE decompression.
    /// <para>流式 DEFLATE 解压。</para>
    /// </summary>
    /// <param name="input">
    /// Readable deflate stream. When it supports seeking, concatenated members are decoded one after another and the position ends after the last member that fits; a non-seekable stream yields a single member and stops wherever the decoder ran out of input. The stream is left open.
    /// <para>可读的 deflate 流。支持定位时会依次解码串接的多个成员，位置停在最后一个完整成员之后；不支持定位时只解出单个成员，并在解码器读尽输入处停止。流保持打开。</para></param>
    /// <param name="output">
    /// Writable destination for the decoded bytes, written in fixed buffers with no size cap; it is not closed.
    /// <para>解码字节的写入目标，按固定缓冲写出不设大小上限，不会被本调用关闭。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">A member is corrupt or truncated, or writing to <paramref name="output"/> failed.
    /// <para>某个成员损坏、截断，或写入 <paramref name="output"/> 失败。</para></exception>
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
            if (input.CanSeek)
            {


                while (true)
                {
                    var memberStart = input.Position;
                    using var deflate = new SCDeflateStream(new NonDisposingStream(input), SCCompressionMode.Decompress);
                    deflate.CopyTo(output);

                    var consumed = deflate.TotalIn;
                    if (consumed <= 0)
                    {
                        break;
                    }

                    var next = memberStart + consumed;
                    if (next >= input.Length)
                    {
                        break;
                    }

                    input.Position = next;
                }
            }
            else
            {


                using var deflate = new SCDeflateStream(new NonDisposingStream(input), SCCompressionMode.Decompress);
                deflate.CopyTo(output);
            }
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental raw deflate reader over a caller-owned payload.
    /// <para>在调用方持有的数据流上创建增量原始 deflate 读取器。</para>
    /// </summary>
    /// <param name="input">
    /// Readable raw deflate stream positioned at its first byte, consumed only as far as the decoder reads.
    /// <para>定位在首字节处的可读原始 deflate 流，解码器读到多少就消费多少。</para></param>
    /// <returns>
    /// A readable stream of decoded bytes stopping at the end of the first member, unlike the array overload that walks every concatenated member; disposing the stream leaves <paramref name="input"/> open.
    /// <para>解码字节的可读流，在首个成员末尾停止（与遍历全部串联成员的数组重载不同）；释放该流不会关闭 <paramref name="input"/>。</para></returns>
    internal static Stream CreateDecompressionStream(Stream input)
        => new SCDeflateStream(new NonDisposingStream(input), SCCompressionMode.Decompress);

    /// <summary>
    /// Streaming DEFLATE decompression with output limit (decompression bomb protection).
    /// <para>流式 DEFLATE 解压，带输出上限（防解压炸弹）。</para>
    /// </summary>
    /// <param name="source">
    /// Readable deflate stream decoded member by member through an 80 KiB pooled buffer; seeking behaves as in the unbounded overload and the stream is left open.
    /// <para>可读的 deflate 流，使用 80 KiB 池化缓冲逐成员解码；定位行为与无上限重载一致，流保持打开。</para></param>
    /// <param name="destination">
    /// Writable destination; the cumulative check runs before each write, so bytes beyond the budget never reach it.
    /// <para>写入目标；每次写入前先检查累计字节数，超出预算的字节不会落入该流。</para></param>
    /// <param name="maxOutputBytes">
    /// Inclusive cap summed across all decoded members, not per member; <c>0</c> allows an empty result only.
    /// <para>对所有已解码成员求和后的含上限，而非单个成员的上限；<c>0</c> 表示只允许空结果。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="destination"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxOutputBytes"/> is negative.
    /// <para><paramref name="maxOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="CompressionException">The decoded size would exceed <paramref name="maxOutputBytes"/>, a member is corrupt or truncated, or writing failed.
    /// <para>解压结果将超过 <paramref name="maxOutputBytes"/>、成员损坏或截断，或写入失败。</para></exception>
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
            long totalWritten = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                if (source.CanSeek)
                {


                    while (true)
                    {
                        var memberStart = source.Position;
                        using var deflate = new SCDeflateStream(new NonDisposingStream(source), SCCompressionMode.Decompress);
                        totalWritten = BoundedCopy(deflate, destination, buffer, totalWritten, maxOutputBytes);

                        var consumed = deflate.TotalIn;
                        if (consumed <= 0)
                        {
                            break;
                        }

                        var next = memberStart + consumed;
                        if (next >= source.Length)
                        {
                            break;
                        }

                        source.Position = next;
                    }
                }
                else
                {


                    using var deflate = new SCDeflateStream(new NonDisposingStream(source), SCCompressionMode.Decompress);
                    BoundedCopy(deflate, destination, buffer, totalWritten, maxOutputBytes);
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
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }

    /// <summary>
    /// Reads from source into destination, throwing if maxOutputBytes is exceeded.
    /// <para>从 source 读取数据写入 destination，超出 maxOutputBytes 则抛异常。</para>
    /// </summary>
    private static long BoundedCopy(Stream source, Stream destination, byte[] buffer, long totalWritten, long maxOutputBytes)
    {
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (totalWritten + bytesRead > maxOutputBytes)
            {
                throw new CompressionException(
                    $"DEFLATE 解压输出超过上限 ({maxOutputBytes} 字节) / Decompressed output exceeds limit ({maxOutputBytes} bytes)");
            }

            destination.Write(buffer, 0, bytesRead);
            totalWritten += bytesRead;
        }

        return totalWritten;
    }
}