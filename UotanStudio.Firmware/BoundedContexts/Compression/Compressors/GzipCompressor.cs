using System.Buffers;
using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.Compression.Internal;

namespace UotanStudio.Firmware.BoundedContexts.Compression.Compressors;

/// <summary>
/// GZIP codec built on the BCL stream, emitting the 10-byte header, deflate body and CRC-32/ISIZE trailer.
/// <para>基于 BCL 流的 GZIP 编解码器，写出 10 字节头、deflate 主体以及 CRC-32/ISIZE 尾部。</para>
/// </summary>
public static class GzipCompressor
{
    /// <summary>
    /// GZIP compression.
    /// <para>GZIP 压缩。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext compressed in one pass.
    /// <para>一次性压缩的明文字节。</para></param>
    /// <param name="options">
    /// <see cref="CompressionOptions.Level"/> selects the BCL level band (0 and below no compression, 1-3 fastest, 4-8 optimal, 9 and above smallest size, unset optimal); <see cref="CompressionOptions.MaxDegreeOfParallelism"/> above 1 encodes independent 1 MiB chunks in parallel and concatenates the resulting members.
    /// <para><see cref="CompressionOptions.Level"/> 决定 BCL 等级档位（0 及以下不压缩、1-3 最快、4-8 最优、9 及以上最小，未设置按最优）；<see cref="CompressionOptions.MaxDegreeOfParallelism"/> 大于 1 时并行压缩相互独立的 1 MiB 块，并串接生成的成员。</para></param>
    /// <returns>
    /// A gzip payload, or several concatenated members when the parallel chunk path was taken.
    /// <para>gzip 数据；走并行分块路径时为多个串接的成员。</para></returns>
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
                (start, count) => CompressChunk(data, start, count, options));
            if (parallel != null)
            {
                return parallel;
            }

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("GZIP 压缩失败", ex);
        }
    }

    /// <summary>
    /// Compresses [start, start+count) into an independent gzip member (for parallel chunking).
    /// <para>把 [start, start+count) 压缩为独立的 gzip 成员（并行分块用）。</para>
    /// </summary>
    private static byte[] CompressChunk(byte[] data, int start, int count, CompressionOptions? options)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true))
        {
            gzip.Write(data, start, count);
        }
        return output.ToArray();
    }

    /// <summary>
    /// GZIP decompression.
    /// <para>GZIP 解压。</para>
    /// </summary>
    /// <param name="data">
    /// Complete gzip payload, treated as untrusted input; the whole expansion is buffered in memory and the stored CRC-32 and ISIZE trailer fields are verified by the reader.
    /// <para>完整的 gzip 数据，按不可信输入处理；解压结果整体驻留内存，读取器会校验尾部的 CRC-32 与 ISIZE 字段。</para></param>
    /// <returns>
    /// The decoded bytes, with no output cap applied.
    /// <para>解码后的字节，不施加输出上限。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The header is malformed, the body is corrupt, the checksum does not match, or the payload is truncated.
    /// <para>头部非法、主体损坏、校验和不匹配或数据被截断。</para></exception>
    public static byte[] Decompress(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("GZIP 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming GZIP compression.
    /// <para>流式 GZIP 压缩。</para>
    /// </summary>
    /// <param name="input">
    /// Readable source copied into the encoder in fixed buffers until end of stream; ownership stays with the caller.
    /// <para>按固定缓冲逐块拷入编码器的可读源流，直至流末尾；所有权归调用方。</para></param>
    /// <param name="output">
    /// Writable destination left open after the header, deflate body and trailer have been written; the gzip payload starts at its current position.
    /// <para>写出头部、deflate 主体和尾部后保持打开的写入目标；gzip 数据从其当前位置开始。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read; a single member is produced, so <see cref="CompressionOptions.MaxDegreeOfParallelism"/> is ignored.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/>；流式路径只生成单个成员，因此忽略 <see cref="CompressionOptions.MaxDegreeOfParallelism"/>。</para></param>
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
            using var gzip = new GZipStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true);
            input.CopyTo(gzip);
        }
        catch (Exception ex)
        {
            throw new CompressionException("GZIP 压缩失败", ex);
        }
    }

    /// <summary>
    /// Streaming GZIP decompression.
    /// <para>流式 GZIP 解压。</para>
    /// </summary>
    /// <param name="input">
    /// Readable gzip payload consumed in fixed buffers; the stream is left open and stops where the reader finished decoding.
    /// <para>按固定缓冲读取的可读 gzip 数据；流保持打开，停在读取器解码完成的位置。</para></param>
    /// <param name="output">
    /// Writable destination with no size cap, so an oversized payload is written out in full; it is not closed.
    /// <para>不设上限的写入目标，因此过大的数据会被完整写出；不会被本调用关闭。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The payload is malformed or truncated, or writing to <paramref name="output"/> failed.
    /// <para>数据格式非法、被截断，或写入 <paramref name="output"/> 失败。</para></exception>
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
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            gzip.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("GZIP 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming GZIP decompression with output limit (decompression bomb protection).
    /// <para>流式 GZIP 解压，带输出上限（防解压炸弹）。</para>
    /// </summary>
    /// <param name="source">
    /// Readable gzip payload decoded through an 80 KiB pooled buffer; the stream is left open.
    /// <para>可读的 gzip 数据，使用 80 KiB 池化缓冲逐块解码；流保持打开。</para></param>
    /// <param name="destination">
    /// Writable destination; the cumulative check runs before each write, so bytes beyond the budget never reach it.
    /// <para>写入目标；每次写入前先检查累计字节数，超出预算的字节不会落入该流。</para></param>
    /// <param name="maxOutputBytes">
    /// Inclusive cap on the decoded total, checked against the actual expansion rather than the four-byte ISIZE field, which is capped at 4 GiB by the format; <c>0</c> allows an empty result only.
    /// <para>解压总量的含上限，按实际展开字节检查，而非格式中最大仅 4 GiB 的 ISIZE 字段；<c>0</c> 表示只允许空结果。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="destination"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxOutputBytes"/> is negative.
    /// <para><paramref name="maxOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="CompressionException">The decoded size would exceed <paramref name="maxOutputBytes"/>, the payload is corrupt or truncated, or writing failed.
    /// <para>解压结果将超过 <paramref name="maxOutputBytes"/>、数据损坏或截断，或写入失败。</para></exception>
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
            using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalWritten = 0;
                int bytesRead;
                while ((bytesRead = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (totalWritten + bytesRead > maxOutputBytes)
                    {
                        throw new CompressionException(
                            $"GZIP 解压输出超过上限 ({maxOutputBytes} 字节) / Decompressed output exceeds limit ({maxOutputBytes} bytes)");
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
            throw new CompressionException("GZIP 解压失败", ex);
        }
    }

    /// <summary>
    /// Detects whether the data is in gzip format (magic 1F 8B).
    /// <para>检测数据是否为 gzip 格式（魔数 1F 8B）。</para>
    /// </summary>
    /// <param name="data">
    /// Leading bytes of a candidate payload.
    /// <para>候选数据的前若干字节。</para></param>
    /// <returns>
    /// <see langword="true"/> when at least two bytes are present and start with <c>1F 8B</c>; <see langword="false"/> for a <see langword="null"/> or shorter buffer. The compression-method byte that follows is not checked.
    /// <para>缓冲区至少有两个字节且以 <c>1F 8B</c> 开头时为 <see langword="true"/>；<see langword="null"/> 或更短的缓冲区为 <see langword="false"/>。其后表示压缩方法的字节不做校验。</para></returns>
    public static bool IsGzipFormat(byte[] data) => data is { Length: >= 2 } && data[0] == 0x1F && data[1] == 0x8B;
}