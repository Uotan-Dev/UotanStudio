using System.Buffers.Binary;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression;
using K4os.Compression.LZ4;
using SevenZip;
using SevenZip.Compression.LZMA;
using UotanStudio.Firmware.Compression.Codecs.Lz4;
using UotanStudio.Firmware.FileSystem.Erofs.Models;

namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// EROFS on-disk compression algorithm identifiers recorded in inodes and the superblock algorithm bitmap.
/// <para>EROFS 磁盘侧压缩算法标识，记录于 inode、超级块算法位图中。</para>
/// </summary>
public enum ErofsCompressionAlgorithm
{
    /// <summary>LZ4 block format, the codec carried by legacy (pre-5.13) compatibility images. <para>LZ4 块格式，legacy（pre-5.13）兼容镜像所使用的编解码器。</para></summary>
    Lz4 = 0,

    /// <summary>MicroLZMA stream without a size header or EOS marker. <para>不含大小头与 EOS 标记的 MicroLZMA 流。</para></summary>
    Lzma = 1,

    /// <summary>Raw DEFLATE stream (RFC 1951, no zlib header or footer). <para>原始 DEFLATE 流（RFC 1951，无 zlib 头/尾）。</para></summary>
    Deflate = 2,

    /// <summary>Zstandard frame format. <para>Zstandard 帧格式。</para></summary>
    Zstd = 3,
}

internal static class ErofsCompression
{
    public const int PclusterIndexSize = 8;

    public const byte FlagPlain = 0x01;

    public const int MaxPclusterSize = 1 << 20;

    public const int MaxPclusterDecompressedSize = 12 << 20;

    /// <summary>LZ4 history window size written into the compression configuration record (LZ4_DISTANCE_MAX).<para>写入压缩配置记录的最大 LZ4 匹配距离（LZ4_DISTANCE_MAX）。</para></summary>
    public const ushort Lz4DistanceMax = 65535;

    public const int LzmaHeaderSize = 13;

    /// <summary>
    /// Computes the number of pclusters required for a file.
    /// <para>计算文件所需 pcluster 数量。</para>
    /// </summary>
    public static int PclusterCount(ulong size, int clusterSize)
    {
        if (size == 0)
        {
            return 0;
        }

        var count = (size - 1) / (ulong)clusterSize + 1;
        if (count > int.MaxValue)
        {
            throw new FileSystemCorruptedException($"Pcluster count {count} exceeds the supported limit of {int.MaxValue}.");
        }

        return (int)count;
    }

