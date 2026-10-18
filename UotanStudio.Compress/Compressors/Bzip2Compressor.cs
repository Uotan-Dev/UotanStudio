using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using UotanStudio.Compress.Internal;

namespace UotanStudio.Compress.Compressors;

/// <summary>
/// BZip2 codec writing the standard <c>BZh9</c> stream layout at the largest 900 kB block setting; concatenation of independent streams is supported on read.
/// <para>BZip2 编解码器，按标准 <c>BZh9</c> 流布局以最大 900 kB 块设置写出；读取时支持独立流的串接。</para>
/// </summary>
internal static class Bzip2Compressor
{
    /// <summary>BZIP2 compression.<para>BZIP2 压缩。</para></summary>
    /// <param name="data">Plaintext to compress, taken in full from the array.
    /// <para>待压缩的明文字节，整体取自该数组。</para></param>
    /// <param name="options">Only <see cref="CompressionOptions.MaxDegreeOfParallelism"/> is read: above 1 and with at least two 1 MiB chunks the input is split, each chunk encoded as an independent stream and the results concatenated in chunk order. <see cref="CompressionOptions.Level"/> is ignored because the block size is fixed.
    /// <para>仅读取 <see cref="CompressionOptions.MaxDegreeOfParallelism"/>：当值大于 1 且至少存在两个 1 MiB 块时，将输入切分、各块独立编码并按块序拼接。<see cref="CompressionOptions.Level"/> 被忽略，因为块大小固定。</para></param>
    /// <returns>One bzip2 stream, or several concatenated ones when the parallel chunk path was taken.
    /// <para>单个 bzip2 流；走并行分块路径时为多个串接的流。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The encoder failed.<para>编码器执行失败。</para></exception>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            var parallel = ParallelCompression.TryCompressChunks(data, options?.MaxDegreeOfParallelism,
                (start, count, destination) => CompressChunk(data, start, count, destination));
            if (parallel != null)
            {
                return parallel;
            }

            using var output = new MemoryStream(Math.Min(data.Length, ParallelCompression.DefaultChunkSize));
            using (var bzip2 = BZip2Stream.Create(output, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true))
            {
                bzip2.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("BZIP2 压缩失败", ex);
        }
    }

    /// <summary>
    /// Compresses [start, start+count) into an independent bzip2 stream (for parallel chunking).
    /// <para>把 [start, start+count) 压缩为独立的 bzip2 流（并行分块用）。</para>
    /// </summary>
    private static void CompressChunk(byte[] data, int start, int count, Stream destination)
    {
        using (var bzip2 = BZip2Stream.Create(destination, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true))
        {
            bzip2.Write(data, start, count);
        }
    }

    /// <summary>BZIP2 decompression.<para>BZIP2 解压。</para></summary>
    /// <param name="data">Complete bzip2 payload; concatenated streams are decoded one after another and their output merged, with no cap on the expansion size.
    /// <para>完整的 bzip2 数据；串接的多个流会依次解码并合并输出，不对解压大小设上限。</para></param>
    /// <returns>The decoded bytes of every concatenated stream.<para>所有串接流解码后的字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The payload is truncated, corrupt, or not bzip2 encoded.
    /// <para>数据被截断、损坏或并非 bzip2 编码。</para></exception>
    public static byte[] Decompress(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var input = new MemoryStream(data);
            using var bzip2 = BZip2Stream.Create(input, CompressionMode.Decompress, decompressConcatenated: true, leaveOpen: false);
            using var output = new MemoryStream();
            bzip2.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("BZIP2 解压失败", ex);
        }
    }

    /// <summary>Streaming BZIP2 compression.<para>流式 BZIP2 压缩。</para></summary>
    /// <param name="input">Readable source piped through a 64 KiB buffer, so the plaintext is never held in memory as a whole; ownership stays with the caller.
    /// <para>经 64 KiB 缓冲逐块读取的可读源流，明文不会整体驻留内存；所有权归调用方。</para></param>
    /// <param name="output">Writable destination that is not closed by this call; the stream trailer is written when the encoder is disposed, so the call only returns after the payload is complete.
    /// <para>不会被本调用关闭的写入目标；流尾结构在编码器释放时写出，因此本调用返回时数据已完整。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, encoding or writing failed.
    /// <para>读取、编码或写入失败。</para></exception>
    public static void Compress(Stream input, Stream output)
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
            using var buffered = new BufferedStream(new NonDisposingStream(output), 64 * 1024);
            using var bzip2 = BZip2Stream.Create(buffered, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true);
            input.CopyTo(bzip2);
        }
        catch (Exception ex)
        {
            throw new CompressionException("BZIP2 压缩失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental bzip2 writer appending its stream to a caller-owned destination through a 64 KiB buffer.
    /// <para>在调用方持有的目标流上创建增量 bzip2 写出器，经 64 KiB 缓冲追加。</para>
    /// </summary>
    /// <param name="output">Writable destination; the encoded stream starts at its current position.
    /// <para>可写目标流；编码流从其当前位置开始。</para></param>
    /// <returns>A writable encoder stream whose dispose writes the stream trailer, flushes the buffer and leaves <paramref name="output"/> open.
    /// <para>可写编码器流；释放时写出流尾部、刷新缓冲且不关闭 <paramref name="output"/>。</para></returns>
    internal static Stream CreateCompressionStream(Stream output)
        => BZip2Stream.Create(new BufferedStream(new NonDisposingStream(output), 64 * 1024), CompressionMode.Compress, decompressConcatenated: false, leaveOpen: false);

    /// <summary>Streaming BZIP2 decompression.<para>流式 BZIP2 解压。</para></summary>
    /// <param name="input">Readable bzip2 payload consumed through a 64 KiB buffer; concatenated streams are followed and the stream is left open at the offset decoding stopped at.
    /// <para>经 64 KiB 缓冲读取的可读 bzip2 数据；会跟随串接的多个流，解码停在哪个偏移该流就处于哪个偏移且保持打开。</para></param>
    /// <param name="output">Writable destination for the decoded bytes, written in fixed buffers with no size cap; it is not closed.
    /// <para>解码字节的写入目标，按固定缓冲写出且不设大小上限，不会被本调用关闭。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The payload is truncated or corrupt, or writing to <paramref name="output"/> failed.
    /// <para>数据被截断、损坏，或写入 <paramref name="output"/> 失败。</para></exception>
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
            using var buffered = new BufferedStream(new NonDisposingStream(input), 64 * 1024);
            using var bzip2 = BZip2Stream.Create(buffered, CompressionMode.Decompress, decompressConcatenated: true, leaveOpen: true);
            bzip2.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("BZIP2 解压失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental bzip2 reader over a caller-owned payload through a 64 KiB buffer.
    /// <para>在调用方持有的数据上创建增量 bzip2 读取器，经 64 KiB 缓冲读取。</para>
    /// </summary>
    /// <param name="input">Readable bzip2 payload positioned at its magic; the buffering layer reads ahead, so the caller's position can pass the offset decoding ended at.
    /// <para>定位在魔数处的可读 bzip2 数据；缓冲层会预读，因此调用方流上的位置可能超过解码实际结束处。</para></param>
    /// <returns>A readable stream of decoded bytes that follows concatenated streams, owns the buffering layer and leaves <paramref name="input"/> open when disposed.
    /// <para>解码字节的可读流，会跟随串接的多个流并持有缓冲层，释放时不关闭 <paramref name="input"/>。</para></returns>
    internal static Stream CreateDecompressionStream(Stream input)
        => BZip2Stream.Create(
            new BufferedStream(new NonDisposingStream(input), 64 * 1024),
            CompressionMode.Decompress,
            decompressConcatenated: true,
            leaveOpen: false);

    /// <summary>
    /// Detects whether the data is in bzip2 format (magic 42 5A 68).
    /// <para>检测数据是否为 bzip2 格式（魔数 42 5A 68）。</para>
    /// </summary>
    /// <param name="data">
    /// Leading bytes of a candidate payload; fewer than three bytes are enough to rule the format out.
    /// <para>候选数据的前若干字节；不足三个字节即可判定不是该格式。</para></param>
    /// <returns>
    /// <see langword="true"/> when the buffer starts with <c>BZh</c>; <see langword="false"/> for a <see langword="null"/> or short buffer. The block-size digit that follows is not checked.
    /// <para>缓冲区以 <c>BZh</c> 开头时为 <see langword="true"/>；<see langword="null"/> 或长度不足时为 <see langword="false"/>。其后表示块大小的数字字符不做校验。</para></returns>
    public static bool IsBzip2Format(byte[] data) => data is { Length: >= 3 } && data[0] == 0x42 && data[1] == 0x5A && data[2] == 0x68;
}