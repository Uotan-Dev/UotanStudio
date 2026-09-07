using System.Buffers;
using System.IO.Compression;
using UotanStudio.Firmware.CrossCutting.Compress.Internal;
using SCCompressionMode = SharpCompress.Compressors.CompressionMode;
using SCDeflateStream = SharpCompress.Compressors.Deflate.DeflateStream;

namespace UotanStudio.Firmware.CrossCutting.Compress.Compressors;

public static class DeflateCompressor
{
    /// <summary>
    /// DEFLATE compression.
    /// <para>DEFLATE 压缩。</para>
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
    /// Compresses [start, start+count) into an independent raw deflate stream (for parallel chunking).
    /// <para>把 [start, start+count) 压缩为独立的 raw deflate 流（并行分块用）。</para>
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
    /// DEFLATE decompression.
    /// <para>DEFLATE 解压。</para>
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
    /// Streaming DEFLATE compression.
    /// <para>流式 DEFLATE 压缩。</para>
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
            using var deflate = new DeflateStream(output, CompressionLevelMapper.ToBclCompressionLevel(options?.Level), leaveOpen: true);
            input.CopyTo(deflate);
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 压缩失败", ex);
        }
    }

    /// <summary>
    /// Streaming DEFLATE decompression.
    /// <para>流式 DEFLATE 解压。</para>
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


                using var deflate = new SCDeflateStream(new NonDisposingStream(input), SCCompressionMode.Decompress);
                deflate.CopyTo(output);
            }
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming DEFLATE decompression with output limit (decompression bomb protection).
    /// <para>流式 DEFLATE 解压，带输出上限（防解压炸弹）。</para>
    /// </summary>
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
            long totalWritten = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                if (source.CanSeek)
                {


                    while (true)
                    {
                        var memberStart = source.Position;
                        using var deflate = new SCDeflateStream(new NonDisposingStream(source), SCCompressionMode.Decompress);
                        totalWritten = BoundedCopy(deflate, destination, buffer, totalWritten, maxOutputBytes);

                        var consumed = deflate.TotalIn;
                        if (consumed <= 0)
                        {
                            break;
                        }

                        var next = memberStart + consumed;
                        if (next >= source.Length)
                        {
                            break;
                        }

                        source.Position = next;
                    }
                }
                else
                {


                    using var deflate = new SCDeflateStream(new NonDisposingStream(source), SCCompressionMode.Decompress);
                    BoundedCopy(deflate, destination, buffer, totalWritten, maxOutputBytes);
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
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }

    /// <summary>
    /// Reads from source into destination, throwing if maxOutputBytes is exceeded.
    /// <para>从 source 读取数据写入 destination，超出 maxOutputBytes 则抛异常。</para>
    /// </summary>
    private static long BoundedCopy(Stream source, Stream destination, byte[] buffer, long totalWritten, long maxOutputBytes)
    {
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (totalWritten + bytesRead > maxOutputBytes)
            {
                throw new CompressionException(
                    $"DEFLATE 解压输出超过上限 ({maxOutputBytes} 字节) / Decompressed output exceeds limit ({maxOutputBytes} bytes)");
            }

            destination.Write(buffer, 0, bytesRead);
            totalWritten += bytesRead;
        }

        return totalWritten;
    }
}
