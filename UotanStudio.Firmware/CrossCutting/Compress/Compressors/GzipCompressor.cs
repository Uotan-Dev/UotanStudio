using System.Buffers;
using System.IO.Compression;
using UotanStudio.Firmware.CrossCutting.Compress.Internal;

namespace UotanStudio.Firmware.CrossCutting.Compress.Compressors;

public static class GzipCompressor
{
    /// <summary>
    /// GZIP compression.
    /// <para>GZIP 压缩。</para>
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
    public static bool IsGzipFormat(byte[] data) => data is { Length: >= 2 } && data[0] == 0x1F && data[1] == 0x8B;
}
