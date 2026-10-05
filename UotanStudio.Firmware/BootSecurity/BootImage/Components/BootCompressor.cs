using System.IO.Compression;
using K4os.Compression.LZ4.Streams;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Compression.Compressors;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Components;

/// <summary>
/// Detects and decompresses the compression containers used by boot image components, working on in-memory buffers.
/// <para>检测并解压启动镜像组件使用的压缩容器，基于内存中的缓冲区工作。</para>
/// </summary>
public static class BootCompressor
{
    /// <summary>
    /// Detects the compression format of data by magic bytes.
    /// <para>依据魔数检测数据的压缩格式。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate compressed buffer; a <see langword="null"/> reference or fewer than 4 bytes always yields <c>CompressionType.None</c>.
    /// <para>候选压缩缓冲区；<see langword="null"/> 引用或不足 4 字节时始终返回 <c>CompressionType.None</c>。</para>
    /// </param>
    /// <returns>
    /// The container whose magic matched (gzip, LZ4, legacy LZ4, XZ, bzip2, Zstd, LZOP, Brotli, raw LZMA), otherwise <c>CompressionType.None</c>. Magic matching alone does not prove the payload decodes.
    /// <para>魔数匹配的容器类型（gzip、LZ4、传统 LZ4 帧、XZ、bzip2、Zstd、LZOP、Brotli、原始 LZMA），否则为 <c>CompressionType.None</c>。仅魔数匹配并不能保证数据可以解码。</para>
    /// </returns>
    public static CompressionType Detect(byte[] data)
    {
        if (data is null || data.Length < 4)
        {
            return CompressionType.None;
        }

        if (data[0] == 0x1F && data[1] == 0x8B)
        {
            return CompressionType.Gzip;
        }

        if (data[0] == 0x02 && data[1] == 0x21 && data[2] == 0x4C && data[3] == 0x18)
        {
            return CompressionType.Lz4Legacy;
        }

        if (data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
        {
            return CompressionType.Lz4;
        }

        if (data.Length >= 6 && data[0] == 0xFD && data[1] == 0x37 && data[2] == 0x7A
            && data[3] == 0x58 && data[4] == 0x5A && data[5] == 0x00)
        {
            return CompressionType.Xz;
        }

        if (data[0] == (byte)'B' && data[1] == (byte)'Z' && data[2] == (byte)'h')
        {
            return CompressionType.Bzip2;
        }

        if (data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
        {
            return CompressionType.Zstd;
        }

        if (data[0] == 0x89 && data[1] == 0x4C && data[2] == 0x5A && data[3] == 0x4F)
        {
            return CompressionType.Lzop;
        }

        if (data[0] == 0x0B && data[1] == 0x0A)
        {
            return CompressionType.Brotli;
        }

        return data[0] == 0x5D && data[1] == 0x00 && data[2] == 0x00 ? CompressionType.Lzma : CompressionType.None;
    }

    /// <summary>
    /// Decompresses data, or returns null when unsupported or on failure.
    /// <para>解压数据；失败或格式不受支持时返回 null。</para>
    /// </summary>
    /// <param name="data">
    /// Compressed container held entirely in memory; <see langword="null"/> or empty input yields <see langword="null"/>.
    /// <para>完整驻留内存的压缩容器；<see langword="null"/> 或空输入返回 <see langword="null"/>。</para>
    /// </param>
    /// <param name="type">
    /// Container format to attempt regardless of the actual contents; <c>CompressionType.None</c> decodes to an empty array and unsupported values yield <see langword="null"/>.
    /// <para>无论实际内容为何、要尝试的容器格式；<c>CompressionType.None</c> 会解码为空数组，不受支持的值返回 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// The decompressed bytes, or <see langword="null"/> when the format is unsupported or any decoding error occurs; all exceptions raised by the underlying codecs are swallowed.
    /// <para>解压后的字节；当格式不受支持或出现任何解码错误时返回 <see langword="null"/>，底层编解码器抛出的异常全部被吞没。</para>
    /// </returns>
    public static byte[]? TryDecompress(byte[] data, CompressionType type)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();

            switch (type)
            {
                case CompressionType.Gzip:
                case CompressionType.Zopfli:
                    using (var gz = new GZipStream(input, CompressionMode.Decompress))
                    {
                        gz.CopyTo(output);
                    }

                    break;
                case CompressionType.Bzip2:
                    using (var bz = BZip2Stream.Create(input, SharpCompress.Compressors.CompressionMode.Decompress, false))
                    {
                        bz.CopyTo(output);
                    }

                    break;
                case CompressionType.Xz:
                    using (var xz = new XZStream(input))
                    {
                        xz.CopyTo(output);
                    }

                    break;
                case CompressionType.Zstd:
                    return ZstdCompressor.Decompress(data);
                case CompressionType.Brotli:
                    using (var br = new BrotliStream(input, CompressionMode.Decompress))
                    {
                        br.CopyTo(output);
                    }

                    break;
                case CompressionType.Lz4:
                    using (var lz4 = LZ4Stream.Decode(input, leaveOpen: false))
                    {
                        lz4.CopyTo(output);
                    }

                    break;
                case CompressionType.None:
                    break;
                case CompressionType.Lz4Legacy:
                    return CompressionService.Decompress(data, CompressionFormat.Lz4Legacy);
                case CompressionType.Lz4Lg:
                    return CompressionService.Decompress(data, CompressionFormat.Lz4Legacy);
                case CompressionType.Lzma:
                    return CompressionService.Decompress(data, CompressionFormat.Lzma);
                case CompressionType.Lzop:
                    return LzopCompressor.Decompress(data);

                default:
                    return null;
            }

            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Convenience overload: auto-detects and tries to decompress.
    /// <para>便捷重载：自动检测并尝试解压。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate compressed buffer inspected by <see cref="Detect"/>. <para>由 <see cref="Detect"/> 检查的候选压缩缓冲区。</para>
    /// </param>
    /// <returns>
    /// The decompressed bytes; the input array itself when no magic is detected or decompression fails.
    /// <para>解压后的字节；未检测到魔数或解压失败时返回输入数组本身。</para>
    /// </returns>
    public static byte[] DecompressOrReturn(byte[] data)
    {
        var type = Detect(data);
        return type == CompressionType.None ? data : TryDecompress(data, type) ?? data;
    }

    /// <summary>
    /// Maps a <see cref="CompressionType"/> to the boot-family encodings this component may produce: gzip and
    /// lz4_legacy only, mirroring the decompression side where <c>Zopfli</c> decodes through gzip and <c>Lz4Lg</c>
    /// through the legacy frame.
    /// <para>将 <see cref="CompressionType"/> 映射到本组件可产出的 boot 家族合法编码：仅 gzip 与 lz4_legacy，
    /// 与解压侧对应——<c>Zopfli</c> 按 gzip 解码、<c>Lz4Lg</c> 按传统帧解码。</para>
    /// </summary>
    /// <param name="type">Label to encode under. <para>要按其编码的标签。</para></param>
    /// <param name="format">Mapped container format on success; <see cref="CompressionFormat.None"/> otherwise.
    /// <para>成功时映射到的容器格式；否则为 <see cref="CompressionFormat.None"/>。</para></param>
    /// <returns><see langword="true"/> when the label has a boot-family encoding.
    /// <para>该标签存在 boot 家族合法编码时返回 <see langword="true"/>。</para></returns>
    private static bool TryMapCompressibleFormat(CompressionType type, out CompressionFormat format)
    {
        switch (type)
        {
            case CompressionType.Gzip:
            case CompressionType.Zopfli:
                format = CompressionFormat.Gzip;
                return true;
            case CompressionType.Lz4Legacy:
            case CompressionType.Lz4Lg:
                format = CompressionFormat.Lz4Legacy;
                return true;
            default:
                format = CompressionFormat.None;
                return false;
        }
    }

    /// <summary>
    /// Compresses a boot component with the resolved container format. Empty gzip-family input returns a zero-length
    /// result because boot images store empty components as size-0 sections, whereas a valid empty gzip member carries
    /// the gzip magic and would be detected as a compressed payload. Legacy LZ4 keeps its native behavior, where empty
    /// input still produces the magic-only frame.
    /// <para>按解析出的容器格式压缩 boot 组件。gzip 家族的空输入返回零长结果，因为 boot 镜像把空组件存成 0 长度区段，
    /// 而合法的 gzip 空成员带有 gzip 魔数、会被识别成压缩载荷。传统 LZ4 保持原生行为，空输入仍产出仅含魔数的帧。</para>
    /// </summary>
    /// <param name="data">Plaintext buffer. <para>明文缓冲区。</para></param>
    /// <param name="format">Resolved container format. <para>已解析的容器格式。</para></param>
    /// <returns>The encoded bytes. <para>编码后的字节。</para></returns>
    private static byte[] CompressBootFamilyPayload(byte[] data, CompressionFormat format)
        => data.Length == 0 && format == CompressionFormat.Gzip
            ? []
            : CompressionService.Compress(data, format);

    /// <summary>
    /// Compresses data with the boot-family encoding selected by the type label.
    /// <para>按类型标签选定的 boot 家族编码压缩数据。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext buffer held entirely in memory; <see langword="null"/> yields <see langword="null"/>.
    /// <para>完整驻留内存的明文缓冲区；<see langword="null"/> 输入返回 <see langword="null"/>。</para>
    /// </param>
    /// <param name="type">
    /// Target label; only <see cref="CompressionType.Gzip"/>, <see cref="CompressionType.Zopfli"/>,
    /// <see cref="CompressionType.Lz4Legacy"/> and <see cref="CompressionType.Lz4Lg"/> have boot-family encodings.
    /// <para>目标标签；仅 <see cref="CompressionType.Gzip"/>、<see cref="CompressionType.Zopfli"/>、
    /// <see cref="CompressionType.Lz4Legacy"/> 与 <see cref="CompressionType.Lz4Lg"/> 存在 boot 家族合法编码。</para>
    /// </param>
    /// <returns>
    /// The compressed bytes whose leading magic is recognized back by <see cref="Detect"/> for a non-empty payload;
    /// an empty payload yields the codec's empty-stream encoding, which carries no magic and detects as
    /// <see cref="CompressionType.None"/>. Returns <see langword="null"/> when the label has no boot-family encoding
    /// or the codec fails; all codec exceptions are swallowed.
    /// <para>非空输入时返回压缩字节，其首部魔数可被 <see cref="Detect"/> 重新识别；空输入产生编解码器的空流编码，
    /// 不含魔数，检测结果为 <see cref="CompressionType.None"/>。标签没有 boot 家族编码或编解码失败时返回
    /// <see langword="null"/>，编解码器异常全部被吞没。</para>
    /// </returns>
    public static byte[]? TryCompress(byte[] data, CompressionType type)
    {
        if (data is null || !TryMapCompressibleFormat(type, out var format))
        {
            return null;
        }

        try
        {
            return CompressBootFamilyPayload(data, format);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Span-based overload of <see cref="TryCompress(byte[], CompressionType)"/> for callers holding slices.
    /// <para><see cref="TryCompress(byte[], CompressionType)"/> 的 Span 重载，供持有切片的调用方使用。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext span copied into a buffer before encoding; an empty span produces the codec's empty-stream encoding.
    /// <para>编码前先复制到缓冲区的明文切片；空切片生成编解码器的空流编码。</para>
    /// </param>
    /// <param name="type">
    /// Target label with a boot-family encoding; see <see cref="TryCompress(byte[], CompressionType)"/>.
    /// <para>具有 boot 家族编码的目标标签；参见 <see cref="TryCompress(byte[], CompressionType)"/>。</para>
    /// </param>
    /// <param name="compressed">
    /// The compressed bytes on success; <see langword="null"/> when the label is unsupported or encoding fails.
    /// <para>成功时为压缩字节；标签不受支持或编码失败时为 <see langword="null"/>。</para>
    /// </param>
    /// <returns><see langword="true"/> when the span was compressed successfully.
    /// <para>切片压缩成功时返回 <see langword="true"/>。</para></returns>
    public static bool TryCompress(ReadOnlySpan<byte> data, CompressionType type, out byte[]? compressed)
    {
        compressed = null;
        if (!TryMapCompressibleFormat(type, out var format))
        {
            return false;
        }

        try
        {
            compressed = CompressBootFamilyPayload(data.ToArray(), format);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compresses data with the boot-family encoding selected by the type label, reporting failures instead of
    /// returning <see langword="null"/>.
    /// <para>按类型标签选定的 boot 家族编码压缩数据，失败时抛出异常而非返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext buffer to compress. <para>待压缩的明文缓冲区。</para>
    /// </param>
    /// <param name="type">
    /// Target label; only gzip-family and legacy-LZ4-family labels are accepted.
    /// <para>目标标签；仅接受 gzip 家族与传统 LZ4 家族标签。</para>
    /// </param>
    /// <returns>
    /// The compressed bytes; for a non-empty payload the stream magic is recognized back by <see cref="Detect"/>,
    /// while an empty payload yields the codec's empty-stream encoding without magic, matching how boot images
    /// store empty components as size-0 sections instead of compressed streams.
    /// <para>压缩后的字节；非空输入的流魔数可被 <see cref="Detect"/> 重新识别，而空输入产生编解码器的空流编码且
    /// 不含魔数，与 boot 镜像将空组件存为 0 长度区段而非压缩流的方式一致。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="NotSupportedException">The label has no boot-family encoding.
    /// <para>该标签没有 boot 家族合法编码。</para></exception>
    /// <exception cref="CompressionException">The codec failed while compressing.
    /// <para>编解码器压缩失败。</para></exception>
    public static byte[] Compress(byte[] data, CompressionType type)
    {
        if (!TryMapCompressibleFormat(type, out var format))
        {
            throw new NotSupportedException($"压缩类型 {type} 没有 boot 家族合法编码 / Compression type {type} has no boot-family encoding.");
        }

        return CompressBootFamilyPayload(data, format);
    }
}