using System.Buffers.Binary;
using System.IO;
using ICSharpCode.SharpZipLib.Zip.Compression;
using K4os.Compression.LZ4;
using SevenZip;
using SevenZip.Compression.LZMA;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 压缩辅助：pcluster 索引结构与四算法编解码（LZ4 / LZMA / DEFLATE / ZSTD），
/// 压缩配置与索引数据结构严格对齐 Linux 内核主线 fs/erofs/erofs_fs.h 与 fs/erofs/zmap.c。
/// 所有编解码均为纯托管实现（K4os.Compression.LZ4、LZMA-SDK、SharpZipLib、ZstdSharp.Port），
/// 无 P/Invoke、无 unsafe，AOT 安全。
/// <para>EROFS compression helpers: pcluster index structures and four-algorithm codecs
/// (LZ4 / LZMA / DEFLATE / ZSTD). The compression configuration and index data structures strictly
/// follow the Linux kernel mainline fs/erofs/erofs_fs.h and fs/erofs/zmap.c. All codecs are pure
/// managed (K4os.Compression.LZ4, LZMA-SDK, SharpZipLib, ZstdSharp.Port) — no P/Invoke, no unsafe,
/// AOT-safe.</para>
/// </summary>
internal static class ErofsCompression
{
    /// <summary>每个 pcluster 索引条目的大小（8 字节）。Size of one pcluster index entry (8 bytes).</summary>
    public const int PclusterIndexSize = 8;

    /// <summary>索引条目标志：pcluster 以原始（未压缩）方式存储。Index flag: pcluster stored plain (uncompressed).</summary>
    public const byte FlagPlain = 0x01;

    /// <summary>最大 pcluster 物理大小（<c>Z_EROFS_PCLUSTER_MAX_SIZE</c>，1 MiB）。
    /// Maximum pcluster physical size (1 MiB).</summary>
    public const int MaxPclusterSize = 1 << 20;

    /// <summary>最大 pcluster 解压大小（<c>Z_EROFS_PCLUSTER_MAX_DSIZE</c>，12 MiB）。
    /// Maximum pcluster decompressed size (12 MiB).</summary>
    public const int MaxPclusterDecompressedSize = 12 << 20;

    /// <summary>.lzma 单独流头部大小（5 属性字节 + 8 未压缩大小字节，Z_EROFS_LZMA_FORMAT_ALONE）。
    /// Size of the .lzma alone-stream header (5 property bytes + 8 uncompressed-size bytes,
    /// Z_EROFS_LZMA_FORMAT_ALONE).</summary>
    public const int LzmaHeaderSize = 13;

    /// <summary>
    /// 计算文件所需 pcluster 数量。pcluster 与逻辑簇同大小（默认等于块大小 4096），
    /// 每个 pcluster 占据一个物理块，pcluster 边界对齐 4KB。
    /// <para>Computes the number of pclusters required for a file. A pcluster has the same size as a
    /// logical cluster (defaulting to the 4096-byte block size); each pcluster occupies one physical
    /// block, so pcluster boundaries are 4KB-aligned.</para>
    /// </summary>
    public static int PclusterCount(ulong size, int clusterSize)
    {
        if (size == 0)
        {
            return 0;
        }

        // Avoid size + clusterSize - 1 overflowing, and require the result to fit an int
        // so callers can bound the index table.
        // 避免 size + clusterSize - 1 溢出；结果须能放入 int，以便调用方约束索引表。
        var count = (size - 1) / (ulong)clusterSize + 1;
        if (count > int.MaxValue)
        {
            throw new InvalidDataException($"Pcluster count {count} exceeds the supported limit of {int.MaxValue}.");
        }

        return (int)count;
    }

    /// <summary>
    /// 将 1-9 的压缩级别映射为 K4os LZ4Level（1-4 为快速模式，5-9 为高压缩模式）。
    /// <para>Maps a 1-9 compression level to a K4os LZ4Level (1-4 fast, 5-9 high compression).</para>
    /// </summary>
    public static LZ4Level ToLz4Level(int compressionLevel)
        => (LZ4Level)Math.Clamp(compressionLevel, 0, (int)LZ4Level.L12_MAX);

