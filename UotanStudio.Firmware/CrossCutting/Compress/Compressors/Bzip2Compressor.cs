using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using UotanStudio.Firmware.CrossCutting.Compress.Internal;

namespace UotanStudio.Firmware.CrossCutting.Compress.Compressors;

public static class Bzip2Compressor
{
    /// <summary>
    /// BZIP2 compression.
    /// <para>BZIP2 压缩。</para>
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
    /// Compresses [start, start+count) into an independent bzip2 stream (for parallel chunking).
    /// <para>把 [start, start+count) 压缩为独立的 bzip2 流（并行分块用）。</para>
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
    /// BZIP2 decompression.
    /// <para>BZIP2 解压。</para>
    /// </summary>
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
    /// Streaming BZIP2 compression.
    /// <para>流式 BZIP2 压缩。</para>
    /// </summary>
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
    /// Streaming BZIP2 decompression.
    /// <para>流式 BZIP2 解压。</para>
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


            using var bzip2 = BZip2Stream.Create(input, CompressionMode.Decompress, decompressConcatenated: true, leaveOpen: true);
            bzip2.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("BZIP2 解压失败", ex);
        }
    }

    /// <summary>
    /// Detects whether the data is in bzip2 format (magic 42 5A 68).
    /// <para>检测数据是否为 bzip2 格式（魔数 42 5A 68）。</para>
    /// </summary>
    public static bool IsBzip2Format(byte[] data) => data is { Length: >= 3 } && data[0] == 0x42 && data[1] == 0x5A && data[2] == 0x68;
}
