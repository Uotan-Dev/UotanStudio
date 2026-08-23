using System.IO.Compression;
using UotanStudio.Firmware.Compress.Internal;
using SCCompressionMode = SharpCompress.Compressors.CompressionMode;
using SCDeflateStream = SharpCompress.Compressors.Deflate.DeflateStream;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// 原始 DEFLATE 压缩/解压（基于 .NET 内置 DeflateStream，RFC 1951，无 zlib/gzip 包装头）。
/// <para>Raw DEFLATE compression/decompression (based on the .NET built-in DeflateStream, RFC 1951, without zlib/gzip wrapping).</para>
/// </summary>
public static class DeflateCompressor
{
    /// <summary>
    /// DEFLATE 压缩。
    /// <para>DEFLATE compression.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="options">压缩选项（Level：0-9，null=Optimal）。<para>Compression options (Level: 0-9, null=Optimal).</para></param>
    /// <returns>原始 deflate 流。<para>Raw deflate stream.</para></returns>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            // 并行分块：每块压缩为独立 deflate 流后按序拼接（多成员 deflate，由本库 TotalIn 循环解压）。
            var parallel = ParallelCompression.TryCompressChunks(data, options?.MaxDegreeOfParallelism,
                (start, count) => CompressChunk(data, start, count, options));
            if (parallel != null)
            {
                return parallel;
            }

            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 压缩失败", ex);
        }
    }

    /// <summary>
    /// 把 [start, start+count) 压缩为独立的 raw deflate 流（并行分块用）。
    /// <para>Compresses [start, start+count) into an independent raw deflate stream (for parallel chunking).</para>
    /// </summary>
    private static byte[] CompressChunk(byte[] data, int start, int count, CompressionOptions? options)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true))
        {
            deflate.Write(data, start, count);
        }
        return output.ToArray();
    }

    /// <summary>
    /// DEFLATE 解压。
    /// <para>DEFLATE decompression.</para>
    /// </summary>
    /// <param name="data">原始 deflate 流。<para>Raw deflate stream.</para></param>
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

            // 逐成员解压：并行模式产出多成员 raw deflate 流，用 SharpCompress 解码器
            // 的 TotalIn 精确定位成员边界（.NET DeflateStream 无法定位且只解首个成员）。
            // Decompress member by member: parallel mode produces multi-member raw deflate
            // streams; SharpCompress's TotalIn locates each member boundary exactly.
            while (offset < data.Length)
            {
                using var sub = new MemoryStream(data, offset, data.Length - offset, writable: false);
                using var deflate = new SCDeflateStream(sub, SCCompressionMode.Decompress);
                deflate.CopyTo(output);

                var consumed = deflate.TotalIn;
                if (consumed <= 0)
                {
                    throw new CompressionException("DEFLATE 数据格式无效：无法定位成员边界");
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
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式 DEFLATE 压缩。
    /// <para>Streaming DEFLATE compression.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入压缩结果的输出流。<para>The output stream that receives the compressed data.</para></param>
    /// <param name="options">压缩选项（Level：0-9，null=Optimal）。<para>Compression options (Level: 0-9, null=Optimal).</para></param>
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
            using var deflate = new DeflateStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true);
            input.CopyTo(deflate);
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 压缩失败", ex);
        }
    }

    /// <summary>
    /// 流式 DEFLATE 解压。
    /// <para>Streaming DEFLATE decompression.</para>
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
            if (input.CanSeek)
            {
                // 可定位流：逐成员解压，用 TotalIn 精确推进到下一成员起点。
                // Seekable input: decompress member by member, advancing by TotalIn.
                while (true)
                {
                    var memberStart = input.Position;
                    using var deflate = new SCDeflateStream(new NonDisposingStream(input), SCCompressionMode.Decompress);
                    deflate.CopyTo(output);

                    var consumed = deflate.TotalIn;
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
                using var deflate = new SCDeflateStream(new NonDisposingStream(input), SCCompressionMode.Decompress);
                deflate.CopyTo(output);
            }
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }
}