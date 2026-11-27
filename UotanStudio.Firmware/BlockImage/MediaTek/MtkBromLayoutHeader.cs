using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BlockImage.MediaTek;

/// <summary>
/// The MediaTek BootROM layout header (<c>brom_layout_header</c>) that a device may place ahead of its
/// partition table, as defined by the vendor's <c>mtk_image.h</c>. Unused trailing bytes are left untouched,
/// so callers can keep the raw prefix for evidence.
/// <para>设备可能放在分区表之前的联发科 BootROM 布局头（<c>brom_layout_header</c>），定义见厂商
/// <c>mtk_image.h</c>。尾部未使用字节保持原样，调用方可以保留原始前缀作为证据。</para>
/// </summary>
/// <remarks>
/// A header is only reported when the eight-byte name is <c>BRLYT</c> and the magic word is
/// <see cref="Magic"/>. The structure carries the same length twice — once as <c>header_size</c>/<c>total_size</c>
/// and once as <c>header_size_2</c>/<c>total_size_2</c> — so a header whose duplicated lengths disagree is
/// reported as invalid instead of being repaired around.
/// <para>仅当八字节名称是 <c>BRLYT</c> 且魔数为 <see cref="Magic"/> 时才判定为布局头。该结构把同样的长度
/// 存了两遍（<c>header_size</c>/<c>total_size</c> 与 <c>header_size_2</c>/<c>total_size_2</c>），因此两份长度
/// 不一致时按无效上报，而不是靠修复绕过。</para>
/// </remarks>
public sealed class MtkBromLayoutHeader
{
    /// <summary>The eight-byte name a layout header starts with.
    /// <para>布局头起始的八字节名称。</para>
    /// </summary>
    public static ReadOnlySpan<byte> Name => "BRLYT"u8;

    /// <summary>The magic word carried at offset 20.
    /// <para>偏移 20 处的魔数。</para>
    /// </summary>
    public const uint Magic = 0x42424242;

    /// <summary>The smallest prefix that can carry a complete layout header.
    /// <para>能承载完整布局头的最小前缀长度。</para>
    /// </summary>
    public const int MinimumLength = 36;

    /// <summary>The largest header size this parser accepts, which keeps a corrupt length from driving a read.
    /// <para>本解析器接受的头部长度上限，避免损坏的长度驱动一次读取。</para>
    /// </summary>
    public const uint MaximumHeaderSize = 0x10000;

    /// <summary>The declared header length in bytes, which is also the offset of the partition table it precedes.
    /// <para>声明的头部字节长度，同时也是其后分区表的起始偏移。</para>
    /// </summary>
    /// <value>A positive byte count, or zero for the default instance.
    /// <para>正的字节数；默认实例为零。</para></value>
    public uint HeaderSize { get; private init; }

    /// <summary>The declared total payload length in bytes.
    /// <para>声明的负载总字节数。</para>
    /// </summary>
    /// <value>A positive byte count, or zero for the default instance.
    /// <para>正的字节数；默认实例为零。</para></value>
    public uint TotalSize { get; private init; }

    /// <summary>The media type word carried at offset 24.
    /// <para>偏移 24 处的介质类型字。</para>
    /// </summary>
    /// <value>A <c>BRLYT_TYPE_*</c> value, or zero for the default instance.
    /// <para><c>BRLYT_TYPE_*</c> 取值；默认实例为零。</para></value>
    public uint MediaType { get; private init; }

    /// <summary>Gets whether a recognised header was parsed.
    /// <para>获取是否解析出可识别的布局头。</para>
    /// </summary>
    /// <value><see langword="true"/> when the name, magic and duplicated lengths all agreed.
    /// <para>名称、魔数与两份重复长度全部一致时为 <see langword="true"/>。</para></value>
    public bool IsRecognized { get; private init; }

    /// <summary>The default instance, which reports no recognised header.
    /// <para>默认实例，表示没有识别出布局头。</para>
    /// </summary>
    public static MtkBromLayoutHeader None { get; } = new();

    /// <summary>Attempts to parse a layout header from the bytes at the start of a storage region.
    /// <para>尝试从存储区域起始的字节解析布局头。</para>
    /// </summary>
    /// <param name="prefix">The leading bytes of the region; a prefix shorter than <see cref="MinimumLength"/> is rejected.
    /// <para>区域的起始字节；短于 <see cref="MinimumLength"/> 的前缀会被拒绝。</para></param>
    /// <param name="header">The parsed header, or <see cref="None"/> when the bytes are not one.
    /// <para>解析出的布局头；字节不是布局头时为 <see cref="None"/>。</para></param>
    /// <returns><see langword="true"/> when a consistent header was parsed.
    /// <para>解析出前后一致的布局头时为 <see langword="true"/>。</para></returns>
    public static bool TryParse(ReadOnlySpan<byte> prefix, out MtkBromLayoutHeader header)
    {
        header = None;
        if (prefix.Length < MinimumLength || !prefix[..Name.Length].SequenceEqual(Name))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix[20..]) != Magic)
        {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..]);
        var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix[16..]);
        var headerSizeAgain = BinaryPrimitives.ReadUInt32LittleEndian(prefix[28..]);
        var totalSizeAgain = BinaryPrimitives.ReadUInt32LittleEndian(prefix[32..]);
        if (headerSize == 0 || headerSize > MaximumHeaderSize || headerSize != headerSizeAgain
            || totalSize != totalSizeAgain || totalSize < headerSize)
        {
            return false;
        }

        header = new MtkBromLayoutHeader
        {
            HeaderSize = headerSize,
            TotalSize = totalSize,
            MediaType = BinaryPrimitives.ReadUInt32LittleEndian(prefix[24..]),
            IsRecognized = true,

        };
        return true;
    }

    /// <summary>Renders the media type as the vendor's symbolic name when the word is one of the documented values.
    /// <para>当介质类型字属于已记录取值时，按厂商的符号名呈现。</para>
    /// </summary>
    /// <returns>The symbolic name, or a hexadecimal fallback.
    /// <para>符号名，或十六进制回退形式。</para></returns>
    public string DescribeMediaType() => MediaType switch
    {
        0x10002 => "NAND",
        0x10005 => "EMMC",
        0x10007 => "NOR",
        0x10008 => "SDMMC",
        0x10009 => "SNAND",
        _ => "0x" + MediaType.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
    };
}
