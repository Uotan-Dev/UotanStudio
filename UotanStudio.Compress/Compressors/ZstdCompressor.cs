using System.Buffers;
using UotanStudio.Compress.Internal;
using ZstdSharp;

namespace UotanStudio.Compress.Compressors;

/// <summary>
/// Zstd compression and decompression over byte arrays or streams, backed by ZstdSharp.
/// <para>基于 ZstdSharp 的 Zstd 字节数组与流式压缩解压。</para>
/// </summary>
public static class ZstdCompressor
{
    /// <summary>
    /// Zstd compression.
    /// <para>Zstd 压缩。</para>
    /// </summary>
    /// <param name="data">Plaintext buffer to compress. <para>要压缩的明文缓冲区。</para></param>
    /// <param name="options">Optional compression level and parallel-chunking degree; <see langword="null"/>
    /// uses level <c>3</c> in one frame. <para>可选压缩级别与并行分块度；为 <see langword="null"/> 时按级别 <c>3</c> 生成单帧。</para></param>
    /// <returns>The zstd frame, or concatenated per-chunk frames when parallel chunking applied.
    /// <para>zstd 帧；启用并行分块时为逐块帧的串联。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null. <para><paramref name="data"/> 为 null。</para></exception>
    /// <exception cref="CompressionException">Compression failed; the codec error is the inner exception.
    /// <para>压缩失败；编解码器错误为内部异常。</para></exception>
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