    /// <summary>
    /// 压缩单个逻辑簇（LZ4，与既有实现一致），返回压缩字节数。
    /// <para>Compresses a single logical cluster with LZ4 (unchanged legacy behavior), returning the
    /// compressed byte count.</para>
    /// </summary>
    public static int Compress(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel)
        => CompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel);

    /// <summary>
    /// 解压单个 pcluster（LZ4，与既有实现一致），返回解压字节数。
    /// <para>Decompresses a single pcluster with LZ4 (unchanged legacy behavior), returning the
    /// decompressed byte count.</para>
    /// </summary>
    public static int Decompress(byte[] source, int sourceIndex, int sourceLength, byte[] target, int targetIndex, int targetLength)
        => DecompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength);

    /// <summary>
    /// 计算 LZ4 压缩所需的最大输出字节数。
    /// <para>Computes the maximum LZ4 output size for a given input length.</para>
    /// </summary>
    public static int MaximumOutputSize(int length) => LZ4Codec.MaximumOutputSize(length);

    /// <summary>
    /// 计算指定算法压缩所需的安全最大输出字节数（保守上界）。
    /// <para>Computes a conservative maximum output size bound for the given algorithm.</para>
    /// </summary>
    public static int MaximumOutputSize(ErofsAlgorithm algorithm, int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return algorithm switch
        {
            ErofsAlgorithm.Lz4 => LZ4Codec.MaximumOutputSize(length),
            ErofsAlgorithm.Lzma => LzmaMaximumOutputSize(length),
            ErofsAlgorithm.Deflate => DeflateMaximumOutputSize(length),
            ErofsAlgorithm.Zstd => ZstdMaximumOutputSize(length),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm."),
        };
    }

    /// <summary>
    /// 按算法分派压缩（dictSize/windowBits 使用算法默认值）。
    /// <para>Algorithm-dispatched compression (dictSize/windowBits use the codec defaults).</para>
    /// </summary>
    public static int AlgorithmCompress(
        ErofsAlgorithm algorithm,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel)
        => AlgorithmCompress(algorithm, source, sourceIndex, sourceLength, target, targetIndex, targetLength,
            compressionLevel, 0, 0);

    /// <summary>
    /// 按算法分派压缩，返回压缩字节数。
    /// <para>Algorithm-dispatched compression, returning the compressed byte count.</para>
    /// </summary>
    /// <param name="algorithm">压缩算法（LZ4/LZMA/DEFLATE/ZSTD）。Compression algorithm.</param>
    /// <param name="source">待压缩数据。Source data.</param>
    /// <param name="sourceIndex">源数据起始偏移。Start offset in the source.</param>
    /// <param name="sourceLength">源数据长度。Source length.</param>
    /// <param name="target">压缩输出缓冲区。Compressed output buffer.</param>
    /// <param name="targetIndex">输出起始偏移。Start offset in the target.</param>
    /// <param name="targetLength">输出容量。Target capacity.</param>
    /// <param name="compressionLevel">压缩级别 1-9。Compression level 1-9.</param>
    /// <param name="dictSize">LZMA 字典大小（须为 2 的幂；0 = 按输入自动推导）。
    /// LZMA dictionary size (must be a power of two; 0 = derived from the input).</param>
    /// <param name="windowBits">DEFLATE 窗口位（仅支持 0 或 15）或 ZSTD 窗口对数（0 = 默认）。
    /// DEFLATE window bits (0 or 15 only) or ZSTD window log (0 = default).</param>
    public static int AlgorithmCompress(
        ErofsAlgorithm algorithm,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        int dictSize,
        int windowBits)
    {
        ValidateRange(source, sourceIndex, sourceLength, nameof(source));
        ValidateRange(target, targetIndex, targetLength, nameof(target));

        return algorithm switch
        {
            ErofsAlgorithm.Lz4 => CompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel),
            ErofsAlgorithm.Lzma => CompressLzma(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, dictSize),
            ErofsAlgorithm.Deflate => CompressDeflate(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, windowBits),
            ErofsAlgorithm.Zstd => CompressZstd(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, windowBits),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm."),
        };
    }

    /// <summary>
    /// 按算法分派解压（dictSize/windowBits 使用默认值）。
    /// <para>Algorithm-dispatched decompression (dictSize/windowBits use the codec defaults).</para>
    /// </summary>
    public static int AlgorithmDecompress(
        ErofsAlgorithm algorithm,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength)
        => AlgorithmDecompress(algorithm, source, sourceIndex, sourceLength, target, targetIndex, targetLength,
            0, 0);

    /// <summary>
    /// 按算法分派解压，返回解压字节数。
    /// <para>Algorithm-dispatched decompression, returning the decompressed byte count.</para>
    /// </summary>
    /// <param name="algorithm">压缩算法（LZ4/LZMA/DEFLATE/ZSTD）。Compression algorithm.</param>
    /// <param name="source">压缩数据。Compressed data.</param>
    /// <param name="sourceIndex">压缩数据起始偏移。Start offset in the source.</param>
    /// <param name="sourceLength">压缩数据长度。Compressed length.</param>
    /// <param name="target">解压输出缓冲区。Decompressed output buffer.</param>
    /// <param name="targetIndex">输出起始偏移。Start offset in the target.</param>
    /// <param name="targetLength">输出容量（解压后大小）。Target capacity (decompressed size).</param>
    /// <param name="dictSize">LZMA 字典大小回退（仅在 .lzma 头字典为 0 时使用；0 = 完全按头解析）。
    /// LZMA dictionary-size fallback (used only when the .lzma header dictionary is 0; 0 = use the
    /// header).</param>
    /// <param name="windowBits">DEFLATE 窗口位（仅支持 0 或 15）或 ZSTD 最大窗口对数（0 = 默认）。
    /// DEFLATE window bits (0 or 15 only) or ZSTD maximum window log (0 = default).</param>
    public static int AlgorithmDecompress(
        ErofsAlgorithm algorithm,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int dictSize,
        int windowBits)
    {
        ValidateRange(source, sourceIndex, sourceLength, nameof(source));
        ValidateRange(target, targetIndex, targetLength, nameof(target));

        return algorithm switch
        {
            ErofsAlgorithm.Lz4 => DecompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength),
            ErofsAlgorithm.Lzma => DecompressLzma(source, sourceIndex, sourceLength, target, targetIndex, targetLength, dictSize),
            ErofsAlgorithm.Deflate => DecompressDeflate(source, sourceIndex, sourceLength, target, targetIndex, targetLength, windowBits),
            ErofsAlgorithm.Zstd => DecompressZstd(source, sourceIndex, sourceLength, target, targetIndex, targetLength, windowBits),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm."),
        };
    }

    // ---- LZ4 codec (K4os.Compression.LZ4). ----

    private static int CompressLz4(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel)
        => LZ4Codec.Encode(source, sourceIndex, sourceLength, target, targetIndex, targetLength, ToLz4Level(compressionLevel));

    private static int DecompressLz4(byte[] source, int sourceIndex, int sourceLength, byte[] target, int targetIndex, int targetLength)
        => LZ4Codec.Decode(source, sourceIndex, sourceLength, target, targetIndex, targetLength);

    // ---- LZMA codec (LZMA-SDK). ----

    /// <summary>级别 → NumFastBytes 映射（与 LZMA SDK DefaultProp 表一致，超过 273 时截断，
    /// 该移植的最大合法值）。Level → NumFastBytes table (mirrors the LZMA SDK DefaultProp table;
    /// values above 273 are clamped, which is this port's legal maximum).</summary>
    private static readonly int[] LzmaNumFastBytesByLevel = [32, 64, 128, 128, 256, 384, 512, 768, 1024];

    /// <summary>
    /// LZMA 压缩，输出 .lzma 单独流（Z_EROFS_LZMA_FORMAT_ALONE）：
    /// 5 字节属性头 + 8 字节小端未压缩大小 + LZMA 数据。
    /// <para>LZMA compression producing the .lzma alone stream (Z_EROFS_LZMA_FORMAT_ALONE):
    /// 5-byte property header + 8-byte little-endian uncompressed size + LZMA data.</para>
    /// </summary>
    private static int CompressLzma(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        int dictSize)
    {
        if (sourceLength == 0)
        {
            return 0;
        }

        var dictionarySize = ResolveLzmaDictionarySize(sourceLength, dictSize);
        var encoder = new Encoder();
        encoder.SetCoderProperties(
            [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitContextBits,
                CoderPropID.LitPosBits, CoderPropID.Algorithm, CoderPropID.NumFastBytes],
            [dictionarySize, 2, 3, 0, compressionLevel >= 5 ? 2 : 1, LzmaNumFastBytes(compressionLevel)]);

        using var input = new MemoryStream(source, sourceIndex, sourceLength, writable: false);
        using var output = new BoundedWriteStream(target, targetIndex, targetLength);

        encoder.WriteCoderProperties(output);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(header, (ulong)sourceLength);
        output.Write(header);
        encoder.Code(input, output, sourceLength, -1, null);
        return checked((int)output.Position);
    }

    /// <summary>
    /// LZMA 解压，接受 .lzma 单独流（5 字节属性 + 8 字节大小 + 数据）。
    /// <para>LZMA decompression accepting the .lzma alone stream (5 property bytes + 8-byte size +
    /// data).</para>
    /// </summary>
    private static int DecompressLzma(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int dictSize)
    {
        if (sourceLength == 0)
        {
            return 0;
        }

        if (sourceLength < LzmaHeaderSize)
        {
            throw new InvalidDataException("LZMA compressed data is too short (missing the .lzma header).");
        }

        var properties = source.AsSpan(sourceIndex, 5).ToArray();
        var headerDictSize = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(1));
        if (headerDictSize == 0 && dictSize > 0)
        {
            if (!IsPowerOfTwo(dictSize))
            {
                throw new ArgumentOutOfRangeException(nameof(dictSize), dictSize,
                    "LZMA dictionary size must be a power of two.");
            }

            properties[1] = (byte)dictSize;
            properties[2] = (byte)(dictSize >> 8);
            properties[3] = (byte)(dictSize >> 16);
            properties[4] = (byte)(dictSize >> 24);
        }

        var decoder = new Decoder();
        decoder.SetDecoderProperties(properties);

        var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(sourceIndex + 5, 8));
        long outSize = uncompressedSize == ulong.MaxValue ? targetLength : checked((long)uncompressedSize);
        if (outSize < 0 || outSize > targetLength)
        {
            throw new InvalidDataException(
                $"LZMA header declares an uncompressed size of {outSize} bytes, which exceeds the destination buffer ({targetLength} bytes).");
        }

        using var input = new MemoryStream(source, sourceIndex + LzmaHeaderSize, sourceLength - LzmaHeaderSize, writable: false);
        using var output = new BoundedWriteStream(target, targetIndex, targetLength);
        decoder.Code(input, output, -1, outSize, null);
        if (output.Position != outSize)
        {
            throw new InvalidDataException("LZMA stream produced fewer bytes than its header declares.");
        }

        return checked((int)output.Position);
    }

    private static int ResolveLzmaDictionarySize(int sourceLength, int dictSize)
    {
        if (dictSize > 0)
        {
            if (!IsPowerOfTwo(dictSize))
            {
                throw new ArgumentOutOfRangeException(nameof(dictSize), dictSize,
                    "LZMA dictionary size must be a power of two.");
            }

            return dictSize;
        }

        // 2 的幂 ≥ sourceLength，截断到 [1 << 12, 1 << 23]（8 MiB，与 LZMA SDK 默认一致）。
        // Smallest power of two >= sourceLength, clamped to [1 << 12, 1 << 23] (8 MiB, matching the
        // LZMA SDK default).
        var d = 1 << 12;
        while (d < sourceLength && d < (1 << 23))
        {
            d <<= 1;
        }

        return d;
    }

    private static int LzmaNumFastBytes(int compressionLevel)
        => Math.Clamp(LzmaNumFastBytesByLevel[Math.Clamp(compressionLevel, 1, 9) - 1], 5, 273);

    // ---- DEFLATE codec (SharpZipLib, raw DEFLATE per RFC 1951). ----

    /// <summary>
    /// DEFLATE 压缩，输出原始 DEFLATE 流（无 zlib 头/尾，RFC 1951，与 erofs 内核 deflateInit2 的
    /// 负 windowBits 语义一致）。SharpZipLib 固定使用 15 位窗口。
    /// <para>DEFLATE compression producing a raw DEFLATE stream (no zlib header/footer, RFC 1951,
    /// matching the kernel's negative-windowBits deflateInit2 semantics). SharpZipLib always uses a
    /// 15-bit window.</para>
    /// </summary>
    private static int CompressDeflate(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        int windowBits)
    {
        ValidateDeflateWindowBits(windowBits);
        if (sourceLength == 0)
        {
            return 0;
        }

        var deflater = new Deflater(Math.Clamp(compressionLevel, 0, 9), noZlibHeaderOrFooter: true);
        deflater.SetInput(source, sourceIndex, sourceLength);
        deflater.Finish();

        var written = 0;
        while (!deflater.IsFinished)
        {
            var remaining = targetLength - written;
            if (remaining <= 0)
            {
                throw new InvalidOperationException("DEFLATE compressed output exceeds the destination buffer.");
            }

            var n = deflater.Deflate(target, targetIndex + written, remaining);
            if (n <= 0)
            {
                throw new InvalidOperationException("DEFLATE compressor made no progress.");
            }

            written += n;
        }

        return written;
    }

    /// <summary>
    /// DEFLATE 解压，接受原始 DEFLATE 流（无 zlib 头，RFC 1951）。
    /// <para>DEFLATE decompression accepting a raw DEFLATE stream (no zlib header, RFC 1951).</para>
    /// </summary>
    private static int DecompressDeflate(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int windowBits)
    {
        ValidateDeflateWindowBits(windowBits);
        if (sourceLength == 0)
        {
            return 0;
        }

        var inflater = new Inflater(noHeader: true);
        inflater.SetInput(source, sourceIndex, sourceLength);

        var written = 0;
        while (!inflater.IsFinished)
        {
            if (written >= targetLength)
            {
                // 输出缓冲已满：以零长度输出再驱动一次状态机，让 Inflater 消费流的结束块标记。
                // Output buffer is full: drive the state machine once more with a zero-length output
                // so the Inflater can consume the end-of-stream marker.
                inflater.Inflate(target, targetIndex, 0);
                if (inflater.IsFinished)
                {
                    break;
                }

                if (inflater.IsNeedingInput)
                {
                    throw new InvalidDataException("DEFLATE compressed data is truncated.");
                }

                throw new InvalidDataException("DEFLATE decompressed output exceeds the destination buffer.");
            }

            var n = inflater.Inflate(target, targetIndex + written, targetLength - written);
            if (n > 0)
            {
                written += n;
                continue;
            }

            if (inflater.IsFinished)
            {
                break;
            }

            if (inflater.IsNeedingInput)
            {
                throw new InvalidDataException("DEFLATE compressed data is truncated.");
            }

            throw new InvalidDataException("DEFLATE decompressor made no progress (corrupt stream).");
        }

        return written;
    }

    private static void ValidateDeflateWindowBits(int windowBits)
    {
        if (windowBits != 0 && windowBits != 15)
        {
            throw new NotSupportedException(
                "SharpZipLib only supports a 15-bit DEFLATE window; windowBits must be 0 (default) or 15.");
        }
    }

    // ---- ZSTD codec (ZstdSharp.Port). ----

    /// <summary>
    /// ZSTD 压缩（ZstdSharp 全托管移植），输出标准 zstd 帧；windowLog &gt; 0 时写入帧的窗口对数。
    /// <para>ZSTD compression (fully-managed ZstdSharp port) producing a standard zstd frame; when
    /// windowLog &gt; 0 the frame window log is set accordingly.</para>
    /// </summary>
    private static int CompressZstd(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        int windowLog)
    {
        if (sourceLength == 0)
        {
            return 0;
        }

        using var compressor = new Compressor(Math.Clamp(compressionLevel, -5, 22));
        if (windowLog > 0)
        {
            compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
        }

        try
        {
            return compressor.Wrap(source.AsSpan(sourceIndex, sourceLength), target.AsSpan(targetIndex, targetLength));
        }
        catch (ZstdException ex)
        {
            throw new InvalidOperationException(
                "ZSTD compressed output exceeds the destination buffer, or the compression parameters are invalid.", ex);
        }
    }

    /// <summary>
    /// ZSTD 解压（ZstdSharp 全托管移植）；windowLog &gt; 0 时约束最大窗口对数。
    /// <para>ZSTD decompression (fully-managed ZstdSharp port); when windowLog &gt; 0 the maximum
    /// window log is constrained accordingly.</para>
    /// </summary>
    private static int DecompressZstd(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int windowLog)
    {
        if (sourceLength == 0)
        {
            return 0;
        }

        using var decompressor = new Decompressor();
        if (windowLog > 0)
        {
            decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, windowLog);
        }

        try
        {
            return decompressor.Unwrap(source.AsSpan(sourceIndex, sourceLength), target.AsSpan(targetIndex, targetLength));
        }
        catch (ZstdException ex)
        {
            throw new InvalidDataException(
                "ZSTD decompression failed: the compressed data is corrupt or the destination buffer is too small.", ex);
        }
    }

    // ---- Maximum output size bounds. ----

    /// <summary>.lzma 流的保守压缩上界（13 字节头 + 输入 + 25% + 128，参考 liblzma
    /// lzma_stream_buffer_bound）。Conservative LZMA alone-stream bound (13-byte header + input +
    /// 25% + 128, per liblzma's lzma_stream_buffer_bound).</summary>
    private static int LzmaMaximumOutputSize(int length) => LzmaHeaderSize + length + (length >> 2) + 128;

    /// <summary>原始 DEFLATE 的保守压缩上界（zlib compressBound 公式）。
    /// Conservative raw-DEFLATE bound (the zlib compressBound formula).</summary>
    private static int DeflateMaximumOutputSize(int length) => length + (length >> 12) + (length >> 14) + (length >> 25) + 13;

    /// <summary>ZSTD 的保守压缩上界（ZSTD_compressBound 公式）。
    /// Conservative ZSTD bound (the ZSTD_compressBound formula).</summary>
    private static int ZstdMaximumOutputSize(int length)
    {
        var bound = length + (length >> 8);
        if (length < (128 << 10))
        {
            bound += (128 << 10) - length;
        }

        return bound;
    }

    // ---- Helpers. ----

    private static void ValidateRange(byte[] buffer, int offset, int length, string paramName)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || length < 0 || offset > buffer.Length - length)
        {
            throw new ArgumentOutOfRangeException(paramName,
                $"{paramName} range [{offset}..{offset + length}) exceeds the buffer length {buffer.Length}.");
        }
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}

