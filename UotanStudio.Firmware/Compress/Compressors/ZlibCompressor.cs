using SharpCompress.Compressors;
using SharpCompress.Compressors.Deflate;
using UotanStudio.Firmware.Compress.Internal;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// ZLIB 压缩/解压（基于 SharpCompress 的全托管 ZlibStream，RFC 1950）。
/// <para>ZLIB compression/decompression (based on SharpCompress's fully-managed ZlibStream, RFC 1950).</para>
/// </summary>
public static class ZlibCompressor
{
    /// <summary>
    /// ZLIB 压缩。
    /// <para>ZLIB compression.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="options">压缩选项（Level：0-9，null=Default）。<para>Compression options (Level: 0-9, null=Default).</para></param>
    /// <returns>zlib 格式的压缩数据。<para>Compressed data in zlib format.</para></returns>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            // 并行分块：每块压缩为独立 zlib 流后按序拼接（多成员 zlib，由本库 TotalIn 循环解压）。
            var parallel = ParallelCompression.TryCompressChunks(data, options?.MaxDegreeOfParallelism,
                (start, count) => CompressChunk(data, start, count, options));
            if (parallel != null)
            {
                return parallel;
            }

            using var output = new MemoryStream();
            using (var zlib = new ZlibStream(new NonDisposingStream(output), CompressionMode.Compress, CompressionLevelMapper.ToSharpCompressDeflate(options?.Level)))
            {
                zlib.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZLIB 压缩失败", ex);
        }
    }

    /// <summary>
    /// 把 [start, start+count) 压缩为独立的 zlib 流（并行分块用）。
    /// <para>Compresses [start, start+count) into an independent zlib stream (for parallel chunking).</para>
    /// </summary>
    private static byte[] CompressChunk(byte[] data, int start, int count, CompressionOptions? options)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZlibStream(new NonDisposingStream(output), CompressionMode.Compress, CompressionLevelMapper.ToSharpCompressDeflate(options?.Level)))
        {
            zlib.Write(data, start, count);
        }
        return output.ToArray();
    }

    /// <summary>
    /// ZLIB 解压。
    /// <para>ZLIB decompression.</para>
    /// </summary>
    /// <param name="data">zlib 格式的压缩数据。<para>Compressed data in zlib format.</para></param>
    /// <returns>解压后的原始数据。<para>The decompressed original data.</para></returns>
    public static byte[] Decompress(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var output = new MemoryStream();
            var offset = 0;

            // 逐成员解压：并行模式产出多成员 zlib 流，用解码器 TotalIn 精确定位成员边界。
            // Decompress member by member: parallel mode produces multi-member zlib streams;
            // the decoder's TotalIn locates each member boundary exactly.
            while (offset < data.Length)
            {
                using var sub = new MemoryStream(data, offset, data.Length - offset, writable: false);
                using var zlib = new ZlibStream(sub, CompressionMode.Decompress);
                zlib.CopyTo(output);

                var consumed = zlib.TotalIn;
                if (consumed <= 0)
                {
                    throw new CompressionException("ZLIB 数据格式无效：无法定位成员边界");
                }

                offset += (int)consumed;
            }

            return output.ToArray();
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZLIB 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式 ZLIB 压缩。
    /// <para>Streaming ZLIB compression.</para>
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
            using var zlib = new ZlibStream(new NonDisposingStream(output), CompressionMode.Compress, CompressionLevelMapper.ToSharpCompressDeflate(options?.Level));
            input.CopyTo(zlib);
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZLIB 压缩失败", ex);
        }
    }

    /// <summary>
    /// 流式 ZLIB 解压。
    /// <para>Streaming ZLIB decompression.</para>
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
            if (input.CanSeek)
            {
                // 可定位流：逐成员解压，用 TotalIn 精确推进到下一成员起点。
                // Seekable input: decompress member by member, advancing by TotalIn.
                while (true)
                {
                    var memberStart = input.Position;
                    using var zlib = new ZlibStream(new NonDisposingStream(input), CompressionMode.Decompress);
                    zlib.CopyTo(output);

                    var consumed = zlib.TotalIn;
                    if (consumed <= 0)
                    {
                        break;
                    }

                    var next = memberStart + consumed;
                    if (next >= input.Length)
                    {
                        break;
                    }

                    input.Position = next;
                }
            }
            else
            {
                // 不可定位流：无法确定成员边界，按单成员处理（与既有行为一致）。
                // Non-seekable input: member boundaries cannot be located; treat as single member.
                using var zlib = new ZlibStream(new NonDisposingStream(input), CompressionMode.Decompress);
                zlib.CopyTo(output);
            }
        }
        catch (Exception ex)
        {
            throw new CompressionException("ZLIB 解压失败", ex);
        }
    }

    /// <summary>
    /// 检测数据是否为 zlib 格式（启发式：CM=8 且 CMF/FLG 可被 31 整除）。
    /// <para>Detects whether the data is in zlib format (heuristic: CM=8 and (CMF*256+FLG) % 31 == 0).</para>
    /// </summary>
    public static bool IsZlibFormat(byte[] data)
    {
        return data is { Length: >= 2 } &&
               (data[0] & 0x0F) == 8 &&
               (data[0] >> 4) <= 7 &&
               (((data[0] << 8) | data[1]) % 31) == 0;
    }
}