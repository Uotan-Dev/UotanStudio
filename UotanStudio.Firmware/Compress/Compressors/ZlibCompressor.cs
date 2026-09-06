using SharpCompress.Compressors;
using SharpCompress.Compressors.Deflate;
using UotanStudio.Firmware.Compress.Internal;

namespace UotanStudio.Firmware.Compress.Compressors;

public static class ZlibCompressor
{
    /// <summary>
    /// ZLIB compression.
    /// <para>ZLIB 压缩。</para>
    /// </summary>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {

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
    /// Compresses [start, start+count) into an independent zlib stream (for parallel chunking).
    /// <para>把 [start, start+count) 压缩为独立的 zlib 流（并行分块用）。</para>
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
    /// ZLIB decompression.
    /// <para>ZLIB 解压。</para>
    /// </summary>
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
    /// Streaming ZLIB compression.
    /// <para>流式 ZLIB 压缩。</para>
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
    /// Streaming ZLIB decompression.
    /// <para>流式 ZLIB 解压。</para>
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
    /// Detects whether the data is in zlib format (heuristic: CM=8 and (CMF*256+FLG) % 31 == 0).
    /// <para>检测数据是否为 zlib 格式（启发式：CM=8 且 CMF/FLG 可被 31 整除）。</para>
    /// </summary>
    public static bool IsZlibFormat(byte[] data)
    {
        return data is { Length: >= 2 } &&
               (data[0] & 0x0F) == 8 &&
               (data[0] >> 4) <= 7 &&
               (((data[0] << 8) | data[1]) % 31) == 0;
    }
}