            using var compressor = new Compressor(options?.Level ?? 3);
            return compressor.Wrap(data).ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZSTD 压缩失败", ex);
        }
    }

    /// <summary>
    /// Compresses [start, start+count) into an independent zstd frame (for parallel chunking).
    /// <para>把 [start, start+count) 压缩为独立的 zstd 帧（并行分块用）。</para>
    /// </summary>
    private static byte[] CompressChunk(byte[] data, int start, int count, CompressionOptions? options)
    {
        using var compressor = new Compressor(options?.Level ?? 3);
        return compressor.Wrap(data.AsSpan(start, count)).ToArray();
    }

    /// <summary>
    /// Zstd decompression.
    /// <para>Zstd 解压。</para>
    /// </summary>
    /// <param name="data">Complete zstd frame bytes. <para>完整的 zstd 帧字节。</para></param>
    /// <returns>The decoded plaintext. <para>解码后的明文。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null. <para><paramref name="data"/> 为 null。</para></exception>
    /// <exception cref="CompressionException">The frame is malformed or decoding failed.
    /// <para>帧无效或解码失败。</para></exception>
    public static byte[] Decompress(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var decompressor = new Decompressor();
            return decompressor.Unwrap(data).ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZSTD 解压失败", ex);
        }
    }

    /// <summary>
    /// Compresses a stream with zstd, using parallel frames when the options allow it and at least two chunks are present.
    /// <para>流式执行 zstd 压缩；选项允许且至少存在两个块时改用并行独立帧。</para>
    /// </summary>
    /// <param name="input">Readable source stream, consumed to its end and left open; it does not have to be seekable, because chunks are read through a bounded look-ahead.
    /// <para>可读源流，读取到末尾并保持打开；通过有界预读分块，因此不需要可寻址。</para></param>
    /// <param name="output">Writable target stream; frames are flushed on completion but the stream is not
    /// closed. <para>可写目标流；完成时刷新帧但不关闭流。</para></param>
    /// <param name="options">Optional compression level; <see langword="null"/> uses level <c>3</c>.
    /// <para>可选压缩级别；为 <see langword="null"/> 时使用级别 <c>3</c>。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 null。</para></exception>
    /// <exception cref="CompressionException">Compression or stream copying failed. <para>压缩或流复制失败。</para></exception>
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
            if (TryCompressParallel(input, output, options))
            {
                return;
            }
            using var compressor = new CompressionStream(new NonDisposingStream(output), options?.Level ?? 3);
            input.CopyTo(compressor);
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZSTD 压缩失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental Zstandard writer appending its frame to a caller-owned destination.
    /// <para>在调用方持有的目标流上创建增量 Zstandard 写出器，帧追加到该流。</para>
    /// </summary>
    /// <param name="output">Writable destination; the frame starts at its current position.
    /// <para>可写目标流；帧从其当前位置开始。</para></param>
    /// <param name="options">Only <see cref="CompressionOptions.Level"/> is read, <see langword="null"/> selecting level <c>3</c>; parallel chunking has no effect because one incremental frame is produced.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/>，为 <see langword="null"/> 时取级别 <c>3</c>；增量写出只生成单帧，并行分块不生效。</para></param>
    /// <returns>A writable frame encoder stream whose dispose closes the frame without closing <paramref name="output"/>.
    /// <para>可写帧编码器流；释放时收尾该帧但不关闭 <paramref name="output"/>。</para></returns>
    internal static Stream CreateCompressionStream(Stream output, CompressionOptions? options)
        => new CompressionStream(new NonDisposingStream(output), options?.Level ?? 3);

    /// <summary>
    /// Compresses a stream through parallel per-chunk frames when parallelism is requested and at least two chunks
    /// are available; otherwise returns <see langword="false"/> and the caller keeps its single-frame sequential path.
    /// <para>在请求并行且至少存在两个块时，把流按块并发压缩为独立帧；否则返回 <see langword="false"/>，
    /// 调用方保持单帧串行路径。</para>
    /// </summary>
    /// <remarks>
    /// A seekable source still decides up front from its remaining length, so an input below two chunks keeps the
    /// caller's single-frame output byte for byte. A non-seekable source cannot be measured, so chunks are read
    /// through a bounded look-ahead ring instead; that is what stops the outcome from depending on whether the
    /// caller wrapped the source for progress reporting. Frames are concatenated in input order, which zstd decodes
    /// as one plaintext, so the byte stream differs from the single-frame sequential output only by carrying one
    /// frame header per chunk.
    /// <para>可寻址来源仍先按剩余长度判定，因此不足两个块的输入逐字节保持调用方的单帧输出。不可寻址来源无法
    /// 预量，改用有界预读环读取；这正是结果不再取决于调用方是否为上报进度而包装源流的原因。各帧按输入顺序
    /// 串联，zstd 会将其解码为同一份明文，因此与单帧串行输出的字节差异仅为每块各带一个帧头。</para>
    /// </remarks>
    private static bool TryCompressParallel(Stream input, Stream output, CompressionOptions? options)
    {
        if (options?.MaxDegreeOfParallelism is null or <= 1)
        {
            return false;
        }

        var requestedDegree = options.MaxDegreeOfParallelism.Value;
        var level = options.Level ?? 3;
        var chunkSize = ParallelCompression.DefaultChunkSize;

        if (input.CanSeek && TryGetRemainingLength(input, out var remaining))
        {
            var chunkCount = (int)Math.Min(int.MaxValue, (remaining + chunkSize - 1) / chunkSize);
            if (Parallelism.Resolve(requestedDegree, Math.Max(chunkCount, 1)) <= 1)
            {
                return false;
            }
        }

        return CompressParallelChunks(input, output, level, chunkSize, requestedDegree);
    }

    /// <summary>
    /// Reads the number of bytes still available from the current position of a seekable stream.
    /// <para>读取可寻址流自当前位置起仍可用的字节数。</para>
    /// </summary>
    /// <param name="input">Seekable source stream. <para>可寻址来源流。</para></param>
    /// <param name="remaining">Receives the byte count, or zero when the length cannot be read.
    /// <para>接收字节数；长度不可读时为 0。</para></param>
    /// <returns><see langword="true"/> when a non-negative remaining length was obtained.
    /// <para>取得非负的剩余长度时返回 <see langword="true"/>。</para></returns>
    private static bool TryGetRemainingLength(Stream input, out long remaining)
    {
        remaining = 0;
        try
        {
            remaining = input.Length - input.Position;
            return remaining >= 0;
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

    /// <summary>
    /// Reads chunks into a bounded ring of pooled buffers, compresses each batch with <c>Parallel.For</c>
    /// and writes the resulting frames in input order until the source is drained.
    /// <para>把块读入有界池化缓冲环，用 <c>Parallel.For</c> 压缩每个批次，并按输入顺序写出所得帧，
    /// 直到来源流读尽。</para>
    /// </summary>
    /// <param name="input">Readable source drained to its end and left open.
    /// <para>可读来源流，读取到末尾并保持打开。</para></param>
    /// <param name="output">Writable target receiving the concatenated frames in input order.
    /// <para>可写目标流，按输入顺序接收串联的各帧。</para></param>
    /// <param name="level">zstd compression level applied to every chunk.
    /// <para>作用于每个块的 zstd 压缩级别。</para></param>
    /// <param name="chunkSize">Maximum uncompressed bytes per frame; the final frame may be shorter.
    /// <para>每帧最大的未压缩字节数；最后一帧可以更短。</para></param>
    /// <param name="degree">Number of chunk slots compressed concurrently, always at least one.
    /// <para>并发压缩的块槽数量，始终至少为 1。</para></param>
    /// <returns><see langword="true"/> once every read chunk was emitted as its own frame.
    /// <para>每个已读块都作为独立帧写出后返回 <see langword="true"/>。</para></returns>
    private static bool CompressParallelChunks(Stream input, Stream output, int level, int chunkSize, int degree)
    {
        var frameBound = chunkSize + (chunkSize >> 3) + 65536 + 128;
        var buffers = new byte[degree][];
        var frames = new byte[degree][];
        var inputLengths = new int[degree];
        var frameLengths = new int[degree];
        var contexts = new Compressor[degree];
        for (var slot = 0; slot < degree; slot++)
        {
            buffers[slot] = ArrayPool<byte>.Shared.Rent(chunkSize);
            frames[slot] = ArrayPool<byte>.Shared.Rent(frameBound);
            contexts[slot] = new Compressor(level);
        }

        try
        {
            var framesWritten = 0;
            while (true)
            {
                var batch = ReadBatch(input, buffers, inputLengths, degree);
                if (batch == 0)
                {
                    break;
                }

                Parallel.For(0, batch, new ParallelOptions { MaxDegreeOfParallelism = batch }, slot =>
                {
                    var written = contexts[slot].Wrap(buffers[slot].AsSpan(0, inputLengths[slot]), frames[slot]);
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(written, frameBound - 1);
                    frameLengths[slot] = written;
                });
                for (var slot = 0; slot < batch; slot++)
                {
                    output.Write(frames[slot], 0, frameLengths[slot]);
                }

                framesWritten += batch;
                if (batch < degree)
                {
                    break;
                }
            }

            return framesWritten > 0;
        }
        finally
        {
            for (var slot = 0; slot < degree; slot++)
            {
                contexts[slot].Dispose();
                ArrayPool<byte>.Shared.Return(buffers[slot]);
                ArrayPool<byte>.Shared.Return(frames[slot]);
            }
        }
    }

    /// <summary>
    /// Fills consecutive slots with the next chunk of each, stopping at end of stream; slots that reached end of
    /// stream are left with a zero length so the helper can tell "empty chunk" from "no chunk read".
    /// <para>从流中依次填充后续若干槽位，遇到流末尾即停止；读到末尾的槽位保持长度为零，使调用方能区分
    /// "空块"与"未读到块"。</para>
    /// </summary>
    /// <param name="input">Readable source positioned at the next unread byte.
    /// <para>可读来源流，定位于下一个未读字节。</para></param>
    /// <param name="buffers">Pooled buffers indexed by slot, each at least <c>chunkSize</c> long.
    /// <para>按槽位索引的池化缓冲，每个长度不小于 <c>chunkSize</c>。</para></param>
    /// <param name="inputLengths">Receives each slot's chunk length; slots after end of stream receive zero.
    /// <para>接收各槽位的块长度；流末尾之后的槽位接收 0。</para></param>
    /// <param name="degree">Number of slots to fill at most. <para>最多填充的槽位数量。</para></param>
    /// <returns>The number of slots that received at least one byte, which is zero once the stream is drained.
    /// <para>至少收到一个字节的槽位数量；流读尽后为 0。</para></returns>
    private static int ReadBatch(Stream input, byte[][] buffers, int[] inputLengths, int degree)
    {
        var batch = 0;
        for (var slot = 0; slot < degree; slot++)
        {
            inputLengths[slot] = 0;
        }

        for (var slot = 0; slot < degree; slot++)
        {
            var read = ReadChunk(input, buffers[slot], buffers[slot].Length);
            if (read == 0)
            {
                break;
            }

            inputLengths[slot] = read;
            batch++;
        }

        return batch;
    }

    /// <summary>
    /// Reads one chunk of at most <paramref name="chunkSize"/> bytes, retrying short reads until the buffer is full
    /// or the stream ends.
    /// <para>读取至多 <paramref name="chunkSize"/> 字节的一个块，缓冲填满或流结束前重试短读。</para>
    /// </summary>
    /// <param name="input">Readable source positioned at the next unread byte.
    /// <para>可读来源流，定位于下一个未读字节。</para></param>
    /// <param name="buffer">Destination buffer. <para>目标缓冲。</para></param>
    /// <param name="chunkSize">Maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <returns>The bytes read, which is zero only at end of stream.
    /// <para>实际读到的字节数；仅在流末尾时为 0。</para></returns>
    private static int ReadChunk(Stream input, byte[] buffer, int chunkSize)
    {
        var total = 0;
        while (total < chunkSize)
        {
            var read = input.Read(buffer, total, chunkSize - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Streaming zstd decompression: pipes through ZstdSharp's DecompressionStream without full buffering, suitable for large inputs.
    /// <para>流式 Zstd 解压：基于 ZstdSharp 的 DecompressionStream 直接管道， 无需整块缓冲，适合大输入。</para>
    /// </summary>
    /// <param name="input">Readable compressed stream, consumed to its end and left open; the decoded size is
    /// unbounded, so callers handling untrusted input must cap <paramref name="output"/> themselves.
    /// <para>可读压缩流，读取到末尾并保持打开；解码大小无上限，处理不可信输入的调用方需自行限制
    /// <paramref name="output"/>。</para></param>
    /// <param name="output">Writable target stream, not closed. <para>可写目标流，不会被关闭。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 null。</para></exception>
    /// <exception cref="CompressionException">Decompression or stream copying failed. <para>解压或流复制失败。</para></exception>
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
            using var decompressor = new DecompressionStream(new NonDisposingStream(input));
            decompressor.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZSTD 解压失败", ex);
        }
    }

    /// <summary>
    /// Creates the incremental Zstandard reader over a caller-owned frame.
    /// <para>在调用方持有的帧数据上创建增量 Zstandard 读取器。</para>
    /// </summary>
    /// <param name="input">Readable zstd frame positioned at its magic; only the bytes the frame requires are consumed.
    /// <para>定位在魔数处的可读 zstd 帧；仅消费帧所需的字节。</para></param>
    /// <returns>A readable stream of decoded bytes that never buffers the expansion as a whole and leaves <paramref name="input"/> open when disposed.
    /// <para>解码字节的可读流，解压结果不会整体驻留内存，释放时不关闭 <paramref name="input"/>。</para></returns>
    internal static Stream CreateDecompressionStream(Stream input)
        => new DecompressionStream(new NonDisposingStream(input));

    /// <summary>
    /// Detects whether the data is in zstd format (magic 28 B5 2F FD).
    /// <para>检测数据是否为 zstd 格式（魔数 28 B5 2F FD）。</para>
    /// </summary>
    /// <param name="data">Buffer to inspect; <see langword="null"/> or shorter than four bytes is never zstd.
    /// <para>要检查的缓冲区；为 <see langword="null"/> 或少于四字节时永不判为 zstd。</para></param>
    /// <returns><see langword="true"/> when the leading four bytes carry the zstd frame magic.
    /// <para>前四字节携带 zstd 帧魔数时返回 <see langword="true"/>。</para></returns>
    public static bool IsZstdFormat(byte[] data)
    {
        return data is { Length: >= 4 } &&
               data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD;
    }
}