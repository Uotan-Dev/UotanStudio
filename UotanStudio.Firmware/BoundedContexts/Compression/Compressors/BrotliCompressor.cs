using System.Buffers;
using System.IO.Compression;
using BrotliStream = BrotliSharpLib.BrotliStream;

namespace UotanStudio.Firmware.BoundedContexts.Compression.Compressors;

/// <summary>
/// Compresses and decompresses raw Brotli streams (no container header or footer) through BrotliSharpLib.
/// <para>通过 BrotliSharpLib 处理原始 Brotli 流（不含容器头尾），支持压缩与解压。</para>
/// </summary>
public static class BrotliCompressor
{
    /// <summary>
    /// Brotli compression.
    /// <para>Brotli 压缩。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext compressed in one pass; an empty array yields an empty Brotli stream.
    /// <para>一次性压缩的明文字节；空数组产生空的 Brotli 流。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read: it maps to Brotli quality, 0 for level 0 and below, 1 up to level 3, 5 for levels 4-8 and 11 from level 9, with quality 4 when unset.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/>：映射为 Brotli 质量，等级 0 及以下为 0，3 及以下为 1，4-8 为 5，9 及以上为 11，未设置时为质量 4。</para></param>
    /// <returns>
    /// The Brotli byte stream without any wrapping container.
    /// <para>不带任何容器包装的 Brotli 字节流。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The encoder failed.
    /// <para>编码器执行失败。</para></exception>
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
    /// <param name="data">
    /// Complete Brotli stream, treated as untrusted input; the whole expansion is buffered in memory with no output cap.
    /// <para>完整的 Brotli 流，按不可信输入处理；解压结果全部驻留内存且没有输出上限。</para></param>
    /// <returns>
    /// The decoded bytes.
    /// <para>解码后的字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The stream is truncated, corrupt, or not Brotli encoded.
    /// <para>数据被截断、损坏或并非 Brotli 编码。</para></exception>
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
    /// <param name="input">
    /// Readable source copied into the encoder in fixed buffers until end of stream; ownership stays with the caller.
    /// <para>按固定缓冲拷贝进编码器直至流末尾的可读源流；所有权归调用方。</para></param>
    /// <param name="output">
    /// Writable destination; the encoded bytes are appended from its current position and it is left open once the final Brotli block is flushed.
    /// <para>写入目标；编码字节从其当前位置追加写出，刷新最后一个 Brotli 块后保持打开。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read; <see cref="CompressionOptions.MaxDegreeOfParallelism"/> is ignored because a Brotli stream is not chunked.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/>；Brotli 流不分块，<see cref="CompressionOptions.MaxDegreeOfParallelism"/> 被忽略。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, encoding or writing failed.
    /// <para>读取、编码或写入失败。</para></exception>
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
    /// <param name="input">
    /// Readable Brotli stream; only the bytes the decoder consumes are read and the stream is left open.
    /// <para>可读的 Brotli 流；仅读取解码器消费的字节，流保持打开。</para></param>
    /// <param name="output">
    /// Writable destination for the decoded bytes, written in fixed buffers so the expansion is never fully buffered; no size cap is enforced.
    /// <para>解码字节的写入目标，按固定缓冲写出因此不会整体驻留内存；不施加大小上限。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The stream is truncated, corrupt, or writing to <paramref name="output"/> failed.
    /// <para>数据被截断、损坏，或写入 <paramref name="output"/> 失败。</para></exception>
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
    /// <param name="source">
    /// Readable Brotli stream, decoded through an 80 KiB pooled buffer; the stream is left open.
    /// <para>可读的 Brotli 流，使用 80 KiB 池化缓冲逐块解码；流保持打开。</para></param>
    /// <param name="destination">
    /// Writable destination; each accepted block is written before the next limit check, so no byte beyond the budget reaches it.
    /// <para>写入目标；每块先通过上限检查再写出，超出预算的字节不会落入该流。</para></param>
    /// <param name="maxOutputBytes">
    /// Inclusive cap on total decoded bytes; <c>0</c> allows an empty result only.
    /// <para>解压输出总字节数的含上限，<c>0</c> 表示只允许空结果。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="destination"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxOutputBytes"/> is negative.
    /// <para><paramref name="maxOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="CompressionException">The decoded size would exceed <paramref name="maxOutputBytes"/>, the stream is malformed, or writing failed.
    /// <para>解压结果将超过 <paramref name="maxOutputBytes"/>、数据损坏或写入失败。</para></exception>
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