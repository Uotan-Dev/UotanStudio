using UotanStudio.Firmware.Compress.Internal;
using ZstdSharp;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// Zstandard (zstd) 压缩/解压（基于 ZstdSharp.Port 的全托管 zstd 移植，Zstandard v1.5.x）。
/// <para>Zstandard (zstd) compression/decompression (based on the fully-managed ZstdSharp.Port
/// port of zstd, Zstandard v1.5.x).</para>
/// </summary>
public static class ZstdCompressor
{
    /// <summary>
    /// Zstd 压缩。
    /// <para>Zstd compression.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="options">压缩选项（Level：-5..22，null=3）。<para>Compression options (Level: -5..22, null=3).</para></param>
    /// <returns>zstd 帧数据。<para>Zstd frame data.</para></returns>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            // 并行分块：每块压缩为独立 zstd 帧后按序拼接（多帧 zstd，ZstdSharp 解码器原生支持）。
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
    /// 把 [start, start+count) 压缩为独立的 zstd 帧（并行分块用）。
    /// <para>Compresses [start, start+count) into an independent zstd frame (for parallel chunking).</para>
    /// </summary>
    private static byte[] CompressChunk(byte[] data, int start, int count, CompressionOptions? options)
    {
        using var compressor = new Compressor(options?.Level ?? 3);
        return compressor.Wrap(data.AsSpan(start, count)).ToArray();
    }

    /// <summary>
    /// Zstd 解压。
    /// <para>Zstd decompression.</para>
    /// </summary>
    /// <param name="data">zstd 帧数据。<para>Zstd frame data.</para></param>
    /// <returns>解压后的原始数据。<para>The decompressed original data.</para></returns>
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
    /// 流式 Zstd 压缩：基于 ZstdSharp 的 <see cref="CompressionStream"/> 直接管道，
    /// 无需整块缓冲，适合大输入。
    /// <para>Streaming zstd compression: pipes through ZstdSharp's
    /// <see cref="CompressionStream"/> without full buffering, suitable for large inputs.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入 zstd 帧的输出流。<para>The output stream receiving the zstd frame.</para></param>
    /// <param name="options">压缩选项（Level：-5..22，null=3）。<para>Compression options (Level: -5..22, null=3).</para></param>
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
    /// 流式 Zstd 解压：基于 ZstdSharp 的 <see cref="DecompressionStream"/> 直接管道，
    /// 无需整块缓冲，适合大输入。
    /// <para>Streaming zstd decompression: pipes through ZstdSharp's
    /// <see cref="DecompressionStream"/> without full buffering, suitable for large inputs.</para>
    /// </summary>
    /// <param name="input">zstd 帧输入流。<para>The input stream containing zstd frames.</para></param>
    /// <param name="output">写入解压结果的输出流。<para>The output stream receiving the decompressed data.</para></param>
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
    /// 检测数据是否为 zstd 格式（魔数 28 B5 2F FD）。
    /// <para>Detects whether the data is in zstd format (magic 28 B5 2F FD).</para>
    /// </summary>
    public static bool IsZstdFormat(byte[] data)
    {
        return data is { Length: >= 4 } &&
               data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD;
    }
}