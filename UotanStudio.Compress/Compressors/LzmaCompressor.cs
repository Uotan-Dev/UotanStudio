using SevenZip;
using SevenZip.Compression.LZMA;

namespace UotanStudio.Compress.Compressors;

/// <summary>
/// LZMA-alone codec built on the LZMA-SDK: a five-byte coder-properties header, an eight-byte little-endian uncompressed size, then the encoded stream.
/// <para>基于 LZMA-SDK 的 LZMA-alone 编解码器：五字节编码器属性头、八字节小端未压缩大小，随后是编码流。</para>
/// </summary>
public static class LzmaCompressor
{
    private const uint EncoderDefaultDictionarySize = 1u << 23;

    /// <summary>
    /// LZMA compression (outputs .lzma format: 5-byte properties + 8-byte uncompressed size + compressed data).
    /// <para>LZMA 压缩（输出 .lzma 格式：5 字节属性 + 8 字节未压缩大小 + 压缩数据）。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext compressed in one pass with the SDK default coder properties; an empty array produces a header with a zero size field.
    /// <para>使用 SDK 默认编码器属性一次性压缩的明文字节；空数组生成大小字段为 0 的头部。</para></param>
    /// <returns>
    /// The <c>.lzma</c> encoding, whose size field records the exact input length and which carries no end marker.
    /// <para><c>.lzma</c> 编码结果：大小字段记录精确的输入长度，且不含结束标记。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The encoder failed.
    /// <para>编码器执行失败。</para></exception>
    public static byte[] Compress(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            var encoder = new Encoder();
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();

            encoder.WriteCoderProperties(output);

            long dataSize = data.Length;
            for (var i = 0; i < 8; i++)
            {
                output.WriteByte((byte)(dataSize >> (8 * i)));
            }

            encoder.Code(input, output, data.Length, -1, null);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZMA 压缩失败", ex);
        }
    }

    /// <summary>
    /// Streaming LZMA compression: the LZMA-SDK encoder consumes the input stream directly without full buffering, suitable for large inputs.
    /// <para>流式 LZMA 压缩：LZMA-SDK 编码器直接消费输入流，无需整块缓冲，适合大输入。</para>
    /// </summary>
    /// <param name="input">
    /// Readable source starting at the bytes to encode; the header size field records <c>Length - Position</c> for a seekable stream and <c>-1</c> otherwise. Ownership stays with the caller.
    /// <para>从待编码字节开始的可读源流；可定位流的头部大小字段记录 <c>Length - Position</c>，否则写入 <c>-1</c>。所有权归调用方。</para></param>
    /// <param name="output">
    /// Writable destination left open by this call, receiving the properties, the size field and an end-marker-terminated stream encoded with an 8 MiB dictionary.
    /// <para>不会被本调用关闭的写入目标，依次收到属性字节、大小字段以及以结束标记收尾的编码流，字典大小为 8 MiB。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Reading, encoding or writing failed.
    /// <para>读取、编码或写入失败。</para></exception>
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
            var encoder = new Encoder();


            encoder.SetCoderProperties(
                [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitPosBits, CoderPropID.LitContextBits, CoderPropID.EndMarker],
                [(int)EncoderDefaultDictionarySize, 2, 0, 3, true]);

            encoder.WriteCoderProperties(output);


            var dataSize = input.CanSeek ? input.Length - input.Position : -1;
            for (var i = 0; i < 8; i++)
            {
                output.WriteByte((byte)((ulong)dataSize >> (8 * i)));
            }


            encoder.Code(input, output, -1, -1, null);
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZMA 压缩失败", ex);
        }
    }

    /// <summary>
    /// LZMA decompression.
    /// <para>LZMA 解压。</para>
    /// </summary>
    /// <param name="data">
    /// Complete <c>.lzma</c> payload, treated as untrusted input; the whole expansion is buffered in memory and its length is driven by the eight-byte size field, so a size of <c>-1</c> relies on the end marker instead.
    /// <para>完整的 <c>.lzma</c> 数据，按不可信输入处理；解压结果整体驻留内存，长度由八字节大小字段决定，因此大小为 <c>-1</c> 时依赖结束标记。</para></param>
    /// <returns>
    /// The decoded bytes, with no output cap applied.
    /// <para>解码后的字节，不施加输出上限。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">The properties or size header is truncated, or the encoded stream is corrupt.
    /// <para>属性或大小头被截断，或编码流损坏。</para></exception>
    public static byte[] Decompress(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            Decompress(input, output);
            return output.ToArray();
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZMA 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming LZMA decompression: the LZMA-SDK decoder consumes streams directly, so large inputs are piped without full buffering.
    /// <para>流式 LZMA 解压：LZMA-SDK 解码器本身接受流，无需整块缓冲，适合大输入。</para>
    /// </summary>
    /// <param name="input">
    /// Readable payload positioned at its first properties byte; thirteen header bytes are consumed up front and the decoder then reads only as far as the declared size or the end marker requires. The stream is not closed.
    /// <para>定位在首个属性字节处的可读数据；先消费 13 字节的头，随后解码器只读到声明大小或结束标记为止，不会关闭该流。</para></param>
    /// <param name="output">
    /// Writable destination fed as blocks are decoded, with no cap on the total size; a failure partway leaves the bytes already written in place. It is not closed.
    /// <para>随块解码即写入的写入目标，总大小不设上限；中途失败时已写字节会保留。不会被本调用关闭。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CompressionException">Fewer than five properties bytes or eight size bytes are available, the properties are invalid, or the encoded stream is corrupt.
    /// <para>可用属性字节不足 5 个或大小字节不足 8 个、属性非法，或编码流损坏。</para></exception>
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
            var decoder = new Decoder();

            var properties = new byte[5];
            if (ReadExactly(input, properties, 5) != 5)
            {
                throw new CompressionException("LZMA 数据格式无效：缺少属性字节");
            }

            decoder.SetDecoderProperties(properties);

            var fileSizeBytes = new byte[8];
            if (ReadExactly(input, fileSizeBytes, 8) != 8)
            {
                throw new CompressionException("LZMA 数据格式无效：缺少大小字段");
            }

            long fileSize = 0;
            for (var i = 0; i < 8; i++)
            {
                fileSize |= (long)fileSizeBytes[i] << (8 * i);
            }

            decoder.Code(input, output, -1, fileSize, null);
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZMA 解压失败", ex);
        }
    }

    private static int ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read;
    }
}