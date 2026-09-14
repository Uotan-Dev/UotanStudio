using System.Buffers;
using System.IO.Compression;
using BrotliStream = BrotliSharpLib.BrotliStream;

namespace UotanStudio.Firmware.CrossCutting.Compress.Compressors;

public static class BrotliCompressor
{
    /// <summary>
    /// Brotli compression.
    /// <para>Brotli 压缩。</para>
    /// </summary>
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
    /// Brotli decompression.
    /// <para>Brotli 解压。</para>
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
    /// Streaming Brotli compression.
    /// <para>流式 Brotli 压缩。</para>
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
    /// Streaming Brotli decompression.
    /// <para>流式 Brotli 解压。</para>
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
            using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
            brotli.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("BROTLI 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming Brotli decompression with output limit (decompression bomb protection).
    /// <para>流式 Brotli 解压，带输出上限（防解压炸弹）。</para>
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