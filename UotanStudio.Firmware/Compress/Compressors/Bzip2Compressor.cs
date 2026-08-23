using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using UotanStudio.Firmware.Compress.Internal;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// BZIP2 压缩/解压（基于 SharpCompress 的全托管 BZip2Stream）。
/// <para>BZIP2 compression/decompression (based on SharpCompress's fully-managed BZip2Stream).</para>
/// </summary>
public static class Bzip2Compressor
{
    /// <summary>
    /// BZIP2 压缩。
    /// <para>BZIP2 compression.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="options">压缩选项（Level：0-9，null=Default）。<para>Compression options (Level: 0-9, null=Default).</para></param>
    /// <returns>bzip2 格式的压缩数据。<para>Compressed data in bzip2 format.</para></returns>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            // 并行分块：每块压缩为独立 bzip2 流后按序拼接（多成员 bzip2，解压端需 decompressConcatenated=true）。
            var parallel = ParallelCompression.TryCompressChunks(data, options?.MaxDegreeOfParallelism,
                (start, count) => CompressChunk(data, start, count));
            if (parallel != null)
            {
                return parallel;
            }

            using var output = new MemoryStream();
            using (var bzip2 = BZip2Stream.Create(output, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: false))
            {
                bzip2.Write(data, 0, data.Length);
                bzip2.Finish();
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("BZIP2 压缩失败", ex);
        }
    }

    /// <summary>
    /// 把 [start, start+count) 压缩为独立的 bzip2 流（并行分块用）。
    /// <para>Compresses [start, start+count) into an independent bzip2 stream (for parallel chunking).</para>
    /// </summary>
    private static byte[] CompressChunk(byte[] data, int start, int count)
    {
        using var output = new MemoryStream();
        using (var bzip2 = BZip2Stream.Create(output, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: false))
        {
            bzip2.Write(data, start, count);
            bzip2.Finish();
        }
        return output.ToArray();
    }

    /// <summary>
    /// BZIP2 解压。
    /// <para>BZIP2 decompression.</para>
    /// </summary>
    /// <param name="data">bzip2 格式的压缩数据。<para>Compressed data in bzip2 format.</para></param>
    /// <returns>解压后的原始数据。<para>The decompressed original data.</para></returns>
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

    /// <summary>
    /// 流式 BZIP2 压缩。
    /// <para>Streaming BZIP2 compression.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入压缩结果的输出流。<para>The output stream that receives the compressed data.</para></param>
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
            using var bzip2 = BZip2Stream.Create(output, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true);
            input.CopyTo(bzip2);
            bzip2.Finish();
        }
        catch (Exception ex)
        {
            throw new CompressionException("BZIP2 压缩失败", ex);
        }
    }

    /// <summary>
    /// 流式 BZIP2 解压。
    /// <para>Streaming BZIP2 decompression.</para>
    /// </summary>
    /// <param name="input">待解压的输入流。<para>The input stream to decompress.</para></param>
    /// <param name="output">写入解压结果的输出流。<para>The output stream that receives the decompressed data.</para></param>
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
            // decompressConcatenated=true：支持并行模式产出的多成员（串联）bzip2 流，
            // 对单成员输入行为不变。
            using var bzip2 = BZip2Stream.Create(input, CompressionMode.Decompress, decompressConcatenated: true, leaveOpen: true);
            bzip2.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("BZIP2 解压失败", ex);
        }
    }

    /// <summary>
    /// 检测数据是否为 bzip2 格式（魔数 42 5A 68）。
    /// <para>Detects whether the data is in bzip2 format (magic 42 5A 68).</para>
    /// </summary>
    public static bool IsBzip2Format(byte[] data) => data is { Length: >= 3 } && data[0] == 0x42 && data[1] == 0x5A && data[2] == 0x68;
}