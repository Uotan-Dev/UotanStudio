using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BootSecurity.Avb;

/// <summary>
/// The 64-byte AVB footer (<c>AVBf</c>) appended after the vbmeta struct at the end of a signed
/// partition image; every field on disk is big-endian.
/// <para>追加在已签名分区镜像末尾、vbmeta 结构之后的 64 字节 AVB 尾部（<c>AVBf</c>）；磁盘上的每个字段均为大端序。</para>
/// </summary>
public readonly struct AvbFooter
{
    /// <summary>Fixed on-disk size of the footer in bytes. <para>尾部在磁盘上的固定字节大小。</para></summary>
    public const int Size = 64;

    /// <summary>The four ASCII magic bytes at the start of the footer. <para>尾部起始处的四个 ASCII 魔数字节。</para></summary>
    public const string ExpectedMagic = "AVBf";

    /// <summary>Footer major version this library reads and writes. <para>本库读取与写入时使用的尾部主版本号。</para></summary>
    public const uint SupportedVersionMajor = 1;

    /// <summary>Footer minor version this library writes; parsing accepts any minor value.
    /// <para>本库写入的尾部次版本号；解析时接受任意次版本值。</para></summary>
    public const uint SupportedVersionMinor = 0;

    /// <summary>
    /// The magic read from footer bytes 0..3; always <see cref="ExpectedMagic"/> after a successful parse.
    /// <para>从尾部第 0..3 字节读出的魔数；解析成功后恒为 <see cref="ExpectedMagic"/>。</para>
    /// </summary>
    /// <value>The four ASCII magic bytes as a string. <para>以字符串表示的四个 ASCII 魔数字节。</para></value>
    public string Magic { get; init; }

    /// <summary>Major format version, a big-endian uint32 at footer offset 4.
    /// <para>主格式版本号，位于尾部偏移 4 的大端 uint32。</para></summary>
    /// <value>The parsed major version. <para>解析出的主版本号。</para></value>
    public uint VersionMajor { get; init; }

    /// <summary>Minor format version, a big-endian uint32 at footer offset 8.
    /// <para>次格式版本号，位于尾部偏移 8 的大端 uint32。</para></summary>
    /// <value>The parsed minor version; parsing accepts any value. <para>解析出的次版本号；解析时接受任意值。</para></value>
    public uint VersionMinor { get; init; }

    /// <summary>
    /// Size in bytes of the original image content before the hashtree, vbmeta and footer regions
    /// were appended, a big-endian uint64 at footer offset 12.
    /// <para>追加哈希树、vbmeta 与尾部区域之前的原始镜像内容字节大小，位于尾部偏移 12 的大端 uint64。</para>
    /// </summary>
    /// <value>The pre-append image content size in bytes. <para>追加之前的镜像内容字节大小。</para></value>
    public ulong OriginalImageSize { get; init; }

    /// <summary>Byte offset from the start of the image to the vbmeta struct, a big-endian uint64 at
    /// footer offset 20. <para>从镜像起始到 vbmeta 结构的字节偏移，位于尾部偏移 20 的大端 uint64。</para></summary>
    /// <value>The absolute vbmeta struct offset. <para>vbmeta 结构的绝对偏移。</para></value>
    public ulong VbmetaOffset { get; init; }

    /// <summary>Byte length of the vbmeta struct as recorded in the footer, a big-endian uint64 at
    /// footer offset 28. <para>尾部中记录的 vbmeta 结构字节长度，位于尾部偏移 28 的大端 uint64。</para></summary>
    /// <value>The vbmeta struct length in bytes. <para>vbmeta 结构的字节长度。</para></value>
    public ulong VbmetaSize { get; init; }

    /// <summary>
    /// Parses a standard footer from the end of an image or a footer-sized span.
    /// <para>从镜像末尾或尾部大小的 span 中解析标准尾部。</para>
    /// </summary>
    /// <param name="imageData">
    /// Span holding the whole image or just its trailing window; only the last <see cref="Size"/> bytes
    /// are read.
    /// <para>包含完整镜像或仅尾部窗口的 span；只读取最后 <see cref="Size"/> 个字节。</para>
    /// </param>
    /// <returns>
    /// The parsed footer, or <see langword="null"/> when the span is shorter than <see cref="Size"/>,
    /// the magic does not match, or <see cref="VersionMajor"/> is not <see cref="SupportedVersionMajor"/>.
    /// <para>解析后的尾部；当 span 短于 <see cref="Size"/>、魔数不匹配，或 <see cref="VersionMajor"/>
    /// 不等于 <see cref="SupportedVersionMajor"/> 时返回 <see langword="null"/>。</para>
    /// </returns>
    public static AvbFooter? TryLoad(ReadOnlySpan<byte> imageData)
    {
        if (imageData.Length < Size)
        {
            return null;
        }

        var footer = imageData[^Size..];
        if (!footer[..4].SequenceEqual("AVBf"u8))
        {
            return null;
        }

        var versionMajor = BinaryPrimitives.ReadUInt32BigEndian(footer.Slice(4, 4));


        if (versionMajor != SupportedVersionMajor)
        {
            return null;
        }

        return new AvbFooter
        {
            Magic = Encoding.ASCII.GetString(footer[..4]),
            VersionMajor = versionMajor,
            VersionMinor = BinaryPrimitives.ReadUInt32BigEndian(footer.Slice(8, 4)),
            OriginalImageSize = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(12, 8)),
            VbmetaOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(20, 8)),
            VbmetaSize = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(28, 8))
        };
    }

    /// <summary>
    /// Reads only the trailing footer-sized window from a file, so an AVB-signed image is
    /// detected without loading it whole into memory. Returns <see langword="null"/> when the
    /// file is smaller than a footer or its tail carries no valid footer.
    /// <para>仅读取文件尾部与 footer 等长的窗口来检测 AVB 签名镜像，避免整镜像载入内存。
    /// 文件小于 footer 或尾部无有效 footer 时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="path">File system path of the candidate image; the file is opened for reading,
    /// shared for concurrent readers, and closed before this method returns.
    /// <para>候选镜像的文件系统路径；文件以只读、允许并发读取的方式打开，并在本方法返回前关闭。</para></param>
    /// <returns>The parsed footer, or <see langword="null"/> when the file is shorter than
    /// <see cref="Size"/> bytes or its tail does not parse via <see cref="TryLoad"/>.
    /// <para>解析后的尾部；当文件短于 <see cref="Size"/> 字节，或其尾部无法通过 <see cref="TryLoad"/>
    /// 解析时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="FileNotFoundException">The file does not exist.
    /// <para>文件不存在。</para></exception>
    public static AvbFooter? TryReadFile(string path)
    {
        var length = new FileInfo(path).Length;
        if (length < Size)
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: Size, FileOptions.SequentialScan);
        stream.Seek(length - Size, SeekOrigin.Begin);
        Span<byte> footer = stackalloc byte[Size];
        stream.ReadExactly(footer);
        return TryLoad(footer);
    }
}