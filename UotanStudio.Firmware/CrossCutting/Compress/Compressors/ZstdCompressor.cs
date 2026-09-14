using UotanStudio.Firmware.CrossCutting.Compress.Internal;
using ZstdSharp;

namespace UotanStudio.Firmware.CrossCutting.Compress.Compressors;

public static class ZstdCompressor
{
    /// <summary>
    /// Zstd compression.
    /// <para>Zstd 压缩。</para>
    /// </summary>
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
    /// Streaming zstd compression: pipes through ZstdSharp's CompressionStream without full buffering, suitable for large inputs.
    /// <para>流式 Zstd 压缩：基于 ZstdSharp 的 CompressionStream 直接管道， 无需整块缓冲，适合大输入。</para>
    /// </summary>
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
            using var compressor = new CompressionStream(new NonDisposingStream(output), options?.Level ?? 3);
            input.CopyTo(compressor);
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZSTD 压缩失败", ex);
        }
    }

    /// <summary>
    /// Streaming zstd decompression: pipes through ZstdSharp's DecompressionStream without full buffering, suitable for large inputs.
    /// <para>流式 Zstd 解压：基于 ZstdSharp 的 DecompressionStream 直接管道， 无需整块缓冲，适合大输入。</para>
    /// </summary>
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
    /// Detects whether the data is in zstd format (magic 28 B5 2F FD).
    /// <para>检测数据是否为 zstd 格式（魔数 28 B5 2F FD）。</para>
    /// </summary>
    public static bool IsZstdFormat(byte[] data)
    {
        return data is { Length: >= 4 } &&
               data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD;
    }
}