using System.Buffers;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using UotanStudio.Firmware.Compress.Internal;

namespace UotanStudio.Firmware.Compress.Compressors;

/// <summary>
/// LZ4 压缩/解压：标准帧（LZ4Stream）与传统块帧（magiskboot 风格）。
/// <para>LZ4 compression/decompression: standard frame (LZ4Stream) and legacy block
/// framing (magiskboot style).</para>
/// </summary>
public static class Lz4Compressor
{
    private const int BlockChunkSize = 0x10000;

    // Standard frame / 标准帧

    /// <summary>
    /// 标准 LZ4 帧压缩。
    /// <para>Standard LZ4 frame compression.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="options">压缩选项（Level：0-12，null=L00_FAST）。<para>Compression options (Level: 0-12, null=L00_FAST).</para></param>
    /// <returns>标准 LZ4 帧数据。<para>Standard LZ4 frame data.</para></returns>
    public static byte[] Compress(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            // 注：LZ4 不做分块并行。K4os LZ4Stream 解码器对 ≥512 KB 的串联帧不可靠
            // （只解首个帧），故保持单帧串行输出。
            // Note: LZ4 is not chunked in parallel. The K4os LZ4Stream decoder does not
            // reliably decode concatenated frames ≥512 KB (only the first frame is read),
            // so single-frame sequential output is kept.
            using var output = new MemoryStream();
            using (var encoder = LZ4Stream.Encode(output, ToLz4Level(options?.Level), leaveOpen: true))
            {
                encoder.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 压缩失败", ex);
        }
    }