/// <summary>
/// pcluster 索引条目（8 字节）：物理块号 + 存储长度 + 标志，构建器按压缩块表顺序写入，
/// 解析器在 inode 之后读取该表。
/// <para>Pcluster index entry (8 bytes): physical block number + stored length + flags. The builder
/// writes the compressed-block table in order right after the inode; the parser reads it from there.</para>
/// </summary>
internal readonly struct PclusterIndex
{
    /// <summary>条目大小。Entry size.</summary>
    public const int Size = 8;

    /// <summary>pcluster 数据起始块号（绝对）。Start block number of the pcluster data.</summary>
    public uint BlockAddress { get; }

    /// <summary>存储数据长度（压缩或原始）。Stored data length (compressed or plain).</summary>
    public ushort CompressedLength { get; }

    /// <summary>标志字节。Flags byte.</summary>
    public byte Flags { get; }

    /// <summary>是否以原始方式存储。Whether stored plain (uncompressed).</summary>
    public bool IsPlain => (Flags & ErofsCompression.FlagPlain) != 0;

    /// <summary>
    /// 创建索引条目。
    /// <para>Creates an index entry.</para>
    /// </summary>
    public PclusterIndex(uint blockAddress, ushort compressedLength, byte flags)
    {
        BlockAddress = blockAddress;
        CompressedLength = compressedLength;
        Flags = flags;
    }

    /// <summary>
    /// 从字节解析索引条目。
    /// <para>Parses an index entry from bytes.</para>
    /// </summary>
    public static PclusterIndex FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException("Pcluster index region too short.");
        }

        return new PclusterIndex(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)),
            data[6]);
    }

    /// <summary>
    /// 将索引条目写入缓冲区（长度至少 8 字节）。
    /// <para>Writes the index entry into a buffer of at least 8 bytes.</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Pcluster index destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, BlockAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), CompressedLength);
        destination[6] = Flags;
        destination[7] = 0;
    }
}

