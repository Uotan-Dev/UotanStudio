using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;

public readonly struct AvbFooter
{
    public const int Size = 64;

    public const string ExpectedMagic = "AVBf";

    public const uint SupportedVersionMajor = 1;

    public const uint SupportedVersionMinor = 0;

    public string Magic { get; init; }

    public uint VersionMajor { get; init; }

    public uint VersionMinor { get; init; }

    public ulong OriginalImageSize { get; init; }

    public ulong VbmetaOffset { get; init; }

    public ulong VbmetaSize { get; init; }

    /// <summary>
    /// Parses a standard footer from the end of an image or a footer-sized span.
    /// <para>从镜像末尾或尾部大小的 span 中解析标准尾部。</para>
    /// </summary>
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