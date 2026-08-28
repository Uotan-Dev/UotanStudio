using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// Represents the standard 64-byte AOSP AVB footer appended to a partition image; integers are big-endian.
/// <para>表示附加到分区镜像末尾的标准 64 字节 AOSP AVB 尾部；整数字段均为大端序。</para>
/// </summary>
public readonly struct AvbFooter
{
    /// <summary>Gets the standard footer size in bytes. <para>获取标准尾部的字节大小。</para></summary>
    public const int Size = 64;

    /// <summary>Gets the expected footer magic. <para>获取预期的尾部魔数。</para></summary>
    public const string ExpectedMagic = "AVBf";

    /// <summary>Gets the supported standard footer major version. <para>获取支持的标准尾部主版本号。</para></summary>
    public const uint SupportedVersionMajor = 1;

    /// <summary>Gets the highest supported footer minor version. <para>获取支持的最高尾部次版本号。</para></summary>
    public const uint SupportedVersionMinor = 0;

    /// <summary>Gets the parsed footer magic. <para>获取解析出的尾部魔数。</para></summary>
    public string Magic { get; init; }

    /// <summary>Gets the footer format major version. <para>获取尾部格式主版本号。</para></summary>
    public uint VersionMajor { get; init; }

    /// <summary>Gets the footer format minor version. <para>获取尾部格式次版本号。</para></summary>
    public uint VersionMinor { get; init; }

    /// <summary>Gets the partition image size before VBMeta and padding were appended. <para>获取附加 VBMeta 和填充前的分区镜像大小。</para></summary>
    public ulong OriginalImageSize { get; init; }

    /// <summary>Gets the absolute byte offset of the VBMeta image in the partition image. <para>获取 VBMeta 镜像在分区镜像中的绝对字节偏移。</para></summary>
    public ulong VbmetaOffset { get; init; }

    /// <summary>Gets the complete VBMeta image size in bytes. <para>获取完整 VBMeta 镜像的字节大小。</para></summary>
    public ulong VbmetaSize { get; init; }

    /// <summary>Parses a standard footer from the end of an image or a footer-sized span. <para>从镜像末尾或尾部大小的 span 中解析标准尾部。</para></summary>
    /// <param name="imageData">The image bytes or a 64-byte footer span. <para>镜像字节或 64 字节尾部 span。</para></param>
    /// <returns>The parsed footer, or <c>null</c> if the data is too short or has invalid magic. <para>解析出的尾部；数据过短或魔数无效时为 <c>null</c>。</para></returns>
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
        var versionMinor = BinaryPrimitives.ReadUInt32BigEndian(footer.Slice(8, 4));
        if (versionMajor != SupportedVersionMajor || versionMinor > SupportedVersionMinor)
        {
            return null;
        }

        return new AvbFooter
        {
            Magic = Encoding.ASCII.GetString(footer[..4]),
            VersionMajor = versionMajor,
            VersionMinor = versionMinor,
            OriginalImageSize = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(12, 8)),
            VbmetaOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(20, 8)),
            VbmetaSize = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(28, 8))
        };
    }
}