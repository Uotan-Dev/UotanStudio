using System.Buffers.Binary;
using System.IO.Compression;
using K4os.Compression.LZ4;
using SevenZip;
using SevenZip.Compression.LZMA;
using UotanStudio.Firmware.Compression.Codecs.Lz4;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using ZstdSharp;
using ZstdSharp.Unsafe;

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

    /// <summary>
    /// Compresses one pcluster with the managed LZ4 block encoder, returning the compressed byte count
    /// or zero when the stream does not fit the destination.
    /// <para>用托管 LZ4 块编码器压缩单个 pcluster，返回压缩字节数；输出放不进目标缓冲区时返回 0。</para>
    /// </summary>
    /// <param name="source">Buffer holding the uncompressed pcluster bytes.
    /// <para>存放未压缩 pcluster 字节的缓冲区。</para></param>
    /// <param name="sourceIndex">Start index of the input range inside <paramref name="source"/>.
    /// <para><paramref name="source"/> 内输入区间的起始索引。</para></param>
    /// <param name="sourceLength">Input bytes to encode; <c>0</c> writes nothing and returns <c>0</c>.
    /// <para>参与编码的字节数；为 <c>0</c> 时不写入任何内容并返回 <c>0</c>。</para></param>
    /// <param name="target">Buffer receiving the encoded LZ4 block. <para>接收 LZ4 编码结果的缓冲区。</para></param>
    /// <param name="targetIndex">Start index of the destination range inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 内目标区间的起始索引。</para></param>
    /// <param name="targetLength">Capacity reserved for the block; <c>0</c> skips encoding and returns <c>0</c>.
    /// <para>为压缩块预留的容量；为 <c>0</c> 时跳过编码并返回 <c>0</c>。</para></param>
    /// <param name="compressionLevel">EROFS LZ4 level applied through the fast path as
    /// <c>10 - compressionLevel</c> clamped to 1-10, so a larger value selects a faster, weaker encode.
    /// <para>EROFS 的 LZ4 等级，经快速路径按 <c>10 - compressionLevel</c>（截断到 1-10）生效，取值越大编码越快、压缩率越低。</para></param>
    /// <returns>Bytes written at <paramref name="targetIndex"/>, or <c>0</c> when the block does not fit the
    /// destination or the encoder consumed less than <paramref name="sourceLength"/> input bytes.
    /// <para>写入 <paramref name="targetIndex"/> 处的字节数；块放不进目标区间或编码器消耗字节数少于
    /// <paramref name="sourceLength"/> 时返回 <c>0</c>。</para></returns>
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
    /// <param name="source">Buffer holding the encoded LZ4 block. <para>存放 LZ4 编码块的缓冲区。</para></param>
    /// <param name="sourceIndex">Start index of the compressed range inside <paramref name="source"/>.
    /// <para><paramref name="source"/> 内压缩区间的起始索引。</para></param>
    /// <param name="sourceLength">Compressed bytes offered to the decoder. <para>交给解码器的压缩字节数。</para></param>
    /// <param name="target">Buffer receiving the recovered pcluster bytes. <para>接收还原 pcluster 字节的缓冲区。</para></param>
    /// <param name="targetIndex">Start index of the destination range inside <paramref name="target"/>.
    /// <para><paramref name="target"/> 内目标区间的起始索引。</para></param>
    /// <param name="targetLength">Capacity reserved for the decoded block. <para>为解码结果预留的目标容量（字节）。</para></param>
    /// <returns>Bytes written at <paramref name="targetIndex"/>, or <c>0</c> when the block is malformed or the
    /// destination range is smaller than the decoded output.
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
    public static int MaximumOutputSize(int length) => LZ4Codec.MaximumOutputSize(length);

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
    /// <param name="compressionLevel">Codec level: steers the LZ4 search, selects the LZMA algorithm and
    /// num-fast-bytes, is clamped to 0..9 for DEFLATE and -5..22 for ZSTD.
    /// <para>编解码级别：影响 LZ4 搜索、决定 LZMA 算法与 num-fast-bytes，DEFLATE 限制到 0..9，ZSTD 限制到 -5..22。</para></param>
    /// <returns>The number of compressed bytes written to <paramref name="target"/>; the LZ4 branch
    /// returns zero when the block does not fit the destination.
    /// <para>写入 <paramref name="target"/> 的压缩字节数；LZ4 分支在块放不下目标时返回 0。</para></returns>
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
    /// <param name="compressionLevel">Codec level: steers the LZ4 search, selects the LZMA algorithm and
    /// num-fast-bytes, is clamped to 0..9 for DEFLATE and -5..22 for ZSTD.
    /// <para>编解码级别：影响 LZ4 搜索、决定 LZMA 算法与 num-fast-bytes，DEFLATE 限制到 0..9，ZSTD 限制到 -5..22。</para></param>
    /// <param name="dictSize">MicroLZMA dictionary size in bytes; <c>0</c> derives one from the input
    /// length, other codecs ignore it. <para>MicroLZMA 字典字节数；<c>0</c> 按输入长度推导，其余算法忽略。</para></param>
    /// <param name="windowBits">DEFLATE window bits (8..15, <c>0</c> for the default) and ZSTD window
    /// log; ignored by LZ4 and LZMA. <para>DEFLATE 窗口位数（8..15，<c>0</c> 为默认）与 ZSTD 窗口对数；LZ4 与 LZMA 忽略。</para></param>
    /// <returns>The number of compressed bytes written to <paramref name="target"/>; the LZ4 branch
    /// returns zero when the block does not fit the destination.
    /// <para>写入 <paramref name="target"/> 的压缩字节数；LZ4 分支在块放不下目标时返回 0。</para></returns>
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
    /// <param name="options">Image-wide codec settings supplying the algorithm, level, LZ4 encoder
    /// identity, dictionary size and window; the level is raised to at least 1 and a zero window
    /// becomes 20 for ZSTD. <para>提供算法、等级、LZ4 编码器身份、字典大小与窗口的镜像级编解码设置；
    /// 等级至少取 1，ZSTD 在窗口为 0 时使用 20。</para></param>
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
    /// <returns>The number of compressed bytes written to <paramref name="target"/>.
    /// <para>写入 <paramref name="target"/> 的压缩字节数。</para></returns>
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
    /// <param name="algorithm">EROFS codec branch used to decode. <para>用于解码的 EROFS 编解码分支。</para></param>
    /// <param name="source">Buffer holding the compressed input. <para>存放压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the compressed region starts.
    /// <para><paramref name="source"/> 中压缩区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of compressed bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的压缩字节数。</para></param>
    /// <param name="target">Buffer the decompressed bytes are written into. <para>写入解压字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>,
    /// which for LZMA and DEFLATE is also the exact expected decompressed size.
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

    /// <summary>
    /// Algorithm-dispatched decompression, returning the decompressed byte count.
    /// <para>按算法分派解压，返回解压字节数。</para>
    /// </summary>
    /// <param name="algorithm">EROFS codec branch used to decode. <para>用于解码的 EROFS 编解码分支。</para></param>
    /// <param name="source">Buffer holding the compressed input. <para>存放压缩输入的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the compressed region starts.
    /// <para><paramref name="source"/> 中压缩区域的起始偏移。</para></param>
    /// <param name="sourceLength">Number of compressed bytes read from <paramref name="sourceIndex"/>.
    /// <para>自 <paramref name="sourceIndex"/> 起读取的压缩字节数。</para></param>
    /// <param name="target">Buffer the decompressed bytes are written into. <para>写入解压字节的缓冲区。</para></param>
    /// <param name="targetIndex">Offset in <paramref name="target"/> where the output region starts.
    /// <para><paramref name="target"/> 中输出区域的起始偏移。</para></param>
    /// <param name="targetLength">Number of bytes reserved for the output inside <paramref name="target"/>,
    /// which for LZMA and DEFLATE is also the exact expected decompressed size.
    /// <para><paramref name="target"/> 中为输出预留的字节数；对 LZMA 与 DEFLATE 同时是期望的解压长度。</para></param>
    /// <param name="dictSize">MicroLZMA dictionary size in bytes; <c>0</c> uses 8 MiB and values below
    /// 4096 are raised to 4096, other codecs ignore it.
    /// <para>MicroLZMA 字典字节数；<c>0</c> 使用 8 MiB，小于 4096 的值提升到 4096，其余算法忽略。</para></param>
    /// <param name="windowBits">Validated DEFLATE window bits (8..15 or <c>0</c>) and the ZSTD maximum
    /// window log; ignored by LZ4 and LZMA.
    /// <para>经校验的 DEFLATE 窗口位数（8..15 或 <c>0</c>）与 ZSTD 最大窗口对数；LZ4 与 LZMA 忽略。</para></param>
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
            source.AsSpan(sourceIndex + 1, sourceLength - 1).CopyTo(rebuilt.AsSpan(1));
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
    /// DEFLATE compression producing a raw DEFLATE stream (no zlib header/footer, RFC 1951, negative-window semantics).
    /// <para>DEFLATE 压缩，输出原始 DEFLATE 流（无 zlib 头/尾，RFC 1951，负 windowBits 语义）。</para>
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

        using var output = new BoundedWriteStream(target, targetIndex, targetLength);
        using (var deflate = new DeflateStream(output, DeflateEncoderOptions(compressionLevel, windowBits), leaveOpen: true))
        {
            deflate.Write(source, sourceIndex, sourceLength);
        }

        return checked((int)output.Position);
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