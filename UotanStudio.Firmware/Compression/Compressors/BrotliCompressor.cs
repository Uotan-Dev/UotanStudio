using System.Buffers;
using System.IO.Compression;
using BrotliStream = BrotliSharpLib.BrotliStream;

namespace UotanStudio.Firmware.Compression.Compressors;

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

    /// <summary>
    /// Brotli compression with a shared custom dictionary.
    /// <para>使用共享自定义字典进行 Brotli 压缩。</para>
    /// </summary>
    /// <remarks>
    /// The dictionary is prepended to the encoder's sliding window so repeated content earns back-references into it; the
    /// matching decoder overload must be given the identical dictionary bytes or the stream decodes to corrupt output rather than
    /// raising an error. An empty dictionary behaves like the dictionary-free overload.
    /// <para>字典预置到编码器滑动窗口，使重复内容可回溯引用到其中；配套的解码重载必须提供完全相同的字典字节，否则数据会解码为损坏输出而不报错。
    /// 空字典等同于无字典重载。</para>
    /// </remarks>
    /// <param name="data">
    /// Plaintext compressed in one pass; an empty array yields an empty Brotli stream.
    /// <para>一次性压缩的明文字节；空数组产生空的 Brotli 流。</para></param>
    /// <param name="dictionary">
    /// Shared dictionary bytes trusted to both endpoints; must match the decoder exactly.
    /// <para>两端共享且可信的字典字节，必须与解码端完全一致。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read, mapped to Brotli quality as in the dictionary-free overload.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/>，按无字典重载的规则映射为 Brotli 质量。</para></param>
    /// <returns>
    /// The Brotli byte stream without any wrapping container.
    /// <para>不带任何容器包装的 Brotli 字节流。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> or <paramref name="dictionary"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 或 <paramref name="dictionary"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The encoder failed.
    /// <para>编码器执行失败。</para></exception>
    public static byte[] CompressWithDictionary(byte[] data, byte[] dictionary, CompressionOptions? options)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ArgumentNullException.ThrowIfNull(dictionary);

        try
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                brotli.SetQuality(Internal.CompressionLevelMapper.ToBrotliQuality(options?.Level));
                brotli.SetCustomDictionary(dictionary);
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
    /// Streaming Brotli compression with a shared custom dictionary.
    /// <para>使用共享自定义字典进行流式 Brotli 压缩。</para>
    /// </summary>
    /// <remarks>
    /// The decoder must supply the identical <paramref name="dictionary"/> bytes or the output decodes to corrupt data.
    /// <para>解码端必须提供完全相同的 <paramref name="dictionary"/> 字节，否则输出会解码为损坏数据。</para>
    /// </remarks>
    /// <param name="input">
    /// Readable source copied into the encoder in fixed buffers until end of stream; ownership stays with the caller.
    /// <para>按固定缓冲拷贝进编码器直至流末尾的可读源流；所有权归调用方。</para></param>
    /// <param name="output">
    /// Writable destination; the encoded bytes are appended from its current position and it is left open once the final Brotli block is flushed.
    /// <para>写入目标；编码字节从其当前位置追加写出，刷新最后一个 Brotli 块后保持打开。</para></param>
    /// <param name="dictionary">
    /// Shared dictionary bytes trusted to both endpoints; must match the decoder exactly.
    /// <para>两端共享且可信的字典字节，必须与解码端完全一致。</para></param>
    /// <param name="options">
    /// Only <see cref="CompressionOptions.Level"/> is read; <see cref="CompressionOptions.MaxDegreeOfParallelism"/> is ignored because a Brotli stream is not chunked.
    /// <para>仅读取 <see cref="CompressionOptions.Level"/>；Brotli 流不分块，<see cref="CompressionOptions.MaxDegreeOfParallelism"/> 被忽略。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/>, <paramref name="output"/> or <paramref name="dictionary"/> is <see langword="null"/>.
    /// <para><paramref name="input"/>、<paramref name="output"/> 或 <paramref name="dictionary"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, encoding or writing failed.
    /// <para>读取、编码或写入失败。</para></exception>
    public static void CompressWithDictionary(Stream input, Stream output, byte[] dictionary, CompressionOptions? options)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        ArgumentNullException.ThrowIfNull(dictionary);

        try
        {
            using var brotli = new BrotliStream(output, CompressionMode.Compress, leaveOpen: true);
            brotli.SetQuality(Internal.CompressionLevelMapper.ToBrotliQuality(options?.Level));
            brotli.SetCustomDictionary(dictionary);
            input.CopyTo(brotli);
        }
        catch (Exception ex)
        {
            throw new CompressionException("BROTLI 压缩失败", ex);
        }
    }

    /// <summary>
    /// Brotli decompression with a shared custom dictionary.
    /// <para>使用共享自定义字典进行 Brotli 解压。</para>
    /// </summary>
    /// <remarks>
    /// <paramref name="dictionary"/> must be byte-identical to the dictionary used to compress <paramref name="data"/>; a mismatch
    /// yields corrupt output rather than an exception. The whole expansion is buffered in memory with no output cap, so callers
    /// handling untrusted firmware streams must prefer the bounded streaming overload.
    /// <para><paramref name="dictionary"/> 必须与压缩 <paramref name="data"/> 所用字典逐字节一致；不一致会得到损坏输出而非异常。
    /// 解压结果全部驻留内存且无输出上限，处理不可信固件流的调用方应改用带上限的流式重载。</para>
    /// </remarks>
    /// <param name="data">
    /// Complete Brotli stream produced by a dictionary-aware encoder, treated as untrusted input.
    /// <para>由带字典编码器生成的完整 Brotli 流，按不可信输入处理。</para></param>
    /// <param name="dictionary">
    /// The same dictionary bytes supplied to the encoder.
    /// <para>与编码器所使用的相同字典字节。</para></param>
    /// <returns>
    /// The decoded bytes.
    /// <para>解码后的字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> or <paramref name="dictionary"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 或 <paramref name="dictionary"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The stream is truncated, corrupt, or not Brotli encoded.
    /// <para>数据被截断、损坏或并非 Brotli 编码。</para></exception>
    public static byte[] DecompressWithDictionary(byte[] data, byte[] dictionary)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ArgumentNullException.ThrowIfNull(dictionary);

        try
        {
            using var input = new MemoryStream(data);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);
            brotli.SetCustomDictionary(dictionary);
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
    /// Streaming Brotli decompression with a shared custom dictionary and output limit (decompression bomb protection).
    /// <para>使用共享自定义字典并带输出上限的流式 Brotli 解压（防解压炸弹）。</para>
    /// </summary>
    /// <remarks>
    /// <paramref name="dictionary"/> must be byte-identical to the encoder's dictionary; a mismatch yields corrupt output rather than
    /// an exception, so integrity-critical callers should verify a digest of the decoded bytes against external metadata.
    /// <para><paramref name="dictionary"/> 必须与编码器字典逐字节一致；不一致会得到损坏输出而非异常，因此完整性关键的调用方应按外部元数据校验解码字节的摘要。</para>
    /// </remarks>
    /// <param name="source">
    /// Readable Brotli stream from a dictionary-aware encoder, decoded through an 80 KiB pooled buffer; the stream is left open.
    /// <para>来自带字典编码器的可读 Brotli 流，使用 80 KiB 池化缓冲逐块解码；流保持打开。</para></param>
    /// <param name="destination">
    /// Writable destination; each accepted block is written before the next limit check, so no byte beyond the budget reaches it.
    /// <para>写入目标；每块先通过上限检查再写出，超出预算的字节不会落入该流。</para></param>
    /// <param name="dictionary">
    /// The same dictionary bytes supplied to the encoder.
    /// <para>与编码器所使用的相同字典字节。</para></param>
    /// <param name="maxOutputBytes">
    /// Inclusive cap on total decoded bytes; <c>0</c> allows an empty result only.
    /// <para>解压输出总字节数的含上限，<c>0</c> 表示只允许空结果。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/>, <paramref name="destination"/> or <paramref name="dictionary"/> is <see langword="null"/>.
    /// <para><paramref name="source"/>、<paramref name="destination"/> 或 <paramref name="dictionary"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxOutputBytes"/> is negative.
    /// <para><paramref name="maxOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="CompressionException">The decoded size would exceed <paramref name="maxOutputBytes"/>, the stream is malformed, or writing failed.
    /// <para>解压结果将超过 <paramref name="maxOutputBytes"/>、数据损坏或写入失败。</para></exception>
    public static void DecompressWithDictionary(Stream source, Stream destination, byte[] dictionary, long maxOutputBytes)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        ArgumentNullException.ThrowIfNull(dictionary);

        if (maxOutputBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        }

        try
        {
            using var brotli = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
            brotli.SetCustomDictionary(dictionary);
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