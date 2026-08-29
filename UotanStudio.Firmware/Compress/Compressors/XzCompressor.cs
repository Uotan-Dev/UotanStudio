using SharpCompress.Compressors.Xz;
using UotanStudio.Firmware.Compress.Internal;
using UotanStudio.Firmware.Compress.Internal.Xz;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// 全托管 XZ 压缩/解压（无 P/Invoke、无 unsafe、无外部进程）。
/// <para>Fully-managed XZ compression/decompression (no P/Invoke, no unsafe, no external processes).</para>
/// <para>
/// 压缩：自有 LZMA2 编码器（基于 LZMA SDK 的 C# 移植）+ 自有 XZ 容器封装；
/// 解压：SharpCompress 的全托管 XZ 解码器。
/// </para>
/// <para>
/// Compression: own LZMA2 encoder (built on the LZMA SDK C# port) + own XZ container;
/// decompression: SharpCompress's fully-managed XZ decoder.
/// </para>
/// </summary>
public static class XzCompressor
{
    /// <summary>
    /// LZMA2 end-of-stream marker (single 0x00 byte) for empty input.
    /// <para>空输入的 LZMA2 流结束标记（单个 0x00 字节）。</para>
    /// </summary>
    private static readonly byte[] Lzma2EosMarker = [0x00];

    /// <summary>
    /// XZ 压缩。
    /// <para>XZ compression.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="dictionarySize">字典大小（默认 8 MiB）。<para>Dictionary size (default 8 MiB).</para></param>
    /// <param name="checkType">校验类型（0x01=CRC32，0x04=CRC64）。<para>Check type (0x01=CRC32, 0x04=CRC64).</para></param>
    /// <param name="maxDegreeOfParallelism">多核并行度；null/1 为串行。输出与串行逐字节一致。<para>Multi-core parallelism; null/1 = sequential. Output is byte-identical to sequential.</para></param>
    /// <returns>完整的 .xz 数据。<para>Complete .xz data.</para></returns>
    public static byte[] Compress(byte[] data, uint dictionarySize = Lzma2Encoder.DefaultDictionarySize, byte checkType = XzContainer.CheckTypeCrc32, int? maxDegreeOfParallelism = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            if (data.Length == 0)
            {
                return XzContainer.Wrap(Lzma2EosMarker, data, dictionarySize, checkType);
            }

            var lzma2 = Lzma2Encoder.Encode(data, dictionarySize, maxDegreeOfParallelism);
            return XzContainer.Wrap(lzma2, data, dictionarySize, checkType);
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 压缩失败", ex);
        }
    }

    /// <summary>
    /// 流式 XZ 压缩：从输入流按 2 MiB 窗口边读边压（有界内存），直接写入输出流。
    /// 输出与 <see cref="Compress(byte[], uint, byte, int?)"/> 对相同数据逐字节一致。
    /// <para>Streaming XZ compression: reads the input in bounded 2 MiB windows and writes
    /// the result directly to the output stream. Output is byte-identical to
    /// <see cref="Compress(byte[], uint, byte, int?)"/> for the same data.</para>
    /// </summary>
    public static void Compress(Stream input, Stream output, uint dictionarySize = Lzma2Encoder.DefaultDictionarySize,
        byte checkType = XzContainer.CheckTypeCrc32, int? maxDegreeOfParallelism = null)
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
            using var lzma2 = new MemoryStream();

            var crc64 = new System.IO.Hashing.Crc64();
            var crc32 = new System.IO.Hashing.Crc32();

            var total = Lzma2Encoder.Encode(input, lzma2, dictionarySize, maxDegreeOfParallelism,
                (win, offset, count) =>
                {
                    if (checkType == XzContainer.CheckTypeCrc64)
                    {
                        crc64.Append(new ReadOnlySpan<byte>(win, offset, count));
                    }
                    else
                    {
                        crc32.Append(new ReadOnlySpan<byte>(win, offset, count));
                    }
                });

            byte[] check;
            if (checkType == XzContainer.CheckTypeCrc64)
            {
                var crc = crc64.GetCurrentHashAsUInt64();
                check = new byte[8];
                for (var i = 0; i < 8; i++)
                {
                    check[i] = (byte)((crc >> (8 * i)) & 0xFF);
                }
            }
            else
            {
                var crc = crc32.GetCurrentHashAsUInt32();
                check = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    check[i] = (byte)((crc >> (8 * i)) & 0xFF);
                }
            }

            XzContainer.WriteContainer(output, lzma2.ToArray(), (ulong)total, check, dictionarySize, checkType);
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 压缩失败", ex);
        }
    }

    /// <summary>
    /// XZ 解压。
    /// <para>XZ decompression.</para>
    /// </summary>
    /// <param name="data">待解压的 .xz 数据。<para>.xz data to decompress.</para></param>
    /// <returns>解压后的原始数据。<para>The decompressed original data.</para></returns>
    public static byte[] Decompress(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            Decompress(input, output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式 XZ 解压：底层 XZStream 本身是流式的，无需整块缓冲，适合大输入。
    /// <para>Streaming XZ decompression: the underlying XZStream is inherently streaming,
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
            using var xzStream = new XZStream(new NonDisposingStream(input));
            xzStream.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 解压失败", ex);
        }
    }

    /// <summary>
    /// 检测数据是否为 XZ 格式（魔数 FD 37 7A 58 5A 00）。
    /// <para>Detects whether the data is in XZ format (magic FD 37 7A 58 5A 00).</para>
    /// </summary>
    public static bool IsXzFormat(byte[] data)
    {
        return data.Length >= 6 &&
               data[0] == 0xFD && data[1] == 0x37 && data[2] == 0x7A &&
               data[3] == 0x58 && data[4] == 0x5A && data[5] == 0x00;
    }
}