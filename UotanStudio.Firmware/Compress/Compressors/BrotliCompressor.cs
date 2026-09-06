using System.Buffers;
using System.IO.Compression;
using BrotliStream = BrotliSharpLib.BrotliStream;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// Brotli 压缩/解压（基于 BrotliSharpLib 的纯托管 BrotliStream，RFC 7932）。
/// <para>Brotli compression/decompression (based on BrotliSharpLib's fully-managed BrotliStream, RFC 7932).</para>
/// </summary>
public static class BrotliCompressor
{
    /// <summary>
    /// Brotli 压缩。
    /// <para>Brotli compression.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="options">压缩选项（Level：0-9，null=Optimal）。<para>Compression options (Level: 0-9, null=Optimal).</para></param>
    /// <returns>Brotli 格式的压缩数据。<para>Compressed data in Brotli format.</para></returns>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                brotli.SetQuality(Internal.CompressionLevelMapper.ToBrotliQuality(options?.Level));
                brotli.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("BROTLI 压缩失败", ex);
        }
    }

    /// <summary>
    /// Brotli 解压。
    /// <para>Brotli decompression.</para>
    /// </summary>
    /// <param name="data">Brotli 格式的压缩数据。<para>Compressed data in Brotli format.</para></param>
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
            using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("BROTLI 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式 Brotli 压缩。
    /// <para>Streaming Brotli compression.</para>
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
            using var brotli = new BrotliStream(output, CompressionMode.Compress, leaveOpen: true);
            brotli.SetQuality(Internal.CompressionLevelMapper.ToBrotliQuality(options?.Level));
            input.CopyTo(brotli);
        }
        catch (Exception ex)
        {
            throw new CompressionException("BROTLI 压缩失败", ex);
        }
    }

    /// <summary>
    /// 流式 Brotli 解压。
    /// <para>Streaming Brotli decompression.</para>
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
            using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
            brotli.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("BROTLI 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式 Brotli 解压，带输出上限（防解压炸弹）。
    /// <para>Streaming Brotli decompression with output limit (decompression bomb protection).</para>
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
            using var brotli = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalWritten = 0;
                int bytesRead;
                while ((bytesRead = brotli.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (totalWritten + bytesRead > maxOutputBytes)
                    {
                        throw new CompressionException(
                            $"BROTLI 解压输出超过上限 ({maxOutputBytes} 字节) / Decompressed output exceeds limit ({maxOutputBytes} bytes)");
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
            throw new CompressionException("BROTLI 解压失败", ex);
        }
    }
}