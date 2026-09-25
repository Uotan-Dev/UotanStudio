using SharpCompress.Compressors;
using SharpCompress.Compressors.Deflate;
using UotanStudio.Firmware.BoundedContexts.Compression.Internal;

namespace UotanStudio.Firmware.BoundedContexts.Compression.Compressors;

/// <summary>
/// ZLIB compression and decompression over byte arrays or streams, tolerant of concatenated zlib members.
/// <para>基于字节数组或流的 ZLIB 压缩解压，可容忍串联的多个 zlib 成员。</para>
/// </summary>
public static class ZlibCompressor
{
    /// <summary>
    /// ZLIB compression.
    /// <para>ZLIB 压缩。</para>
    /// </summary>
    /// <param name="data">Plaintext buffer to compress. <para>要压缩的明文缓冲区。</para></param>
    /// <param name="options">Optional compression level and parallel-chunking degree; <see langword="null"/>
    /// maps to the default level. <para>可选压缩级别与并行分块度；为 <see langword="null"/> 时采用默认级别。</para></param>
    /// <returns>A zlib stream, or concatenated per-chunk streams when parallel chunking applied.
    /// <para>zlib 流；启用并行分块时为逐块流的串联。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null. <para><paramref name="data"/> 为 null。</para></exception>
    /// <exception cref="CompressionException">Compression failed; the codec error is the inner exception.
    /// <para>压缩失败；编解码器错误为内部异常。</para></exception>
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
    /// <param name="data">Buffer holding one or more concatenated zlib members.
    /// <para>包含一个或多个串联 zlib 成员的缓冲区。</para></param>
    /// <returns>The decoded plaintext of all members concatenated. <para>全部成员解码明文按序拼接。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null. <para><paramref name="data"/> 为 null。</para></exception>
    /// <exception cref="CompressionException">A member boundary cannot be located or decoding failed.
    /// <para>无法定位成员边界或解码失败。</para></exception>
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
    /// <param name="input">Readable source stream, consumed to its end and left open.
    /// <para>可读源流，读取到末尾并保持打开。</para></param>
    /// <param name="output">Writable target stream; the zlib stream is flushed on completion but not closed.
    /// <para>可写目标流；完成时刷新 zlib 流但不关闭。</para></param>
    /// <param name="options">Optional compression level; <see langword="null"/> maps to the default level.
    /// <para>可选压缩级别；为 <see langword="null"/> 时采用默认级别。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 null。</para></exception>
    /// <exception cref="CompressionException">Compression or stream copying failed. <para>压缩或流复制失败。</para></exception>
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
    /// <param name="input">Readable compressed stream, left open; seekable inputs decode every concatenated
    /// member, non-seekable inputs stop after the first. The decoded size is unbounded, so callers handling
    /// untrusted input must cap <paramref name="output"/> themselves.
    /// <para>可读压缩流并保持打开；可定位的输入会解码全部串联成员，不可定位的输入在首个成员后停止。解码大小
    /// 无上限，处理不可信输入的调用方需自行限制 <paramref name="output"/>。</para></param>
    /// <param name="output">Writable target stream, not closed. <para>可写目标流，不会被关闭。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 null。</para></exception>
    /// <exception cref="CompressionException">Decompression or stream copying failed. <para>解压或流复制失败。</para></exception>
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
    /// <param name="data">Buffer to inspect; <see langword="null"/> or shorter than two bytes never matches.
    /// <para>要检查的缓冲区；为 <see langword="null"/> 或少于两字节时永不匹配。</para></param>
    /// <returns><see langword="true"/> when the two header bytes pass the zlib checksum heuristic.
    /// <para>头部两字节通过 zlib 校验启发式时返回 <see langword="true"/>。</para></returns>
    public static bool IsZlibFormat(byte[] data)
    {
        return data is { Length: >= 2 } &&
               (data[0] & 0x0F) == 8 &&
               (data[0] >> 4) <= 7 &&
               (((data[0] << 8) | data[1]) % 31) == 0;
    }
}