using System.Buffers;
using System.IO.Compression;
using UotanStudio.Firmware.Compression.Internal;

namespace UotanStudio.Firmware.Compression.Compressors;

/// <summary>
/// Zstd compression and decompression over byte arrays or streams, backed by the in-box Zstandard codec.
/// <para>基于内置 Zstandard 编解码器的 Zstd 字节数组与流式压缩解压。</para>
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

            var frame = new byte[ZstandardEncoder.GetMaxCompressedLength(data.Length)];
            if (!ZstandardEncoder.TryCompress(data, frame, out var written, options?.Level ?? 3, 0))
            {
                throw new CompressionException("ZSTD 压缩失败");
            }

            return frame.AsSpan(0, written).ToArray();
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
        var frame = new byte[ZstandardEncoder.GetMaxCompressedLength(count)];
        if (!ZstandardEncoder.TryCompress(data.AsSpan(start, count), frame, out var written, options?.Level ?? 3, 0))
        {
            throw new CompressionException("ZSTD 压缩失败");
        }

        return frame.AsSpan(0, written).ToArray();
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
            using var decompressor = new ZstandardStream(new MemoryStream(data, writable: false),
                CompressionMode.Decompress, leaveOpen: true);
            using var plaintext = new MemoryStream();
            decompressor.CopyTo(plaintext);
            return plaintext.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZSTD 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming zstd compression: pipes through the in-box <see cref="ZstandardStream"/> without full buffering, suitable for large inputs.
    /// When <see cref="CompressionOptions.MaxDegreeOfParallelism"/> is above 1 and the input is seekable with at least two
    /// 1 MiB chunks, chunks are compressed concurrently into independent frames and concatenated in chunk order, matching
    /// the byte-array overload; otherwise a single frame is produced.
    /// <para>流式 Zstd 压缩：基于内置 <see cref="ZstandardStream"/> 直接管道，无需整块缓冲，适合大输入。当
    /// <see cref="CompressionOptions.MaxDegreeOfParallelism"/> 大于 1 且输入可寻址并至少包含两个 1 MiB 块时，各块并发
    /// 压缩为独立帧并按块序串接，与字节数组重载语义一致；否则生成单帧。</para>
    /// </summary>
    /// <param name="input">Readable source stream, consumed to its end and left open.
    /// <para>可读源流，读取到末尾并保持打开。</para></param>
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
            using var compressor = new ZstandardStream(output,
                new ZstandardCompressionOptions { Quality = options?.Level ?? 3 }, leaveOpen: true);
            input.CopyTo(compressor);
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZSTD 压缩失败", ex);
        }
    }

    /// <summary>
    /// Compresses a seekable stream through parallel per-chunk frames when parallelism is requested and at least two
    /// chunks exist; otherwise returns <see langword="false"/> and the caller keeps its single-frame sequential path.
    /// <para>在请求并行且至少存在两个块时，把可寻址流按块并发压缩为独立帧；否则返回 <see langword="false"/>，
    /// 调用方保持单帧串行路径。</para>
    /// </summary>
    private static bool TryCompressParallel(Stream input, Stream output, CompressionOptions? options)
    {
        if (!input.CanSeek || options?.MaxDegreeOfParallelism is null or <= 1)
        {
            return false;
        }
        var remaining = input.Length - input.Position;
        var chunkSize = ParallelCompression.DefaultChunkSize;
        var chunkCount = (int)Math.Min(int.MaxValue, (remaining + chunkSize - 1) / chunkSize);
        var degree = Parallelism.Resolve(options.MaxDegreeOfParallelism, chunkCount);
        if (degree <= 1 || chunkCount < 2)
        {
            return false;
        }

        var buffers = new byte[degree][];
        var compressed = new byte[degree][];
        for (var slot = 0; slot < degree; slot++)
        {
            buffers[slot] = ArrayPool<byte>.Shared.Rent(chunkSize);
        }
        try
        {
            for (var chunk = 0; chunk < chunkCount; chunk += degree)
            {
                var batch = Math.Min(degree, chunkCount - chunk);
                for (var slot = 0; slot < batch; slot++)
                {
                    var start = (chunk + slot) * (long)chunkSize;
                    var count = (int)Math.Min(chunkSize, remaining - start);
                    var buffer = buffers[slot];
                    input.ReadExactly(buffer, 0, count);
                    compressed[slot] = CompressChunk(buffer, 0, count, options);
                }
                for (var slot = 0; slot < batch; slot++)
                {
                    output.Write(compressed[slot], 0, compressed[slot].Length);
                    compressed[slot] = null!;
                }
            }
            return true;
        }
        finally
        {
            for (var slot = 0; slot < buffers.Length; slot++)
            {
                ArrayPool<byte>.Shared.Return(buffers[slot]);
            }
        }
    }

    /// <summary>
    /// Streaming zstd decompression: pipes through the in-box <see cref="ZstandardStream"/> without full buffering, suitable for large inputs.
    /// <para>流式 Zstd 解压：基于内置 <see cref="ZstandardStream"/> 直接管道，无需整块缓冲，适合大输入。</para>
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
            using var decompressor = new ZstandardStream(input, CompressionMode.Decompress, leaveOpen: true);
            decompressor.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZSTD 解压失败", ex);
        }
    }

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