    /// <summary>
    /// 标准 LZ4 帧解压。
    /// <para>Standard LZ4 frame decompression.</para>
    /// </summary>
    /// <param name="data">标准 LZ4 帧数据。<para>Standard LZ4 frame data.</para></param>
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
            using var decoder = LZ4Stream.Decode(input);
            using var output = new MemoryStream();
            decoder.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式标准 LZ4 帧压缩。
    /// <para>Streaming standard LZ4 frame compression.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入压缩结果的输出流。<para>The output stream that receives the compressed data.</para></param>
    /// <param name="options">压缩选项（Level：0-12，null=L00_FAST）。<para>Compression options (Level: 0-12, null=L00_FAST).</para></param>
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
            using var encoder = LZ4Stream.Encode(output, ToLz4Level(options?.Level), leaveOpen: true);
            input.CopyTo(encoder);
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 压缩失败", ex);
        }
    }

    /// <summary>
    /// 流式标准 LZ4 帧解压。
    /// <para>Streaming standard LZ4 frame decompression.</para>
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
            using var decoder = LZ4Stream.Decode(input, leaveOpen: true);
            decoder.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式标准 LZ4 帧解压，带输出上限（防解压炸弹）。
    /// <para>Streaming standard LZ4 frame decompression with output limit (decompression bomb protection).</para>
    /// </summary>
    /// <param name="source">待解压的压缩流。<para>The compressed input stream.</para></param>
    /// <param name="destination">写入解压结果的输出流。<para>The output stream that receives the decompressed data.</para></param>
    /// <param name="maxOutputBytes">最大允许的解压输出字节数，超出则抛出异常。<para>Maximum allowed decompressed output in bytes; throws if exceeded.</para></param>
    /// <exception cref="CompressionException">解压输出超过 <paramref name="maxOutputBytes"/> 时抛出。
    /// <para>Thrown when decompressed output exceeds <paramref name="maxOutputBytes"/>.</para></exception>
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
            using var decoder = LZ4Stream.Decode(source, leaveOpen: true);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalWritten = 0;
                int bytesRead;
                while ((bytesRead = decoder.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (totalWritten + bytesRead > maxOutputBytes)
                    {
                        throw new CompressionException(
                            $"LZ4 解压输出超过上限 ({maxOutputBytes} 字节) / Decompressed output exceeds limit ({maxOutputBytes} bytes)");
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
            throw new CompressionException("LZ4 解压失败", ex);
        }
    }

    // Legacy block framing (magiskboot style) / 传统块帧

    /// <summary>
    /// LZ4 传统格式压缩（0x02 0x21 0x4C 0x18 魔数 + 4 字节块长前缀）。
    /// <para>LZ4 legacy compression (0x02 0x21 0x4C 0x18 magic + 4-byte block length prefixes).</para>
    /// </summary>
    public static byte[] CompressLegacy(byte[] data, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var output = new MemoryStream();
            CompressLegacy(new MemoryStream(data, writable: false), output, options);
            return output.ToArray();
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4_LEGACY 压缩失败", ex);
        }
    }

    /// <summary>
    /// 流式 LZ4 传统格式压缩：按 64 KiB 块边读边压（有界内存），直接写入输出流。
    /// <para>Streaming LZ4 legacy compression: reads 64 KiB chunks, compresses and writes
    /// them in a bounded-memory pipeline directly to the output stream.</para>
    /// </summary>
    public static void CompressLegacy(Stream input, Stream output, CompressionOptions? options = null)
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
            CompressBlockFrame(input, output, [0x02, 0x21, 0x4C, 0x18], ToLz4Level(options?.Level), options?.MaxDegreeOfParallelism);
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4_LEGACY 压缩失败", ex);
        }
    }

    /// <summary>
    /// LZ4 传统格式解压。
    /// <para>LZ4 legacy decompression.</para>
    /// </summary>
    public static byte[] DecompressLegacy(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            using var output = new MemoryStream();
            DecompressLegacy(new MemoryStream(data, writable: false), output);
            return output.ToArray();
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4_LEGACY 解压失败", ex);
        }
    }

    /// <summary>
    /// 流式 LZ4 传统格式解压：按块读取并解码（有界内存），直接写入输出流。
    /// <para>Streaming LZ4 legacy decompression: reads and decodes block by block
    /// (bounded memory), writing directly to the output stream.</para>
    /// </summary>
    public static void DecompressLegacy(Stream input, Stream output)
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
            // 可选魔数（0x02 0x21 0x4C 0x18）；无魔数时前 4 字节按块长前缀处理。
            var magic = new byte[4];
            var magicRead = ReadUpTo(input, magic);
            if (magicRead < 4)
            {
                throw new CompressionException("LZ4_LEGACY 数据格式无效：魔数截断");
            }

            if (magic[0] != 0x02 || magic[1] != 0x21 || magic[2] != 0x4C || magic[3] != 0x18)
            {
                throw new CompressionException("LZ4_LEGACY 数据格式无效：缺少魔数");
            }

            var lengthBuf = new byte[4];
            byte[]? pooled = null;
            byte[]? block = null;
            try
            {
                while (true)
                {
                    var lenRead = ReadUpTo(input, lengthBuf);
                    if (lenRead == 0)
                    {
                        break; // 正常结束（无更多块）
                    }
                    if (lenRead < 4)
                    {
                        throw new CompressionException("LZ4_LEGACY 数据格式无效：块长度头截断");
                    }

                    var blockLength = lengthBuf[0] | (lengthBuf[1] << 8) | (lengthBuf[2] << 16) | (lengthBuf[3] << 24);
                    if (blockLength <= 0)
                    {
                        throw new CompressionException("LZ4_LEGACY 数据格式无效：块长度非法");
                    }
                    const int maxDecompressed = 8 * 1024 * 1024;
                    var maximumBlockLength = LZ4Codec.MaximumOutputSize(maxDecompressed);
                    if (blockLength > maximumBlockLength)
                    {
                        throw new CompressionException($"LZ4_LEGACY 数据格式无效：块长度超过安全上限 {maximumBlockLength}");
                    }

                    // 输入块缓冲也跨块复用 ArrayPool；池化数组可能大于块长，须精确读取。
                    if (block == null || block.Length < blockLength)
                    {
                        if (block != null)
                        {
                            ArrayPool<byte>.Shared.Return(block);
                        }

                        block = ArrayPool<byte>.Shared.Rent(blockLength);
                    }
                    var blockRead = ReadExactly(input, block, blockLength);
                    if (blockRead < blockLength)
                    {
                        throw new CompressionException("LZ4_LEGACY 数据格式无效：块数据截断");
                    }

                    // Legacy readers must accept the standard legacy frame block size (8 MiB),
                    // while the writer remains free to emit smaller 64 KiB blocks.
                    if (pooled == null || pooled.Length < maxDecompressed)
                    {
                        if (pooled != null)
                        {
                            ArrayPool<byte>.Shared.Return(pooled);
                        }

                        pooled = ArrayPool<byte>.Shared.Rent(maxDecompressed);
                    }
                    var decoded = LZ4Codec.Decode(block, 0, blockLength, pooled, 0, maxDecompressed);
                    if (decoded <= 0)
                    {
                        throw new CompressionException("LZ4_LEGACY 数据格式无效：块解码失败");
                    }

                    output.Write(pooled, 0, decoded);
                }
            }
            finally
            {
                if (pooled != null)
                {
                    ArrayPool<byte>.Shared.Return(pooled);
                }

                if (block != null)
                {
                    ArrayPool<byte>.Shared.Return(block);
                }
            }
        }
        catch (CompressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException("LZ4_LEGACY 解压失败", ex);
        }
    }

    // Detection / 格式检测

    /// <summary>
    /// 检测数据是否为标准 LZ4 帧（魔数 04 22 4D 18）。
    /// <para>Detects whether the data is a standard LZ4 frame (magic 04 22 4D 18).</para>
    /// </summary>
    public static bool IsLz4Format(byte[] data)
    {
        return data is { Length: >= 4 } &&
               data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18;
    }

    /// <summary>
    /// 检测数据是否为 LZ4 传统格式（魔数 02 21 4C 18）。
    /// <para>Detects whether the data is in LZ4 legacy format (magic 02 21 4C 18).</para>
    /// </summary>
    public static bool IsLz4LegacyFormat(byte[] data)
    {
        return data is { Length: >= 4 } &&
               data[0] == 0x02 && data[1] == 0x21 && data[2] == 0x4C && data[3] == 0x18;
    }

    private static LZ4Level ToLz4Level(int? level)
    {
        if (!level.HasValue)
        {
            return LZ4Level.L00_FAST;
        }

        var v = level.Value;
        return (LZ4Level)(v < 0 ? 0 : v > 12 ? 12 : v);
    }

    /// <summary>
    /// 共享的 LZ4 块帧流式压缩：写魔数后按 64 KiB 块边读边压（有界内存），
    /// 每块独立压缩（无跨块依赖），支持有界窗口并行；按块序拼接，输出与串行逐字节一致。
    /// <para>Shared LZ4 block-frame streaming compression: writes the magic then reads and
    /// compresses 64 KiB chunks in a bounded-memory pipeline. Each chunk is compressed
    /// independently (no cross-chunk dependency), enabling bounded-window parallelism;
    /// results are appended in chunk order, byte-identical to sequential output.</para>
    /// </summary>
    private static void CompressBlockFrame(Stream input, Stream output, byte[] magic, LZ4Level level, int? maxDegreeOfParallelism)
    {
        output.Write(magic, 0, magic.Length);

        var dop = Parallelism.Resolve(maxDegreeOfParallelism, int.MaxValue);
        var chunks = new List<byte[]>(dop);

        while (true)
        {
            // 批量读取至多 dop 个 64 KiB 块（内存有界）。
            chunks.Clear();
            while (chunks.Count < dop)
            {
                var chunk = new byte[BlockChunkSize];
                var read = ReadUpTo(input, chunk);
                if (read == 0)
                {
                    break; // EOF
                }

                if (read < chunk.Length)
                {
                    Array.Resize(ref chunk, read);
                }

                chunks.Add(chunk);
            }
            if (chunks.Count == 0)
            {
                break;
            }

            // 各块独立压缩（LZ4 块编码无跨块状态，线程安全）。
            var compressed = new byte[chunks.Count][];
            var sizes = new int[chunks.Count];
            if (chunks.Count > 1 && dop > 1)
            {
                Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
                {
                    var buf = new byte[LZ4Codec.MaximumOutputSize(chunks[i].Length)];
                    sizes[i] = LZ4Codec.Encode(chunks[i], 0, chunks[i].Length, buf, 0, buf.Length, level);
                    compressed[i] = buf;
                });
            }
            else
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    var buf = new byte[LZ4Codec.MaximumOutputSize(chunks[i].Length)];
                    sizes[i] = LZ4Codec.Encode(chunks[i], 0, chunks[i].Length, buf, 0, buf.Length, level);
                    compressed[i] = buf;
                }
            }

            // 按块序拼接输出（与串行一致）。
            for (var i = 0; i < chunks.Count; i++)
            {
                var size = sizes[i];
                output.WriteByte((byte)(size & 0xFF));
                output.WriteByte((byte)((size >> 8) & 0xFF));
                output.WriteByte((byte)((size >> 16) & 0xFF));
                output.WriteByte((byte)((size >> 24) & 0xFF));
                output.Write(compressed[i], 0, size);
            }
        }
    }

    /// <summary>
    /// 尽力读满 buffer（遇到 EOF 时返回实际读取字节数）。
    /// <para>Reads up to buffer.Length bytes; returns the actual count read (may be short at EOF).</para>
    /// </summary>
    private static int ReadUpTo(Stream input, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = input.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read;
    }

    /// <summary>
    /// 精确读取至多 count 字节（遇到 EOF 时返回实际读取字节数）。
    /// 用于池化缓冲：数组实际长度可能大于块长，必须按块长精确读取。
    /// <para>Reads exactly up to count bytes; returns the actual count read (may be short at EOF).
    /// Used with pooled buffers whose actual length may exceed the block length.</para>
    /// </summary>
    private static int ReadExactly(Stream input, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = input.Read(buffer, read, count - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read;
    }
}