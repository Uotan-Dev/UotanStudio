using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Lz4;
using UotanStudio.Compress.Codecs.Lzma;
using UotanStudio.Compress.Codecs.Zstd;
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
    /// <param name="size">Logical file size in bytes; a size of <c>0</c> needs no pcluster at all.
    /// <para>文件逻辑字节数；为 <c>0</c> 时不需要任何 pcluster。</para></param>
    /// <param name="clusterSize">Logical cluster width in bytes that the size is divided by.
    /// <para>用于切分该大小的逻辑簇宽度（字节）。</para></param>
    /// <returns>The number of pclusters covering <paramref name="size"/> bytes, rounded up.
    /// <para>向上取整后覆盖 <paramref name="size"/> 字节所需的 pcluster 数量。</para></returns>
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

    /// <summary>Compresses one pcluster with the managed LZ4 block encoder, returning the compressed byte count or zero when the stream does not fit the destination.
    /// <para>用托管 LZ4 块编码器压缩单个 pcluster，返回压缩字节数；输出放不进目标缓冲区时返回 0。</para></summary>
    /// <param name="source">Buffer holding the uncompressed pcluster bytes.
    /// <para>存放未压缩 pcluster 字节的缓冲区。</para></param>
    /// <param name="sourceIndex">Start index of the input range inside <paramref name="source"/>.
    /// <para><paramref name="source"/> 内输入区间的起始索引。</para></param>
    /// <param name="sourceLength">Input bytes to encode; <c>0</c> writes nothing and returns <c>0</c>.
    /// <para>参与编码的字节数；为 <c>0</c> 时不写入任何内容并返回 <c>0</c>。</para></param>
    /// <param name="target">Buffer receiving the encoded LZ4 block.
    /// <para>接收 LZ4 编码结果的缓冲区。</para></param>
    /// <param name="targetIndex">Start index of the destination range inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 内目标区间的起始索引。</para></param>
    /// <param name="targetLength">Capacity reserved for the block; <c>0</c> skips encoding and returns <c>0</c>.
    /// <para>为压缩块预留的容量；为 <c>0</c> 时跳过编码并返回 <c>0</c>。</para></param>
    /// <param name="compressionLevel">EROFS LZ4 level applied through the fast path as <c>10 - compressionLevel</c> clamped to 1-10, so a larger value selects a faster, weaker encode.
    /// <para>EROFS 的 LZ4 等级，经快速路径按 <c>10 - compressionLevel</c>（截断到 1-10）生效，取值越大编码越快、压缩率越低。</para></param>
    /// <returns>Bytes written at <paramref name="targetIndex"/>, or <c>0</c> when the block does not fit the destination or the encoder consumed less than <paramref name="sourceLength"/> input bytes.
    /// <para>写入 <paramref name="targetIndex"/> 处的字节数；块放不进目标区间或编码器消耗字节数少于 <paramref name="sourceLength"/> 时返回 <c>0</c>。</para></returns>
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

    /// <summary>Decompresses a single pcluster with LZ4, the pre-5.13 codec, returning the decompressed byte count.
    /// <para>以 pre-5.13 编解码器 LZ4 解压单个 pcluster，返回解压字节数。</para></summary>
    /// <param name="source">Buffer holding the encoded LZ4 block.<para>存放 LZ4 编码块的缓冲区。</para></param>
    /// <param name="sourceIndex">Start index of the compressed range inside <paramref name="source"/>.
    /// <para><paramref name="source"/> 内压缩区间的起始索引。</para></param>
    /// <param name="sourceLength">Compressed bytes offered to the decoder.
    /// <para>交给解码器的压缩字节数。</para></param>
    /// <param name="target">Buffer receiving the recovered pcluster bytes.
    /// <para>接收还原 pcluster 字节的缓冲区。</para></param>
    /// <param name="targetIndex">Start index of the destination range inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 内目标区间的起始索引。</para></param>
    /// <param name="targetLength">Capacity reserved for the decoded block.
    /// <para>为解码结果预留的目标容量（字节）。</para></param>
    /// <returns>Bytes written at <paramref name="targetIndex"/>, or <c>0</c> when the block is malformed or the destination range is smaller than the decoded output.
    /// <para>写入 <paramref name="targetIndex"/> 处的字节数；块格式非法或目标区间小于解码输出时返回 <c>0</c>。</para></returns>
    public static int Decompress(byte[] source, int sourceIndex, int sourceLength, byte[] target, int targetIndex, int targetLength)
        => DecompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength);

    /// <summary>
    /// Computes the maximum LZ4 output size for a given input length.
    /// <para>计算 LZ4 压缩所需的最大输出字节数。</para>
    /// </summary>
    /// <param name="length">Number of uncompressed input bytes. <para>未压缩输入的字节数。</para></param>
    /// <returns>The LZ4 output bound in bytes for <paramref name="length"/>.
    /// <para>LZ4 针对 <paramref name="length"/> 可能输出的字节数上界。</para></returns>
    public static int MaximumOutputSize(int length) => Lz4BlockCodec.MaximumOutputSize(length);

    /// <summary>
    /// Computes a conservative maximum output size bound for the given algorithm.
    /// <para>计算指定算法压缩所需的安全最大输出字节数（保守上界）。</para>
    /// </summary>
    /// <param name="algorithm">Codec branch whose bound is requested. <para>需要求上界的编解码分支。</para></param>
    /// <param name="length">Number of uncompressed input bytes. <para>未压缩输入的字节数。</para></param>
    /// <returns>The number of destination bytes guaranteed to hold the compressed form of
    /// <paramref name="length"/> input bytes.
    /// <para>足以容纳 <paramref name="length"/> 字节输入压缩结果的目标字节数。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, or
    /// <paramref name="algorithm"/> is not a known EROFS codec.
    /// <para><paramref name="length"/> 为负数，或 <paramref name="algorithm"/> 不是已知的 EROFS 算法。</para></exception>
    public static int MaximumOutputSize(ErofsAlgorithm algorithm, int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return algorithm switch
        {
            ErofsAlgorithm.Lz4 => Lz4BlockCodec.MaximumOutputSize(length),
            ErofsAlgorithm.Lzma => LzmaMaximumOutputSize(length),
            ErofsAlgorithm.Deflate => DeflateMaximumOutputSize(length),
            ErofsAlgorithm.Zstd => ZstdMaximumOutputSize(length),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm."),
        };
    }

    /// <summary>Algorithm-dispatched compression (dictSize/windowBits use the codec defaults).
    /// <para>按算法分派压缩（dictSize/windowBits 使用算法默认值）。</para></summary>
    /// <param name="algorithm">EROFS codec branch selected for this call.
    /// <para>本次调用选择的 EROFS 编解码分支。</para></param>
    /// <param name="source">Buffer holding the uncompressed input.<para>存放未压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the input region starts.
    /// <para><paramref name="source"/> 中输入区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of input bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的输入字节数。</para></param>
    /// <param name="target">Buffer the compressed bytes are written into.
    /// <para>写入压缩字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 中为输出预留的字节数。</para></param>
    /// <param name="compressionLevel">Codec level: steers the LZ4 search (LZ4HC values above 9 are clamped to 9), selects the LZMA algorithm and num-fast-bytes, is clamped to 0..9 for DEFLATE and -5..22 for ZSTD.
    /// <para>编解码级别：影响 LZ4 搜索（LZ4HC 超过 9 的取值截断到 9）、决定 LZMA 算法与 num-fast-bytes，DEFLATE 限制到 0..9，ZSTD 限制到 -5..22。</para></param>
    /// <returns>The number of compressed bytes written to <paramref name="target"/>; the LZ4 and DEFLATE branches return zero when the encoded stream does not fit the destination.
    /// <para>写入 <paramref name="target"/> 的压缩字节数；LZ4 与 DEFLATE 分支在编码流放不下目标时返回 0。</para></returns>
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

    /// <summary>Algorithm-dispatched compression, returning the compressed byte count.
    /// <para>按算法分派压缩，返回压缩字节数。</para></summary>
    /// <param name="algorithm">EROFS codec branch selected for this call.
    /// <para>本次调用选择的 EROFS 编解码分支。</para></param>
    /// <param name="source">Buffer holding the uncompressed input.<para>存放未压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the input region starts.
    /// <para><paramref name="source"/> 中输入区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of input bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的输入字节数。</para></param>
    /// <param name="target">Buffer the compressed bytes are written into.
    /// <para>写入压缩字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 中为输出预留的字节数。</para></param>
    /// <param name="compressionLevel">Codec level: steers the LZ4 search (LZ4HC values above 9 are clamped to 9), selects the LZMA algorithm and num-fast-bytes, is clamped to 0..9 for DEFLATE and -5..22 for ZSTD.
    /// <para>编解码级别：影响 LZ4 搜索（LZ4HC 超过 9 的取值截断到 9）、决定 LZMA 算法与 num-fast-bytes，DEFLATE 限制到 0..9，ZSTD 限制到 -5..22。</para></param>
    /// <param name="dictSize">MicroLZMA dictionary size in bytes; <c>0</c> derives one from the input length, other codecs ignore it.
    /// <para>MicroLZMA 字典字节数；<c>0</c> 按输入长度推导，其余算法忽略。</para></param>
    /// <param name="windowBits">DEFLATE window bits (8..15, <c>0</c> for the default) and ZSTD window log; ignored by LZ4 and LZMA.
    /// <para>DEFLATE 窗口位数（8..15，<c>0</c> 为默认）与 ZSTD 窗口对数；LZ4 与 LZMA 忽略。</para></param>
    /// <returns>The number of compressed bytes written to <paramref name="target"/>; the LZ4 and DEFLATE branches return zero when the encoded stream does not fit the destination.
    /// <para>写入 <paramref name="target"/> 的压缩字节数；LZ4 与 DEFLATE 分支在编码流放不下目标时返回 0。</para></returns>
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

    /// <summary>Algorithm-dispatched compression with an explicit LZ4 encoder identity.
    /// <para>按算法分派压缩，并显式指定 LZ4 编码器身份。</para></summary>
    /// <param name="algorithm">EROFS codec branch selected for this call.
    /// <para>本次调用选择的 EROFS 编解码分支。</para></param>
    /// <param name="source">Buffer holding the uncompressed input.<para>存放未压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the input region starts.
    /// <para><paramref name="source"/> 中输入区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of input bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的输入字节数。</para></param>
    /// <param name="target">Buffer the compressed bytes are written into.
    /// <para>写入压缩字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 中为输出预留的字节数。</para></param>
    /// <param name="compressionLevel">Codec level: steers the LZ4 search (LZ4HC values above 9 are clamped to 9), selects LZMA algorithm and num-fast-bytes, is clamped to 0..9 for DEFLATE and -5..22 for ZSTD.
    /// <para>编解码级别：影响 LZ4 搜索（LZ4HC 超过 9 的取值截断到 9）、决定 LZMA 算法与 num-fast-bytes，DEFLATE 限制到 0..9，ZSTD 限制到 -5..22。</para></param>
    /// <param name="highCompression">Uses the LZ4HC search for LZ4 payloads instead of the fast single-hash matcher; ignored by the other codecs.
    /// <para>对 LZ4 数据使用 LZ4HC 搜索而非快速单哈希匹配器；其余算法忽略。</para></param>
    /// <param name="dictSize">MicroLZMA dictionary size in bytes; <c>0</c> derives one from the input length, other codecs ignore it.
    /// <para>MicroLZMA 字典字节数；<c>0</c> 按输入长度推导，其余算法忽略。</para></param>
    /// <param name="windowBits">DEFLATE window bits (8..15, <c>0</c> for the default) and ZSTD window log; ignored by LZ4 and LZMA.
    /// <para>DEFLATE 窗口位数（8..15，<c>0</c> 为默认）与 ZSTD 窗口对数；LZ4 与 LZMA 忽略。</para></param>
    /// <returns>The number of compressed bytes written to <paramref name="target"/>; the LZ4 and DEFLATE branches return zero when the encoded stream does not fit the destination.
    /// <para>写入 <paramref name="target"/> 的压缩字节数；LZ4 与 DEFLATE 分支在编码流放不下目标时返回 0。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="target"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="target"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A region exceeds its buffer, <paramref name="algorithm"/> is unknown, or <paramref name="dictSize"/>/<paramref name="windowBits"/> fall outside the codec limits.
    /// <para>区域超出缓冲区、<paramref name="algorithm"/> 未知，或 <paramref name="dictSize"/>/<paramref name="windowBits"/> 超出编解码上限。</para></exception>
    /// <exception cref="InvalidOperationException">ZSTD output exceeds the destination buffer, or the LZMA encoder misbehaves.
    /// <para>ZSTD 输出超出目标缓冲区，或 LZMA 编码器行为异常。</para></exception>
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
            ErofsAlgorithm.Lzma => CompressLzma(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, dictSize, context: null),
            ErofsAlgorithm.Deflate => CompressDeflate(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, windowBits),
            ErofsAlgorithm.Zstd => CompressZstd(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel, windowBits, reusableCompressor: null),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm."),
        };
    }

    /// <summary>Compresses one pcluster with the codec parameters carried by the options, including the LZ4 encoder identity selected by <see cref="ErofsOptions.Lz4HighCompression"/>.
    /// <para>按选项中的编解码参数压缩单个 pcluster，包含 <see cref="ErofsOptions.Lz4HighCompression"/> 选择的 LZ4 编码器身份。</para></summary>
    /// <param name="options">Image-wide codec settings supplying the algorithm, level, LZ4 encoder identity, dictionary size and window; the level is raised to at least 1, LZ4HC levels above 9 are clamped to 9, and a zero window becomes 20 for ZSTD.
    /// <para>提供算法、等级、LZ4 编码器身份、字典大小与窗口的镜像级编解码设置；等级至少取 1，LZ4HC 超过 9 的等级截断到 9，ZSTD 在窗口为 0 时使用 20。</para></param>
    /// <param name="source">Buffer holding the uncompressed input.<para>存放未压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the input region starts.
    /// <para><paramref name="source"/> 中输入区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of input bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的输入字节数。</para></param>
    /// <param name="target">Buffer the compressed bytes are written into.
    /// <para>写入压缩字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 中为输出预留的字节数。</para></param>
    /// <returns>The number of compressed bytes written to <paramref name="target"/>; the LZ4 and DEFLATE branches return zero when the encoded stream does not fit the destination.
    /// <para>写入 <paramref name="target"/> 的压缩字节数；LZ4 与 DEFLATE 分支在编码流放不下目标时返回 0。</para></returns>
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

    /// <summary>Compresses one pcluster through a caller-owned codec context, so a planning loop reuses the
    /// retained ZSTD compressors, one MicroLZMA encoder and its encoding buffer across all of its probes
    /// instead of constructing them per probe. The emitted bytes are identical to the context-free overload:
    /// every backend receives its full parameter set before each encode, which the codec-state pool
    /// guarantees by keying each retained ZSTD compressor on its level and window log, by re-applying the
    /// LZMA coder properties whenever the level and dictionary size change, and by resetting the encoding
    /// buffer before every use.
    /// <para>经调用方持有的编解码器上下文压缩单个 pcluster，使规划循环在全部探测之间复用保留的 ZSTD 压缩器、一个
    /// MicroLZMA 编码器及其编码缓冲，而不是每次探测都构造。输出字节与不带上下文的重载逐字节相同：每个后端在每次
    /// 编码前都会收到完整参数集，编解码状态池以级别与窗口对数作为每个保留 ZSTD 压缩器的键，在级别与字典大小变化时
    /// 重新应用 LZMA 编码属性，并在每次使用前重置编码缓冲。</para>
    /// </summary>
    /// <param name="options">Image-wide codec settings; the level, dictionary size and window are resolved
    /// exactly as the context-free overload resolves them. <para>镜像级编解码设置；级别、字典大小与窗口的解析方式
    /// 与不带上下文的重载完全相同。</para></param>
    /// <param name="context">Encoder state borrowed for this payload, or <see langword="null"/> to allocate
    /// private state for this call. <para>为本负载借用的编码器状态；为 <see langword="null"/> 时仅为本调用分配
    /// 私有状态。</para></param>
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
    /// <returns>The number of compressed bytes written to <paramref name="target"/>; the LZ4 and DEFLATE
    /// branches return zero when the encoded stream does not fit the destination. <para>写入
    /// <paramref name="target"/> 的压缩字节数；LZ4 与 DEFLATE 分支在编码流放不下目标时返回 0。</para></returns>
    internal static int AlgorithmCompress(
        ErofsOptions options,
        ErofsEncoderContext? context,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength)
    {
        ValidateRange(source, sourceIndex, sourceLength, nameof(source));
        ValidateRange(target, targetIndex, targetLength, nameof(target));
        var level = Math.Max(1, options.CompressionLevel);
        var windowBits = options.WindowBits == 0 && options.Algorithm == ErofsCompressionAlgorithm.Zstd
            ? 20
            : options.WindowBits;
        return options.Algorithm switch
        {
            ErofsCompressionAlgorithm.Lz4 => CompressLz4(source, sourceIndex, sourceLength, target, targetIndex,
                targetLength, level, options.Lz4HighCompression, context?.RentLz4(options.Lz4HighCompression)),
            ErofsCompressionAlgorithm.Lzma => CompressLzma(source, sourceIndex, sourceLength, target, targetIndex,
                targetLength, level, options.DictionarySize, context),
            ErofsCompressionAlgorithm.Deflate => CompressDeflate(source, sourceIndex, sourceLength, target,
                targetIndex, targetLength, level, windowBits),
            ErofsCompressionAlgorithm.Zstd => CompressZstd(source, sourceIndex, sourceLength, target, targetIndex,
                targetLength, level, windowBits,
                context?.RentZstd(Math.Clamp(level, -5, 22), windowBits)),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Algorithm,
                "Unknown EROFS compression algorithm."),
        };
    }

    /// <summary>Algorithm-dispatched decompression (dictSize/windowBits use the codec defaults).
    /// <para>按算法分派解压（dictSize/windowBits 使用默认值）。</para></summary>
    /// <param name="algorithm">EROFS codec branch used to decode.
    /// <para>用于解码的 EROFS 编解码分支。</para></param>
    /// <param name="source">Buffer holding the compressed input.<para>存放压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the compressed region starts.
    /// <para><paramref name="source"/> 中压缩区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of compressed bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的压缩字节数。</para></param>
    /// <param name="target">Buffer the decompressed bytes are written into.
    /// <para>写入解压字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>, which for LZMA and DEFLATE is also the exact expected decompressed size.
    /// <para><paramref name="target"/> 中为输出预留的字节数；对 LZMA 与 DEFLATE 同时是期望的解压长度。</para></param>
    /// <returns>The number of decompressed bytes written to <paramref name="target"/>.
    /// <para>写入 <paramref name="target"/> 的解压字节数。</para></returns>
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

    /// <summary>Algorithm-dispatched decompression, returning the decompressed byte count.
    /// <para>按算法分派解压，返回解压字节数。</para></summary>
    /// <param name="algorithm">EROFS codec branch used to decode.
    /// <para>用于解码的 EROFS 编解码分支。</para></param>
    /// <param name="source">Buffer holding the compressed input.<para>存放压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the compressed region starts.
    /// <para><paramref name="source"/> 中压缩区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of compressed bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的压缩字节数。</para></param>
    /// <param name="target">Buffer the decompressed bytes are written into.
    /// <para>写入解压字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>, which for LZMA and DEFLATE is also the exact expected decompressed size.
    /// <para><paramref name="target"/> 中为输出预留的字节数；对 LZMA 与 DEFLATE 同时是期望的解压长度。</para></param>
    /// <param name="dictSize">MicroLZMA dictionary size in bytes; <c>0</c> uses 8 MiB and values below 4096 are raised to 4096, after which the window is capped to the frame's own decoded length rounded up to a power of two, other codecs ignore it.
    /// <para>MicroLZMA 字典字节数；<c>0</c> 使用 8 MiB，小于 4096 的值提升到 4096；随后窗口被限制到该帧自身解码长度向上取整的 2 的幂，其余算法忽略。</para></param>
    /// <param name="windowBits">Validated DEFLATE window bits (8..15 or <c>0</c>) and the ZSTD maximum window log; ignored by LZ4 and LZMA.
    /// <para>经校验的 DEFLATE 窗口位数（8..15 或 <c>0</c>）与 ZSTD 最大窗口对数；LZ4 与 LZMA 忽略。</para></param>
    /// <param name="context">Decoder state held by the reading context, so consecutive pclusters reuse one ZSTD
    /// decompressor and one MicroLZMA decoder instead of rebuilding their workspace; <see langword="null"/>
    /// allocates private state for this call. <para>读取上下文持有的解码器状态，使相邻 pcluster 复用一个 ZSTD
    /// 解压器与一个 MicroLZMA 解码器而不必重建工作区；为 <see langword="null"/> 时仅为本调用分配私有状态。</para></param>
    /// <returns>The number of decompressed bytes written to <paramref name="target"/>.
    /// <para>写入 <paramref name="target"/> 的解压字节数。</para></returns>
    public static int AlgorithmDecompress(
        ErofsAlgorithm algorithm,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int dictSize,
        int windowBits,
        ErofsDecoderContext? context = null)
    {
        ValidateRange(source, sourceIndex, sourceLength, nameof(source));
        ValidateRange(target, targetIndex, targetLength, nameof(target));

        return algorithm switch
        {
            ErofsAlgorithm.Lz4 => DecompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength),
            ErofsAlgorithm.Lzma => DecompressLzma(source, sourceIndex, sourceLength, target, targetIndex, targetLength, dictSize, context),
            ErofsAlgorithm.Deflate => DecompressDeflate(source, sourceIndex, sourceLength, target, targetIndex, targetLength, windowBits),
            ErofsAlgorithm.Zstd => DecompressZstd(source, sourceIndex, sourceLength, target, targetIndex, targetLength, windowBits, context),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm."),
        };
    }

    /// <summary>
    /// Encodes one LZ4 block through a caller-owned encoder context, so a planning loop reuses one native
    /// allocation across its probes instead of allocating a fresh context per probe. The frame bytes are
    /// identical to the context-free overload, which the shared encoder guarantees by clearing the context
    /// before every use.
    /// <para>经调用方持有的编码器上下文输出一个 LZ4 块，使规划循环在各次探测之间复用同一块原生分配，而不是
    /// 每次探测都新建上下文。帧字节与不带上下文的重载逐字节相同，这一致性由共享编码器在每次使用前清零上下文
    /// 保证。</para>
    /// </summary>
    /// <param name="options">Codec settings of the node being planned, supplying the level and LZ4 encoder
    /// identity; an LZ4HC level above 9 is clamped to 9. <para>正在规划的节点的编解码设置，提供级别与 LZ4
    /// 编码器身份；LZ4HC 超过 9 的等级截断到 9。</para></param>
    /// <param name="context">Context borrowed by the calling worker from
    /// <see cref="ErofsLz4StatePool"/>; <see langword="null"/> allocates a private one for this call.
    /// <para>调用工作者从 <see cref="ErofsLz4StatePool"/> 借用的上下文；为 <see langword="null"/> 时仅为本调用
    /// 分配私有上下文。</para></param>
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
    /// <returns>The number of compressed bytes written to <paramref name="target"/>, or <c>0</c> when the block
    /// does not fit the destination. <para>写入 <paramref name="target"/> 的压缩字节数；块放不下目标时为
    /// <c>0</c>。</para></returns>
    internal static int AlgorithmCompressLz4(
        ErofsOptions options,
        ErofsLz4Context? context,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength)
    {
        ValidateRange(source, sourceIndex, sourceLength, nameof(source));
        ValidateRange(target, targetIndex, targetLength, nameof(target));
        return CompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength,
            Math.Max(1, options.CompressionLevel),
            options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4HighCompression,
            context);
    }

    /// <summary>
    /// Encodes one LZ4 block into a destination bounded by the pcluster frame limit and reports the input
    /// bytes the encoder consumed. A frame that consumes only part of the input is a success: it decodes to
    /// exactly those bytes, which is the pcluster span the destination-filling encoder fitted, and the caller
    /// emits that span as the index's source length.
    /// <para>在 pcluster 帧上限所约束的目标区间内编码一个 LZ4 块，并报告编码器消费的输入字节数。只消费部分输入的帧
    /// 属于成功结果：它解码出的正是这些字节，也就是填满目标的编码器装入的 pcluster 跨度，调用方以该跨度作为索引的
    /// 源长度。</para>
    /// </summary>
    /// <param name="options">Codec settings supplying the level and the LZ4 encoder identity.
    /// <para>提供等级与 LZ4 编码器身份的编解码设置。</para></param>
    /// <param name="context">Encoder state borrowed by the calling worker, or <see langword="null"/> to
    /// allocate private state for this call. <para>调用工作者借用的编码器状态；为 <see langword="null"/> 时仅为
    /// 本调用分配私有状态。</para></param>
    /// <param name="source">Buffer holding the uncompressed input. <para>存放未压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the input region starts.
    /// <para><paramref name="source"/> 中输入区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of input bytes offered to the encoder.
    /// <para>提供给编码器的输入字节数。</para></param>
    /// <param name="target">Buffer the compressed bytes are written into.
    /// <para>写入压缩字节的缓冲区。</para></param>
    /// <param name="targetLength">Frame limit passed as the destination capacity; the encoder stops at the
    /// last input byte that still fits. <para>作为目标容量传入的帧上限；编码器停在仍能装入的最后一个输入字节处。</para></param>
    /// <param name="consumed">Input bytes the emitted frame decodes to, or <c>0</c> when nothing was encoded.
    /// <para>发出的帧所解码出的输入字节数；未编码任何内容时为 <c>0</c>。</para></param>
    /// <returns>The encoded byte count, or <c>0</c> when the input or destination is empty.
    /// <para>编码字节数；输入或目标为空时为 <c>0</c>。</para></returns>
    internal static int CompressLz4Fill(
        ErofsOptions options,
        ErofsEncoderContext? context,
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetLength,
        out int consumed)
    {
        consumed = 0;
        ValidateRange(source, sourceIndex, sourceLength, nameof(source));
        ValidateRange(target, 0, targetLength, nameof(target));
        if (sourceLength == 0 || targetLength == 0)
        {
            return 0;
        }

        var highCompression = options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4HighCompression;
        ResolveLz4Encoder(highCompression, Math.Max(1, options.CompressionLevel), out var algorithm, out var level);
        var sourceSpan = source.AsSpan(sourceIndex, sourceLength);
        var targetSpan = target.AsSpan(0, targetLength);
        var lz4Context = context?.RentLz4(highCompression);
        var encoded = lz4Context is null
            ? Lz4DestinationCodec.Encode(sourceSpan, targetSpan, algorithm, level, out consumed)
            : lz4Context.Encode(sourceSpan, targetSpan, algorithm, level, out consumed);
        if (encoded <= 0)
        {
            consumed = 0;
            return 0;
        }

        return encoded;
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
        => CompressLz4(source, sourceIndex, sourceLength, target, targetIndex, targetLength, compressionLevel,
            highCompression, context: null);

    private static int CompressLz4(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        bool highCompression,
        ErofsLz4Context? context)
    {
        if (sourceLength == 0 || targetLength == 0)
        {
            return 0;
        }

        ResolveLz4Encoder(highCompression, compressionLevel, out var algorithm, out var level);
        var sourceSpan = source.AsSpan(sourceIndex, sourceLength);
        var targetSpan = target.AsSpan(targetIndex, targetLength);
        int encoded;
        int consumed;
        if (context is null)
        {
            encoded = Lz4DestinationCodec.Encode(sourceSpan, targetSpan, algorithm, level, out consumed);
        }
        else
        {
            encoded = context.Encode(sourceSpan, targetSpan, algorithm, level, out consumed);
        }

        return consumed == sourceLength ? encoded : 0;
    }

    /// <summary>
    /// Resolves the destination-size encoder identity and level the EROFS LZ4 path applies, including the
    /// cap that keeps the LZ4HC optimal parser off the format path.
    /// <para>解析 EROFS LZ4 路径实际使用的限定目标大小编码器身份与等级，包括把 LZ4HC 最优解析器挡在格式路径外的
    /// 等级上限。</para>
    /// </summary>
    /// <param name="highCompression"><see langword="true"/> for the LZ4HC encoder.
    /// <para>为 <see langword="true"/> 时使用 LZ4HC 编码器。</para></param>
    /// <param name="compressionLevel">Validated level; the fast path maps it as <c>10 - level</c>.
    /// <para>经校验的等级；快速路径按 <c>10 - level</c> 映射。</para></param>
    /// <param name="algorithm"><c>1</c> for the fast path, <c>2</c> for LZ4HC.
    /// <para><c>1</c> 为快速路径，<c>2</c> 为 LZ4HC。</para></param>
    /// <param name="level">Level handed to the selected encoder.
    /// <para>交给所选编码器的等级。</para></param>
    private static void ResolveLz4Encoder(bool highCompression, int compressionLevel, out int algorithm,
        out int level)
    {
        algorithm = highCompression ? 2 : 1;
        level = highCompression
            ? Math.Min(compressionLevel, ErofsCompressionLevel.Lz4HighCompressionBest)
            : Math.Clamp(10 - compressionLevel, 1, 10);
    }

    private static int DecompressLz4(byte[] source, int sourceIndex, int sourceLength, byte[] target, int targetIndex, int targetLength)
        => Lz4BlockCodec.Decode(source.AsSpan(sourceIndex, sourceLength), target.AsSpan(targetIndex, targetLength));

    private static readonly int[] LzmaNumFastBytesByLevel = [32, 64, 128, 128, 256, 384, 512, 768, 1024];

    private const int LzmaPosStateBits = 2;
    private const int LzmaLitPosBits = 0;
    private const int LzmaLitContextBits = 3;
    private const byte MicroLzmaPropertyByte =
        unchecked((byte)~((LzmaPosStateBits * 5 + LzmaLitPosBits) * 9 + LzmaLitContextBits));

    /// <summary>
    /// Compresses data into an EROFS MicroLZMA stream without a size header or EOS marker. A caller-owned
    /// <paramref name="context"/> supplies the retained encoder and intermediate buffer, whose coder
    /// properties are re-applied only when the level or dictionary size changes; without a context this
    /// call allocates both privately. The emitted bytes never depend on a previous frame, because every
    /// encode starts from reset stream state and from a buffer whose length is reset to zero.
    /// <para>将数据压缩为不含大小头和 EOS 标记的 EROFS MicroLZMA 流。调用方持有的 <paramref name="context"/>
    /// 提供保留的编码器与中间缓冲，仅当等级或字典大小变化时才重新应用编码属性；不带上下文时本调用自行分配两者。
    /// 输出字节从不依赖上一帧，因为每次编码都从重置的流状态和长度已清零的缓冲开始。</para>
    /// </summary>
    /// <param name="source">Buffer holding the uncompressed input.<para>存放未压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the input region starts.
    /// <para><paramref name="source"/> 中输入区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of input bytes read from <paramref name="sourceIndex"/>; <c>0</c>
    /// writes nothing and returns <c>0</c>. <para>自 <paramref name="sourceIndex"/> 起读取的输入字节数；为
    /// <c>0</c> 时不写入任何内容并返回 <c>0</c>。</para></param>
    /// <param name="target">Buffer the MicroLZMA frame is written into.
    /// <para>写入 MicroLZMA 帧的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the frame starts.
    /// <para><paramref name="target"/> 中帧的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the frame inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 中为帧预留的字节数。</para></param>
    /// <param name="compressionLevel">Level selecting the LZMA algorithm and fast-byte count.
    /// <para>选择 LZMA 算法与 fast-bytes 数量的等级。</para></param>
    /// <param name="dictSize">Requested dictionary size in bytes; <c>0</c> derives one capped to the frame
    /// length. <para>请求的字典字节数；<c>0</c> 按帧长推导并封顶。</para></param>
    /// <param name="context">Encoder state borrowed from <see cref="ErofsCodecStatePool"/>, or
    /// <see langword="null"/> to allocate private state for this call.
    /// <para>从 <see cref="ErofsCodecStatePool"/> 借用的编码器状态；为 <see langword="null"/> 时仅为本调用分配
    /// 私有状态。</para></param>
    /// <returns>Bytes written at <paramref name="targetIndex"/>.
    /// <para>写入 <paramref name="targetIndex"/> 处的字节数。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The frame does not fit <paramref name="targetLength"/>.
    /// <para>帧放不进 <paramref name="targetLength"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The encoder did not emit its leading size byte.
    /// <para>编码器未输出其前导大小字节。</para></exception>
    private static int CompressLzma(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        int dictSize,
        ErofsEncoderContext? context)
    {
        if (sourceLength == 0)
        {
            return 0;
        }

        var dictionarySize = ResolveLzmaDictionarySize(sourceLength, dictSize);
        var encoder = context is null ? new LzmaEncoder() : context.RentLzma(compressionLevel, dictionarySize);
        if (context is null)
        {
            ConfigureLzmaEncoder(encoder, compressionLevel, dictionarySize);
        }

        using var input = new MemoryStream(source, sourceIndex, sourceLength, writable: false);
        using var output = new BoundedWriteStream(target, targetIndex, targetLength);
        var start = output.Position;

        output.WriteByte(MicroLzmaPropertyByte);
        var raw = context?.LzmaOutput ?? new MemoryStream();
        try
        {
            raw.SetLength(0);
            encoder.Code(input, raw, sourceLength, sourceLength);
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
        finally
        {
            if (context is null)
            {
                raw.Dispose();
            }
            else
            {
                context.TrimLzmaOutput();
            }
        }
    }

    /// <summary>
    /// Applies the EROFS MicroLZMA coder properties (dictionary size, pb/lc/lp, algorithm and fast-byte count)
    /// to a reusable encoder. Applying unchanged values is equivalent to skipping the call, so the codec
    /// state pool only re-applies them when the level or the frame-capped dictionary size actually changes.
    /// <para>把 EROFS MicroLZMA 编码属性（字典大小、pb/lc/lp、算法与 fast-bytes 数量）应用到可复用编码器。
    /// 应用相同取值等价于跳过该调用，因此编解码状态池只在等级或按帧长封顶的字典大小确实变化时重新应用。</para>
    /// </summary>
    /// <param name="encoder">Encoder to configure. <para>待配置的编码器。</para></param>
    /// <param name="compressionLevel">Level selecting the LZMA algorithm and fast-byte count.
    /// <para>选择 LZMA 算法与 fast-bytes 数量的等级。</para></param>
    /// <param name="dictionarySize">Dictionary size in bytes already capped to the frame length.
    /// <para>已按帧长封顶的字典字节数。</para></param>
    internal static void ConfigureLzmaEncoder(LzmaEncoder encoder, int compressionLevel, int dictionarySize)
        => encoder.Configure(
            dictionarySize: dictionarySize,
            posStateBits: LzmaPosStateBits,
            litContextBits: LzmaLitContextBits,
            litPosBits: LzmaLitPosBits,
            algorithm: compressionLevel >= 5 ? 2 : 1,
            numFastBytes: LzmaNumFastBytes(compressionLevel));

    /// <summary>
    /// LZMA decompression accepting the EROFS MicroLZMA stream (a negated property byte followed by raw LZMA
    /// data, without the .lzma 8-byte size header or an EOS marker). The frame is presented to the decoder
    /// through a zero-copy stream that reintroduces the leading zero byte, and the dictionary is tightened to
    /// the frame's own decoded length so the decoder window stays small.
    /// <para>LZMA 解压，接受 erofs 的 MicroLZMA 流（首字节为属性字节取反，随后为原始 LZMA 数据，无 .lzma
    /// 8 字节大小头与 EOS）。帧经零拷贝流（补回前导零字节）交给解码器，字典收紧到该帧自身的解码长度，
    /// 使解码窗口保持较小。</para>
    /// </summary>
    private static int DecompressLzma(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int dictSize,
        ErofsDecoderContext? context)
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
        dictionarySize = TightenLzmaDictionarySize(dictionarySize, targetLength);

        var properties = context?.LzmaProperties ?? new byte[5];
        properties[0] = propertyByte;
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(1), (uint)dictionarySize);

        var decoder = context?.LzmaDecoder ?? new LzmaDecoder();
        decoder.SetProperties(properties);

        using var input = new MicroLzmaInputStream(source, sourceIndex, sourceLength);
        using var output = new BoundedWriteStream(target, targetIndex, targetLength);

        decoder.Code(input, output, sourceLength, targetLength);
        if (output.Position != targetLength)
        {
            throw new FileSystemCorruptedException(
                $"LZMA stream produced {output.Position} bytes, expected {targetLength}.");
        }

        return checked((int)output.Position);
    }

    /// <summary>
    /// Tightens the MicroLZMA dictionary to the distance one independent frame can actually reference: a
    /// match never reaches further back than the frame's own decoded length, so that length rounded up to a
    /// power of two (at least 4096 bytes) yields a window the frame fully fits and keeps the decoder's window
    /// buffer small for small frames. Valid frames decode to exactly the same bytes because no distance can
    /// exceed the bytes the frame has already produced.
    /// <para>把 MicroLZMA 字典收紧到单个独立帧实际可能引用的距离：匹配最远只回溯到该帧自身已解出的长度，
    /// 因此把该长度向上取整到 2 的幂（至少 4096 字节）即可得到完全容纳该帧的窗口，小帧也只需很小的解码窗口
    /// 缓冲。合法帧的解码字节完全相同，因为任何距离都不会超过该帧已经产出的字节数。</para>
    /// </summary>
    private static int TightenLzmaDictionarySize(int dictionarySize, int targetLength)
    {
        if (targetLength <= 0)
        {
            return 1 << 12;
        }

        if (targetLength >= dictionarySize || targetLength > (1 << 30))
        {
            return dictionarySize;
        }

        var required = (int)BitOperations.RoundUpToPowerOf2((uint)targetLength);
        return Math.Min(dictionarySize, Math.Max(required, 1 << 12));
    }

    /// <summary>
    /// Resolves the MicroLZMA encoder dictionary: an explicit size is validated against the EROFS supported
    /// range, while <c>0</c> derives the smallest power of two that still covers the frame (at least 4096
    /// bytes). A dictionary larger than the frame can never be referenced, so capping it to the frame keeps
    /// both the encoder and the decoder workspace as small as the frame allows.
    /// <para>解析 MicroLZMA 编码字典：显式大小按 EROFS 支持区间校验，<c>0</c> 则推导仍能覆盖该帧的最小 2 的幂
    /// （至少 4096 字节）。大于帧的字典永远不可能被引用，因此按帧封顶可使编码器与解码器工作区都尽可能小。</para>
    /// </summary>
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
    /// DEFLATE compression producing a raw DEFLATE stream (no zlib header/footer, RFC 1951, negative-window
    /// semantics). The stream is encoded into a private worst-case buffer and copied to
    /// <paramref name="target"/> only when it fits, so a destination smaller than the encoded stream returns
    /// <c>0</c> (the planner's no-fit signal) instead of failing inside the bounded destination.
    /// <para>DEFLATE 压缩，输出原始 DEFLATE 流（无 zlib 头/尾，RFC 1951，负 windowBits 语义）。编码先写入私有的
    /// 最坏情况缓冲，仅在放得下时复制到 <paramref name="target"/>，因此目标小于编码流时返回 <c>0</c>（规划器的
    /// 放不下信号），而不是在有界目标内部失败。</para>
    /// </summary>
    /// <param name="source">Buffer holding the uncompressed input.<para>存放未压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the input region starts.
    /// <para><paramref name="source"/> 中输入区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of input bytes read from <paramref name="sourceIndex"/>; <c>0</c>
    /// writes nothing and returns <c>0</c>. <para>自 <paramref name="sourceIndex"/> 起读取的输入字节数；为
    /// <c>0</c> 时不写入任何内容并返回 <c>0</c>。</para></param>
    /// <param name="target">Buffer the raw DEFLATE stream is written into.
    /// <para>写入原始 DEFLATE 流的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the stream starts.
    /// <para><paramref name="target"/> 中流的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the stream; a stream larger than this returns
    /// <c>0</c>. <para>为流预留的字节数；流大于该值时返回 <c>0</c>。</para></param>
    /// <param name="compressionLevel">Requested level, clamped to the 0-9 range the encoder accepts.
    /// <para>请求的等级，截断到编码器接受的 0-9 区间。</para></param>
    /// <param name="windowBits">Validated DEFLATE window size; a value in 1-14 also selects Huffman-only
    /// encoding. <para>已校验的 DEFLATE 窗口大小；取值 1-14 时同时选择 Huffman-only 编码。</para></param>
    /// <returns>Bytes written at <paramref name="targetIndex"/>, or <c>0</c> when the stream does not fit or
    /// the input is empty. <para>写入 <paramref name="targetIndex"/> 处的字节数；流放不下或输入为空时为
    /// <c>0</c>。</para></returns>
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
        if (sourceLength == 0 || targetLength == 0)
        {
            return 0;
        }

        var bound = DeflateMaximumOutputSize(sourceLength);
        var buffer = ArrayPool<byte>.Shared.Rent(bound);
        try
        {
            using var raw = new MemoryStream(buffer, 0, buffer.Length, writable: true, publiclyVisible: false);
            using (var deflate = new DeflateStream(raw, DeflateEncoderOptions(compressionLevel, windowBits),
                       leaveOpen: true))
            {
                deflate.Write(source, sourceIndex, sourceLength);
            }

            var produced = checked((int)raw.Position);
            if (produced > bound)
            {
                throw new InvalidOperationException(
                    "DEFLATE output exceeded its documented worst-case bound.");
            }

            if (produced <= 0 || produced > targetLength)
            {
                return 0;
            }

            buffer.AsSpan(0, produced).CopyTo(target.AsSpan(targetIndex, produced));
            return produced;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Builds the raw DEFLATE encoder options: the clamped level plus the Huffman-only strategy used for
    /// ZLIB4C semi-compressed pclusters, which request a reduced window size.
    /// <para>构建原始 DEFLATE 编码器选项：截断后的等级，以及 ZLIB4C 半压缩 pcluster 传入缩小窗口时
    /// 请求的 Huffman-only 策略。</para>
    /// </summary>
    /// <param name="compressionLevel">Requested level, clamped to the 0-9 range the encoder accepts.
    /// <para>请求的等级，截断到编码器接受的 0-9 区间。</para></param>
    /// <param name="windowBits">Validated window size; a value in 1-14 selects Huffman-only encoding.
    /// <para>已校验的窗口大小；取值 1-14 时选择 Huffman-only 编码。</para></param>
    /// <returns>Options producing a raw DEFLATE stream. <para>产生原始 DEFLATE 流的选项。</para></returns>
    private static ZLibCompressionOptions DeflateEncoderOptions(int compressionLevel, int windowBits)
        => new()
        {
            CompressionLevel = Math.Clamp(compressionLevel, 0, 9),
            CompressionStrategy = windowBits is > 0 and < 15
                ? ZLibCompressionStrategy.HuffmanOnly
                : ZLibCompressionStrategy.Default,
        };

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
            using var input = new MemoryStream(source, sourceIndex, sourceLength, writable: false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
            var written = 0;
            while (written < targetLength)
            {
                var read = inflater.Read(target, targetIndex + written, targetLength - written);
                if (read == 0)
                {
                    return written;
                }

                written += read;
            }

            if (inflater.ReadByte() != -1)
            {
                throw new FileSystemCorruptedException("DEFLATE decompressed output exceeds the destination buffer.");
            }

            return written;
        }
        catch (InvalidDataException exception)
        {
            throw new FileSystemCorruptedException("EROFS DEFLATE compressed cluster data is invalid.", exception);
        }
    }

    private static void ValidateDeflateWindowBits(int windowBits)
    {
        if (windowBits != 0 && windowBits is < 8 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(windowBits), "DEFLATE windows must be between 8 and 15 bits, or zero for the default.");
        }
    }

    /// <summary>
    /// ZSTD compression (fully-managed ZstdSharp port) producing a standard zstd frame; when windowLog &gt; 0 the frame window log is set accordingly.
    /// <para>ZSTD 压缩（ZstdSharp 全托管移植），输出标准 zstd 帧；windowLog &gt; 0 时写入帧的窗口对数。</para>
    /// </summary>
    private static int CompressZstd(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int compressionLevel,
        int windowLog,
        ZstdEncoder? reusableCompressor)
    {
        if (sourceLength == 0)
        {
            return 0;
        }

        if (reusableCompressor is null)
        {
            using var compressor = new ZstdEncoder(Math.Clamp(compressionLevel, -5, 22), windowLog: Math.Max(0, windowLog));
            return CompressZstdFrame(compressor, source, sourceIndex, sourceLength, target, targetIndex, targetLength);
        }

        return CompressZstdFrame(reusableCompressor, source, sourceIndex, sourceLength, target, targetIndex, targetLength);
    }

    private static int CompressZstdFrame(ZstdEncoder compressor, byte[] source, int sourceIndex, int sourceLength,
        byte[] target, int targetIndex, int targetLength)
    {
        try
        {
            return compressor.Compress(source.AsSpan(sourceIndex, sourceLength), target.AsSpan(targetIndex, targetLength));
        }
        catch (CompressionException ex)
        {
            throw new InvalidOperationException(
                "ZSTD compressed output exceeds the destination buffer, or the compression parameters are invalid.", ex);
        }
    }

    /// <summary>
    /// ZSTD decompression (fully-managed ZstdSharp port); when windowLog &gt; 0 the maximum window log is constrained accordingly.
    /// <para>ZSTD 解压（ZstdSharp 全托管移植）；windowLog &gt; 0 时约束最大窗口对数。</para>
    /// </summary>
    private static int DecompressZstd(
        byte[] source,
        int sourceIndex,
        int sourceLength,
        byte[] target,
        int targetIndex,
        int targetLength,
        int windowLog,
        ErofsDecoderContext? context)
    {
        if (sourceLength == 0)
        {
            return 0;
        }

        if (context is null)
        {
            using var decompressor = new ZstdDecoder(maximumWindowLog: windowLog);
            return DecompressZstdFrame(decompressor, source, sourceIndex, sourceLength, target, targetIndex, targetLength);
        }

        return DecompressZstdFrame(context.RentZstd(windowLog), source, sourceIndex, sourceLength, target, targetIndex,
            targetLength);
    }

    private static int DecompressZstdFrame(ZstdDecoder decompressor, byte[] source, int sourceIndex,
        int sourceLength, byte[] target, int targetIndex, int targetLength)
    {
        try
        {
            return decompressor.Decompress(source.AsSpan(sourceIndex, sourceLength), target.AsSpan(targetIndex, targetLength));
        }
        catch (CompressionException ex)
        {
            throw new FileSystemCorruptedException(
                "ZSTD decompression failed: the compressed data is corrupt or the destination buffer is too small.", ex);
        }
    }

    /// <summary>
    /// Conservative LZMA alone-stream bound (13-byte header + input + 25% + 128, per liblzma's lzma_stream_buffer_bound).
    /// <para>.lzma 流的保守压缩上界（13 字节头 + 输入 + 25% + 128，参考 liblzma lzma_stream_buffer_bound）。</para>
    /// </summary>
    private static int LzmaMaximumOutputSize(int length) => LzmaHeaderSize + length + (length >> 2) + 128;

    /// <summary>
    /// Raw-DEFLATE worst-case bound: the larger of the stored-block form (one five-byte block header per
    /// at most 65535 input bytes) and zlib's certified one-shot bound, plus a safety margin for a final
    /// partial block and the Huffman-only strategy. The result never exceeds
    /// <see cref="int.MaxValue"/>.
    /// <para>原始 DEFLATE 的最坏情况上界：取「每至多 65535 输入字节一个 5 字节块头」的存储块形式与 zlib
    /// 经认证的一次性上界中的较大者，再加一段安全余量以覆盖末尾不完整块与 Huffman-only 策略；结果不超过
    /// <see cref="int.MaxValue"/>。</para>
    /// </summary>
    /// <param name="length">Number of uncompressed input bytes. <para>未压缩输入的字节数。</para></param>
    /// <returns>Destination bytes guaranteed to hold the encoded raw DEFLATE stream.
    /// <para>保证容纳编码后原始 DEFLATE 流的目标字节数。</para></returns>
    private static int DeflateMaximumOutputSize(int length)
    {
        var storedBlocks = ((long)length + 65534) / 65535;
        var stored = (long)length + 5 * storedBlocks + 16;
        var dynamic = (long)length + (long)(length >> 3) + 64;
        return (int)Math.Min(int.MaxValue, Math.Max(stored, dynamic));
    }

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
    /// Routes the buffer-range bounds check through the shared range validator and translates the failure into
    /// <see cref="ArgumentOutOfRangeException"/> so the codec entry points keep their documented parameter contract.
    /// <para>将缓冲区范围校验交由共享范围校验器完成，并把失败转换为 <see cref="ArgumentOutOfRangeException"/>，
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

    /// <summary>
    /// Read-only stream presenting one EROFS MicroLZMA frame to <see cref="LzmaDecoder"/> without copying it: the
    /// stream yields the synthetic leading zero byte the LZMA range coder expects, then the frame bytes after
    /// the property byte, exactly the sequence a rented copy would expose.
    /// <para>向 <see cref="LzmaDecoder"/> 呈现单个 EROFS MicroLZMA 帧而不复制它的只读流：先产出 LZMA 区间编码器
    /// 期望的前导零字节，随后是属性字节之后的帧字节，与租借缓冲复制出的序列完全一致。</para>
    /// </summary>
    private sealed class MicroLzmaInputStream(byte[] source, int sourceIndex, int length) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int ReadByte()
        {
            if (_position >= length)
            {
                return -1;
            }

            var value = _position == 0 ? 0 : source[sourceIndex + _position];
            _position++;
            return value;
        }

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, length - _position);
            if (count <= 0)
            {
                return 0;
            }

            if (_position == 0)
            {
                buffer[0] = 0;
                if (count > 1)
                {
                    source.AsSpan(sourceIndex + 1, count - 1).CopyTo(buffer[1..]);
                }
            }
            else
            {
                source.AsSpan(sourceIndex + _position, count).CopyTo(buffer);
            }

            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
    /// <param name="blockAddress">Value stored in the four-byte blkaddr field of the index entry.
    /// <para>写入索引条目 4 字节 blkaddr 字段的值。</para></param>
    /// <param name="compressedLength">Value stored in the two-byte len field, holding the recorded size of the
    /// pcluster in bytes up to <c>65535</c>. <para>写入 2 字节 len 字段的值，记录 pcluster 的字节长度，上限为 <c>65535</c>。</para></param>
    /// <param name="flags">Raw one-byte flag field; <see cref="IsPlain"/> reports whether the plain bit
    /// <c>0x01</c> is set. <para>原始 1 字节标志字段；<see cref="IsPlain"/> 反映 <c>0x01</c> 明文标志位是否置位。</para></param>
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
    /// <param name="data">Little-endian record starting at the first byte: four-byte blkaddr, two-byte len and
    /// the flags byte at offset <c>6</c>; the trailing byte is ignored.
    /// <para>自首字节开始的小端记录：4 字节 blkaddr、2 字节 len，偏移 <c>6</c> 处为标志字节；末尾字节被忽略。</para></param>
    /// <returns>The entry decoded from the first <c>8</c> bytes of <paramref name="data"/>.
    /// <para>由 <paramref name="data"/> 前 <c>8</c> 字节解码得到的索引条目。</para></returns>
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
    /// <param name="destination">Destination span of at least <c>8</c> bytes; its first eight bytes are cleared,
    /// then blkaddr, len and flags are written little-endian and the last byte stays zero.
    /// <para>至少 <c>8</c> 字节的目标区间；先清零前 8 字节，再按小端写入 blkaddr、len 与 flags，最后一字节保持为 0。</para></param>
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