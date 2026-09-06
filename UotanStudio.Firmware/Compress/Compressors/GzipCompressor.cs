using System.Buffers;
using System.IO.Compression;
using UotanStudio.Firmware.Compress.Internal;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// GZIP 压缩/解压（基于 .NET 内置 GZipStream，RFC 1952）。
/// <para>GZIP compression/decompression (based on the .NET built-in GZipStream, RFC 1952).</para>
/// </summary>
public static class GzipCompressor
{
    /// <summary>
    /// GZIP 压缩。
    /// <para>GZIP compression.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="options">压缩选项（Level：0-9，null=Optimal）。<para>Compression options (Level: 0-9, null=Optimal).</para></param>
    /// <returns>gzip 格式的压缩数据。<para>Compressed data in gzip format.</para></returns>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            // 并行分块：每块压缩为独立 gzip 成员后按序拼接（多成员 gzip，.NET GZipStream 原生可解）。
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
    /// 把 [start, start+count) 压缩为独立的 gzip 成员（并行分块用）。
    /// <para>Compresses [start, start+count) into an independent gzip member (for parallel chunking).</para>
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
    /// GZIP 解压。
    /// <para>GZIP decompression.</para>
    /// </summary>
    /// <param name="data">gzip 格式的压缩数据。<para>Compressed data in gzip format.</para></param>
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
    /// 流式 GZIP 压缩。
    /// <para>Streaming GZIP compression.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入压缩结果的输出流。<para>The output stream that receives the compressed data.</para></param>
    /// <param name="options">压缩选项（Level：0-9，null=Optimal）。<para>Compression options (Level: 0-9, null=Optimal).</para></param>
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
    /// 流式 GZIP 解压。
    /// <para>Streaming GZIP decompression.</para>
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
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            gzip.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("GZIP 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式 GZIP 解压，带输出上限（防解压炸弹）。
    /// <para>Streaming GZIP decompression with output limit (decompression bomb protection).</para>
    /// </summary>
    /// <param name="source">待解压的压缩流。<para>The compressed input stream.</para></param>
    /// <param name="destination">写入解压结果的输出流。<para>The output stream that receives the decompressed data.</para></param>
    /// <param name="maxOutputBytes">最大允许的解压输出字节数，超出则抛出异常。<para>Maximum allowed decompressed output in bytes; throws if exceeded.</para></param>
    /// <exception cref="CompressionException">解压输出超过 <paramref name="maxOutputBytes"/> 时抛出。
    /// <para>Thrown when decompressed output exceeds <paramref name="maxOutputBytes"/>.</para></exception>
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
    /// 检测数据是否为 gzip 格式（魔数 1F 8B）。
    /// <para>Detects whether the data is in gzip format (magic 1F 8B).</para>
    /// </summary>
    public static bool IsGzipFormat(byte[] data) => data is { Length: >= 2 } && data[0] == 0x1F && data[1] == 0x8B;
}