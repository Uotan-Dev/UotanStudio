using SevenZip;
using SevenZip.Compression.LZMA;

namespace UotanStudio.Firmware.CrossCutting.Compress.Compressors;

public static class LzmaCompressor
{
    private const uint EncoderDefaultDictionarySize = 1u << 23;

    /// <summary>
    /// LZMA compression (outputs .lzma format: 5-byte properties + 8-byte uncompressed size + compressed data).
    /// <para>LZMA 压缩（输出 .lzma 格式：5 字节属性 + 8 字节未压缩大小 + 压缩数据）。</para>
    /// </summary>
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