/// <summary>
/// 有界可写流：把输出写入调用方提供的 byte[] 区间，超限即抛 <see cref="InvalidOperationException"/>。
/// 用于 LZMA 这类面向 Stream 的编码器/解码器，避免额外分配。
/// <para>A bounded write-only stream that writes into a caller-provided byte[] region and throws
/// <see cref="InvalidOperationException"/> on overflow. Used for Stream-based codecs such as LZMA
/// without extra allocations.</para>
/// </summary>
internal sealed class BoundedWriteStream : Stream
{
    private readonly byte[] _buffer;
    private readonly int _offset;
    private readonly int _length;
    private int _position;

    public BoundedWriteStream(byte[] buffer, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || length < 0 || offset > buffer.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                $"BoundedWriteStream region [{offset}..{offset + length}) exceeds the buffer length {buffer.Length}.");
        }

        _buffer = buffer;
        _offset = offset;
        _length = length;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        WriteCore(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer);

    public override void WriteByte(byte value)
    {
        if (_position >= _length)
        {
            throw new InvalidOperationException("Compressed output exceeds the destination buffer length.");
        }

        _buffer[_offset + _position++] = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void WriteCore(ReadOnlySpan<byte> buffer)
    {
        var end = checked(_position + buffer.Length);
        if (end > _length)
        {
            throw new InvalidOperationException(
                "Compressed output exceeds the destination buffer length. Allocate a larger destination buffer.");
        }

        buffer.CopyTo(_buffer.AsSpan(_offset + _position));
        _position = end;
    }
}