    /// <summary>
    /// Compresses one pcluster with the managed LZ4 block encoder, returning the compressed byte count
    /// or zero when the stream does not fit the destination.
    /// <para>用托管 LZ4 块编码器压缩单个 pcluster，返回压缩字节数；输出放不进目标缓冲区时返回 0。</para>
    /// </summary>
    public static int Compress(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel)
        => CompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel,
            highCompression: false);

    /// <summary>
    /// Decompresses a single pcluster with LZ4 (unchanged legacy behavior), returning the decompressed byte count.
    /// <para>解压单个 pcluster（LZ4，与既有实现一致），返回解压字节数。</para>
    /// </summary>
    public static int Decompress(byte[] source, int sourceIndex, int sourceLength, byte[] target, int targetIndex, int targetLength)
        => DecompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength);

    /// <summary>
    /// Computes the maximum LZ4 output size for a given input length.
    /// <para>计算 LZ4 压缩所需的最大输出字节数。</para>
    /// </summary>
    public static int MaximumOutputSize(int length) => LZ4Codec.MaximumOutputSize(length);

    /// <summary>
    /// Computes a conservative maximum output size bound for the given algorithm.
    /// <para>计算指定算法压缩所需的安全最大输出字节数（保守上界）。</para>
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
    /// Algorithm-dispatched compression (dictSize/windowBits use the codec defaults).
    /// <para>按算法分派压缩（dictSize/windowBits 使用算法默认值）。</para>
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
    /// Algorithm-dispatched compression, returning the compressed byte count.
    /// <para>按算法分派压缩，返回压缩字节数。</para>
    /// </summary>
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
        => AlgorithmCompress(algorithm, source, sourceIndex, sourceLength, target, targetIndex, targetLength,
            compressionLevel, highCompression: false, dictSize, windowBits);

    /// <summary>
    /// Algorithm-dispatched compression with an explicit LZ4 encoder identity.
    /// <para>按算法分派压缩，并显式指定 LZ4 编码器身份。</para>
    /// </summary>
    /// <param name="algorithm">EROFS codec branch selected for this call. <para>本次调用选择的 EROFS 编解码分支。</para></param>
    /// <param name="source">Buffer holding the uncompressed input. <para>存放未压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the input region starts.
    /// <para><paramref name="source"/> 中输入区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of input bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的输入字节数。</para></param>
    /// <param name="target">Buffer the compressed bytes are written into. <para>写入压缩字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 中为输出预留的字节数。</para></param>
    /// <param name="compressionLevel">Codec level: steers the LZ4 search, selects LZMA algorithm and
    /// num-fast-bytes, is clamped to 0..9 for DEFLATE and -5..22 for ZSTD.
    /// <para>编解码级别：影响 LZ4 搜索、决定 LZMA 算法与 num-fast-bytes，DEFLATE 限制到 0..9，ZSTD 限制到 -5..22。</para></param>
    /// <param name="highCompression">Uses the LZ4HC search for LZ4 payloads instead of the fast
    /// single-hash matcher; ignored by the other codecs.
    /// <para>对 LZ4 数据使用 LZ4HC 搜索而非快速单哈希匹配器；其余算法忽略。</para></param>
    /// <param name="dictSize">MicroLZMA dictionary size in bytes; <c>0</c> derives one from the input
    /// length, other codecs ignore it. <para>MicroLZMA 字典字节数；<c>0</c> 按输入长度推导，其余算法忽略。</para></param>
    /// <param name="windowBits">DEFLATE window bits (8..15, <c>0</c> for the default) and ZSTD window
    /// log; ignored by LZ4 and LZMA. <para>DEFLATE 窗口位数（8..15，<c>0</c> 为默认）与 ZSTD 窗口对数；LZ4 与 LZMA 忽略。</para></param>
    /// <returns>The number of compressed bytes written to <paramref name="target"/>.
    /// <para>写入 <paramref name="target"/> 的压缩字节数。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="target"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="target"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A region exceeds its buffer, <paramref name="algorithm"/> is
    /// unknown, or <paramref name="dictSize"/>/<paramref name="windowBits"/> fall outside the codec limits.
    /// <para>区域超出缓冲区、<paramref name="algorithm"/> 未知，或 <paramref name="dictSize"/>/<paramref name="windowBits"/> 超出编解码上限。</para></exception>
    /// <exception cref="InvalidOperationException">DEFLATE or ZSTD output exceeds the destination buffer, or the
    /// LZMA encoder misbehaves. <para>DEFLATE 或 ZSTD 输出超出目标缓冲区，或 LZMA 编码器行为异常。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The LZMA payload does not fit the destination buffer.
    /// <para>LZMA 压缩数据放不进目标缓冲区。</para></exception>
    public static int AlgorithmCompress(
        ErofsAlgorithm algorithm,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        bool highCompression,
        int dictSize,
        int windowBits)
    {
        ValidateRange(source, sourceIndex, sourceLength, nameof(source));
        ValidateRange(target, targetIndex, targetLength, nameof(target));

        return algorithm switch
        {
            ErofsAlgorithm.Lz4 => CompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength,
                compressionLevel, highCompression),
            ErofsAlgorithm.Lzma => CompressLzma(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, dictSize),
            ErofsAlgorithm.Deflate => CompressDeflate(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, windowBits),
            ErofsAlgorithm.Zstd => CompressZstd(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, windowBits),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm."),
        };
    }

    /// <summary>
    /// Compresses one pcluster with the codec parameters carried by the options, including the LZ4
    /// encoder identity selected by <see cref="ErofsOptions.Lz4HighCompression"/>.
    /// <para>按选项中的编解码参数压缩单个 pcluster，包含 <see cref="ErofsOptions.Lz4HighCompression"/>
    /// 选择的 LZ4 编码器身份。</para>
    /// </summary>
    public static int AlgorithmCompress(
        ErofsOptions options,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength)
        => AlgorithmCompress(
            (ErofsAlgorithm)options.Algorithm,
            source, sourceIndex, sourceLength, target, targetIndex, targetLength,
            Math.Max(1, options.CompressionLevel),
            options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4HighCompression,
            options.DictionarySize,
            options.WindowBits == 0 && options.Algorithm == ErofsCompressionAlgorithm.Zstd ? 20 : options.WindowBits);

    /// <summary>
    /// Algorithm-dispatched decompression (dictSize/windowBits use the codec defaults).
    /// <para>按算法分派解压（dictSize/windowBits 使用默认值）。</para>
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
    /// Algorithm-dispatched decompression, returning the decompressed byte count.
    /// <para>按算法分派解压，返回解压字节数。</para>
    /// </summary>
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

    /// <summary>
    /// Encodes one LZ4 block with the managed destination-size encoder ported from liblz4 1.9.4.
    /// <para>用移植自 liblz4 1.9.4 的托管限定目标大小编码器输出一个 LZ4 块。</para>
    /// </summary>
    private static int CompressLz4(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        bool highCompression)
    {
        if (sourceLength == 0 || targetLength == 0)
        {
            return 0;
        }

        var encoded = Lz4DestinationCodec.Encode(
            source.AsSpan(sourceIndex, sourceLength),
            target.AsSpan(targetIndex, targetLength),
            highCompression ? 2 : 1,
            highCompression ? compressionLevel : Math.Clamp(10 - compressionLevel, 1, 10),
            out var consumed);
        return consumed == sourceLength ? encoded : 0;
    }

    private static int DecompressLz4(byte[] source, int sourceIndex, int sourceLength, byte[] target, int targetIndex, int targetLength)
        => LZ4Codec.Decode(source, sourceIndex, sourceLength, target, targetIndex, targetLength);

    private static readonly int[] LzmaNumFastBytesByLevel = [32, 64, 128, 128, 256, 384, 512, 768, 1024];

    /// <summary>
    /// Compresses data into an EROFS MicroLZMA stream without a size header or EOS marker.
    /// <para>将数据压缩为不含大小头和 EOS 标记的 EROFS MicroLZMA 流。</para>
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

        const int pb = 2;
        const int lp = 0;
        const int lc = 3;
        var encoder = new Encoder();
        encoder.SetCoderProperties(
            [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitContextBits,
                CoderPropID.LitPosBits, CoderPropID.Algorithm, CoderPropID.NumFastBytes],
            [dictionarySize, pb, lc, lp, compressionLevel >= 5 ? 2 : 1, LzmaNumFastBytes(compressionLevel)]);

        using var input = new MemoryStream(source, sourceIndex, sourceLength, writable: false);
        using var output = new BoundedWriteStream(target, targetIndex, targetLength);
        var start = output.Position;

        output.WriteByte(unchecked((byte)~((pb * 5 + lp) * 9 + lc)));
        using var raw = new MemoryStream();
        encoder.Code(input, raw, sourceLength, sourceLength, null);
        var rawBytes = raw.GetBuffer();
        if (raw.Length <= 0 || rawBytes[0] != 0)
        {
            throw new InvalidOperationException("LZMA encoder did not produce its leading 0x00 byte.");
        }

        var payload = raw.Length - 1;
        var room = targetLength - (int)output.Position;
        if (payload > room)
        {
            throw new FileSystemCorruptedException("LZMA compressed data exceeds the destination buffer.");
        }

        output.Write(rawBytes, 1, checked((int)payload));
        return checked((int)(output.Position - start));
    }

    /// <summary>
    /// LZMA decompression accepting the EROFS MicroLZMA stream (a negated property byte followed by raw LZMA data, without the .lzma 8-byte size header or an EOS marker).
    /// <para>LZMA 解压，接受 erofs 的 MicroLZMA 流（首字节为属性字节取反，随后为原始 LZMA 数据， 无 .lzma 8 字节大小头与 EOS）。</para>
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

        if (sourceLength < 1)
        {
            throw new FileSystemCorruptedException("LZMA compressed data is empty.");
        }

        var propertyByte = (byte)~(source[sourceIndex] & 0xFF);
        var dictionarySize = dictSize > 0
            ? dictSize
            : 8 << 20;
        if (dictionarySize < (1 << 12))
        {
            dictionarySize = 1 << 12;
        }

        var properties = new byte[5];
        properties[0] = propertyByte;
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(1), (uint)dictionarySize);

        var decoder = new Decoder();
        decoder.SetDecoderProperties(properties);

        var rebuilt = System.Buffers.ArrayPool<byte>.Shared.Rent(sourceLength);
        try
        {
            rebuilt[0] = 0;
            Buffer.BlockCopy(source, sourceIndex + 1, rebuilt, 1, sourceLength - 1);
            using var input = new MemoryStream(rebuilt, 0, sourceLength, writable: false);
            using var output = new BoundedWriteStream(target, targetIndex, targetLength);

            decoder.Code(input, output, sourceLength, targetLength, null);
            if (output.Position != targetLength)
            {
                throw new FileSystemCorruptedException(
                    $"LZMA stream produced {output.Position} bytes, expected {targetLength}.");
            }

            return checked((int)output.Position);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rebuilt);
        }
    }

    private static int ResolveLzmaDictionarySize(int sourceLength, int dictSize)
    {
        if (dictSize > 0)
        {
            if (dictSize is < 4096 or > 8 << 20)
            {
                throw new ArgumentOutOfRangeException(nameof(dictSize), dictSize,
                    "EROFS MicroLZMA dictionaries must be between 4096 and 8388608 bytes.");
            }

            return dictSize;
        }

        var d = 1 << 12;
        while (d < sourceLength && d < (1 << 23))
        {
            d <<= 1;
        }

        return d;
    }

    private static int LzmaNumFastBytes(int compressionLevel)
        => Math.Clamp(LzmaNumFastBytesByLevel[Math.Clamp(compressionLevel, 1, 9) - 1], 5, 273);

    /// <summary>
    /// DEFLATE compression producing a raw DEFLATE stream (no zlib header/footer, RFC 1951, matching the kernel's negative-windowBits deflateInit2 semantics).
    /// <para>DEFLATE 压缩，输出原始 DEFLATE 流（无 zlib 头/尾，RFC 1951，与 erofs 内核 deflateInit2 的 负 windowBits 语义一致）。</para>
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
        if (windowBits is > 0 and < 15) deflater.SetStrategy(DeflateStrategy.HuffmanOnly);
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
    /// DEFLATE decompression accepting a raw DEFLATE stream (no zlib header, RFC 1951).
    /// <para>DEFLATE 解压，接受原始 DEFLATE 流（无 zlib 头，RFC 1951）。</para>
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

        try
        {
            return InflateDeflateRaw(source, sourceIndex, sourceLength, target, targetIndex, targetLength);
        }
        catch (ICSharpCode.SharpZipLib.SharpZipBaseException exception)
        {
            throw new FileSystemCorruptedException("EROFS DEFLATE compressed cluster data is invalid.", exception);
        }
    }

    private static int InflateDeflateRaw(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength)
    {
        var inflater = new Inflater(noHeader: true);
        inflater.SetInput(source, sourceIndex, sourceLength);

        var written = 0;
        while (!inflater.IsFinished)
        {
            if (written >= targetLength)
            {

                inflater.Inflate(target, targetIndex, 0);
                if (inflater.IsFinished)
                {
                    break;
                }

                if (inflater.IsNeedingInput)
                {
                    throw new FileSystemCorruptedException("DEFLATE compressed data is truncated.");
                }

                throw new FileSystemCorruptedException("DEFLATE decompressed output exceeds the destination buffer.");
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
                throw new FileSystemCorruptedException("DEFLATE compressed data is truncated.");
            }

            throw new FileSystemCorruptedException("DEFLATE decompressor made no progress (corrupt stream).");
        }

        return written;
    }

    private static void ValidateDeflateWindowBits(int windowBits)
    {
        if (windowBits != 0 && windowBits is < 8 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(windowBits), "DEFLATE windows must be between 8 and 15 bits, or zero for the default.");
        }
    }

    /// <summary>
    /// ZSTD compression (in-box Zstandard codec) producing a standard zstd frame; when windowLog &gt; 0 the frame window log is set accordingly.
    /// <para>ZSTD 压缩（内置 Zstandard 编解码器），输出标准 zstd 帧；windowLog &gt; 0 时写入帧的窗口对数。</para>
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

        var quality = Math.Clamp(compressionLevel, -5, 22);
        if (!ZstandardEncoder.TryCompress(source.AsSpan(sourceIndex, sourceLength),
                target.AsSpan(targetIndex, targetLength), out var compressed, quality, windowLog))
        {
            throw new InvalidOperationException(
                "ZSTD compressed output exceeds the destination buffer, or the compression parameters are invalid.");
        }

        return compressed;
    }

    /// <summary>
    /// ZSTD decompression (in-box Zstandard codec); when windowLog &gt; 0 the maximum window log is constrained accordingly.
    /// <para>ZSTD 解压（内置 Zstandard 编解码器）；windowLog &gt; 0 时约束最大窗口对数。</para>
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

        var options = new ZstandardDecompressionOptions();
        if (windowLog > 0)
        {
            options.MaxWindowLog2 = windowLog;
        }

        byte[] plaintext;
        try
        {
            using var frameStream = new MemoryStream(source, sourceIndex, sourceLength, writable: false);
            using var decoder = new ZstandardStream(frameStream, options, leaveOpen: true);
            using var buffer = new MemoryStream();
            decoder.CopyTo(buffer);
            plaintext = buffer.ToArray();
        }
        catch (Exception ex) when (ex is not FileSystemCorruptedException)
        {
            throw new FileSystemCorruptedException(
                "ZSTD decompression failed: the compressed data is corrupt or the destination buffer is too small.", ex);
        }

        if (plaintext.Length > targetLength)
        {
            throw new FileSystemCorruptedException(
                "ZSTD decompression failed: the compressed data is corrupt or the destination buffer is too small.");
        }

        Array.Copy(plaintext, 0, target, targetIndex, plaintext.Length);
        return plaintext.Length;
    }

    /// <summary>
    /// Conservative LZMA alone-stream bound (13-byte header + input + 25% + 128, per liblzma's lzma_stream_buffer_bound).
    /// <para>.lzma 流的保守压缩上界（13 字节头 + 输入 + 25% + 128，参考 liblzma lzma_stream_buffer_bound）。</para>
    /// </summary>
    private static int LzmaMaximumOutputSize(int length) => LzmaHeaderSize + length + (length >> 2) + 128;

    /// <summary>
    /// Conservative raw-DEFLATE bound (the zlib compressBound formula).
    /// <para>原始 DEFLATE 的保守压缩上界（zlib compressBound 公式）。</para>
    /// </summary>
    private static int DeflateMaximumOutputSize(int length) => length + (length >> 12) + (length >> 14) + (length >> 25) + 13;

    /// <summary>
    /// Conservative ZSTD bound (the ZSTD_compressBound formula).
    /// <para>ZSTD 的保守压缩上界（ZSTD_compressBound 公式）。</para>
    /// </summary>
    private static int ZstdMaximumOutputSize(int length)
    {
        var bound = length + (length >> 8);
        if (length < (128 << 10))
        {
            bound += (128 << 10) - length;
        }

        return bound;
    }

    /// <summary>
    /// Routes the buffer-range bounds check through the shared kernel validator and translates the failure into
    /// <see cref="ArgumentOutOfRangeException"/> so the codec entry points keep their documented parameter contract.
    /// <para>将缓冲区范围校验交由共享内核校验器完成，并把失败转换为 <see cref="ArgumentOutOfRangeException"/>，
    /// 使编解码入口保持其文档化的参数契约。</para>
    /// </summary>
    private static void ValidateRange(byte[] buffer, int offset, int length, string paramName)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        try
        {
            Core.FileExtraction.ValidateRange(offset, length, buffer.Length, paramName);
        }
        catch (InvalidDataException)
        {
            throw new ArgumentOutOfRangeException(paramName,
                $"{paramName} range [{offset}..{offset + length}) exceeds the buffer length {buffer.Length}.");
        }
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}

internal readonly struct PclusterIndex
{
    public const int Size = 8;

    public uint BlockAddress { get; }

    public ushort CompressedLength { get; }

    public byte Flags { get; }

    public bool IsPlain => (Flags & ErofsCompression.FlagPlain) != 0;

    /// <summary>
    /// Creates an index entry.
    /// <para>创建索引条目。</para>
    /// </summary>
    public PclusterIndex(uint blockAddress, ushort compressedLength, byte flags)
    {
        BlockAddress = blockAddress;
        CompressedLength = compressedLength;
        Flags = flags;
    }

    /// <summary>
    /// Parses an index entry from bytes.
    /// <para>从字节解析索引条目。</para>
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
    /// Writes the index entry into a buffer of at least 8 bytes.
    /// <para>将索引条目写入缓冲区（长度至少 8 字节）。</para>
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