using SevenZip;
using SevenZip.Compression.LZMA;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// LZMA 压缩/解压（基于 LZMA-SDK 的全托管 7-Zip C# 移植，带 .lzma 头）。
/// <para>LZMA compression/decompression (based on the fully-managed 7-Zip C# port of LZMA-SDK, with .lzma header).</para>
/// </summary>
public static class LzmaCompressor
{
    /// <summary>默认字典大小（8 MiB，与 LZMA SDK 默认一致）。<para>Default dictionary size (8 MiB, matching the LZMA SDK default).</para></summary>
    private const uint EncoderDefaultDictionarySize = 1u << 23;

    /// <summary>
    /// LZMA 压缩（输出 .lzma 格式：5 字节属性 + 8 字节未压缩大小 + 压缩数据）。
    /// <para>LZMA compression (outputs .lzma format: 5-byte properties + 8-byte uncompressed size + compressed data).</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <returns>.lzma 格式的压缩数据。<para>Compressed data in .lzma format.</para></returns>
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
    /// 流式 LZMA 压缩：LZMA-SDK 编码器直接消费输入流，无需整块缓冲，适合大输入。
    /// 输入可定位时头部写入真实未压缩大小；不可定位时写入 0xFFFFFFFFFFFFFFFF（未知大小，
    /// .lzma 规范允许），解码端按未知大小流式解出。
    /// LZMA1 是单字典流（无 LZMA2 的独立块），因此本格式不做分块并行。
    /// <para>Streaming LZMA compression: the LZMA-SDK encoder consumes the input stream
    /// directly without full buffering, suitable for large inputs. When the input is
    /// seekable the real uncompressed size is written in the header; otherwise
    /// 0xFFFFFFFFFFFFFFFF (unknown size, allowed by the .lzma spec) is written and the
    /// decoder streams to EOF. LZMA1 is a single-dictionary stream (no independent blocks
    /// like LZMA2), so this format is not chunked in parallel.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入 .lzma 数据的输出流。<para>The output stream receiving .lzma data.</para></param>
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

            // 未知大小（不可定位输入）时必须有 EndMarker，解码器才能识别流结束。
            // 可定位输入写真实大小、解码按 outSize 停止，EndMarker 亦无害。
            encoder.SetCoderProperties(
                [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitPosBits, CoderPropID.LitContextBits, CoderPropID.EndMarker],
                [(int)EncoderDefaultDictionarySize, 2, 0, 3, true]);

            encoder.WriteCoderProperties(output);

            // .lzma 头：8 字节小端未压缩大小；未知时写 0xFFFFFFFFFFFFFFFF。
            var dataSize = input.CanSeek ? input.Length - input.Position : -1;
            for (var i = 0; i < 8; i++)
            {
                output.WriteByte((byte)((ulong)dataSize >> (8 * i)));
            }

            // inSize=-1：编码器持续读取输入流直到 EOF。
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
    /// LZMA 解压。
    /// <para>LZMA decompression.</para>
    /// </summary>
    /// <param name="data">.lzma 格式的压缩数据。<para>Compressed data in .lzma format.</para></param>
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
    /// 流式 LZMA 解压：LZMA-SDK 解码器本身接受流，无需整块缓冲，适合大输入。
    /// <para>Streaming LZMA decompression: the LZMA-SDK decoder consumes streams directly,
    /// so large inputs are piped without full buffering.</para>
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