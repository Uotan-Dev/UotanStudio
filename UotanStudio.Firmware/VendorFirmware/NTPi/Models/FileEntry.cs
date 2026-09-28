using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Firmware.VendorFirmware.NTPi.Models;

/// <summary>
/// One <c>file</c> record of an NTPI package FileIndex: the stored name, the plaintext and encoded sizes, the
/// plaintext SHA-256 digest, the key-map slot and where the file's encoded blocks start inside Region6. Every
/// property is init-only, so an instance describes a layout that the caller must not mutate in place.
/// <para>NTPI 包 FileIndex 中的一条 <c>file</c> 记录：存储的文件名、明文与编码后的长度、明文 SHA-256 摘要、
/// 密钥映射槽位，以及该文件编码块在 Region6 中的起始位置。所有属性仅可在初始化时赋值，
/// 实例所描述的布局不应被就地修改。</para>
/// </summary>
public class FileEntry
{
    /// <summary>
    /// File name recorded in the index; packing stores the path relative to the input directory, and in-memory
    /// parse results key extracted data by this value.
    /// <para>索引中记录的文件名；打包时保存相对于输入目录的路径，内存解析结果也以该值作为提取数据的键。</para>
    /// </summary>
    /// <value>
    /// The raw index name, which may contain directory separators; extraction keeps only its file component when
    /// writing to disk. Never <see langword="null"/>; defaults to an empty string.
    /// <para>原始索引名，可以包含目录分隔符；写盘提取时仅保留其文件名部分。永不为 <see langword="null"/>，
    /// 默认值为空字符串。</para>
    /// </value>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Plaintext size declared for the file, used as the bound for decoder output during extraction.
    /// <para>声明的文件明文大小，提取时作为解码器输出的上限。</para>
    /// </summary>
    /// <value>
    /// Exact byte count the decoded file must reach; <c>0</c> marks an empty file whose extraction writes nothing.
    /// A negative value is rejected by the reader as an invalid format.
    /// <para>解码文件必须达到的准确字节数；<c>0</c> 表示空文件，提取时不写出任何内容。负值会被读取器判定为格式无效。</para>
    /// </value>
    public long OriginalLength { get; init; }

    /// <summary>
    /// Encoded size the file occupies in Region6, spanning its <c>NTENCODE</c> block headers and ciphertext.
    /// <para>文件在 Region6 中占用的编码后大小，涵盖其 <c>NTENCODE</c> 块头和密文。</para>
    /// </summary>
    /// <value>
    /// Total stored bytes, so extraction keeps reading <c>NTENCODE</c> blocks until this many bytes are consumed;
    /// <c>0</c> for an empty file. Negative values and values shorter than one 112-byte header are rejected.
    /// <para>存储的总字节数，提取会持续读取 <c>NTENCODE</c> 块直到用完这些字节；空文件为 <c>0</c>。
    /// 负值以及小于单个 112 字节头部的值都会被拒绝。</para>
    /// </value>
    public long Length { get; init; }

    /// <summary>
    /// SHA-256 digest of the file's plaintext content, recorded so extraction can verify integrity.
    /// <para>文件明文内容的 SHA-256 摘要，供提取时校验完整性。</para>
    /// </summary>
    /// <value>
    /// Lowercase hexadecimal digest; comparison during extraction is case-insensitive, and an empty string means no
    /// digest is available, so the hash check is skipped. Never <see langword="null"/>.
    /// <para>小写十六进制摘要；提取时的比较不区分大小写，空字符串表示没有摘要，因此跳过哈希校验。永不为
    /// <see langword="null"/>。</para>
    /// </value>
    public string FileSha256Hash { get; init; } = string.Empty;

    /// <summary>
    /// Key-map slot that supplies the AES key for this file's encoded blocks.
    /// <para>为该文件编码块提供 AES 密钥的密钥映射槽位。</para>
    /// </summary>
    /// <value>
    /// Index into the package's <c>KeyMap.bin</c> table, combined with the per-file block counter to derive each
    /// block key; <c>0</c> when the index attribute is absent or unparsable.
    /// <para>指向包内 <c>KeyMap.bin</c> 表的索引，与逐文件块计数器一起推导每个块的密钥；
    /// 索引属性缺失或无法解析时为 <c>0</c>。</para>
    /// </value>
    public int KeyIndex { get; init; }

    /// <summary>
    /// Start of the file's encoded payload relative to the beginning of the Region6 data.
    /// <para>文件编码载荷相对于 Region6 数据起始位置的偏移。</para>
    /// </summary>
    /// <value>
    /// Zero-based byte offset added to the Region6 base position to locate the file's first <c>NTENCODE</c> header;
    /// the first stored file starts at <c>0</c>, and an empty file occupies no bytes at all.
    /// <para>与 Region6 基准位置相加、用于定位该文件首个 <c>NTENCODE</c> 头部的从零开始字节偏移；首个存储文件从
    /// <c>0</c> 开始，空文件完全不占用字节。</para>
    /// </value>
    public long Offset { get; init; }

    /// <summary>
    /// Parses an XML element and creates a FileEntry instance.
    /// <para>从 XML 元素解析并创建 FileEntry 实例。</para>
    /// </summary>
    /// <param name="element">
    /// FileIndex element whose <c>Name</c>, <c>OriginalLength</c>, <c>Length</c>, <c>FileSha256Hash</c>,
    /// <c>KeyIndex</c> and <c>Offset</c> attributes are read.
    /// <para>读取其 <c>Name</c>、<c>OriginalLength</c>、<c>Length</c>、<c>FileSha256Hash</c>、<c>KeyIndex</c>
    /// 和 <c>Offset</c> 属性的 FileIndex 元素。</para>
    /// </param>
    /// <returns>
    /// A populated entry; an absent numeric attribute or one that fails to parse yields <c>0</c>, and an absent text
    /// attribute yields an empty string. Attribute names are matched case-sensitively.
    /// <para>已填充的条目；数值属性缺失或解析失败时取 <c>0</c>，文本属性缺失时取空字符串。属性名区分大小写。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.
    /// <para><paramref name="element"/> 为 <see langword="null"/>。</para></exception>
    public static FileEntry FromXmlElement(XElement element)
    {
        return element is null
            ? throw new ArgumentNullException(nameof(element))
            : new FileEntry
            {
                Name = element.Attribute("Name")?.Value ?? string.Empty,
                OriginalLength = long.TryParse(element.Attribute("OriginalLength")?.Value, out var origLen) ? origLen : 0,
                Length = long.TryParse(element.Attribute("Length")?.Value, out var len) ? len : 0,
                FileSha256Hash = element.Attribute("FileSha256Hash")?.Value ?? string.Empty,
                KeyIndex = int.TryParse(element.Attribute("KeyIndex")?.Value, out var keyIdx) ? keyIdx : 0,
                Offset = long.TryParse(element.Attribute("Offset")?.Value, out var offset) ? offset : 0
            };
    }

    /// <summary>
    /// Reads attributes from the current XmlReader position and creates a FileEntry instance, avoiding XDocument intermediate allocations.
    /// <para>从 XmlReader 当前位置读取属性并创建 FileEntry 实例，避免 XDocument 中间分配。</para>
    /// </summary>
    /// <param name="reader">
    /// Reader positioned on the element to read; the caller owns it and advances it, and only the current node's
    /// <c>Name</c>, <c>OriginalLength</c>, <c>Length</c>, <c>FileSha256Hash</c>, <c>KeyIndex</c> and <c>Offset</c>
    /// attributes are consumed.
    /// <para>定位到待读取元素上的读取器；调用方持有并推进它，此处只消费当前节点的 <c>Name</c>、<c>OriginalLength</c>、
    /// <c>Length</c>、<c>FileSha256Hash</c>、<c>KeyIndex</c> 和 <c>Offset</c> 属性。</para>
    /// </param>
    /// <returns>
    /// A populated entry with the same attribute defaults as <see cref="FromXmlElement"/>: <c>0</c> for absent or
    /// unparsable numeric attributes and an empty string for absent text attributes.
    /// <para>已填充的条目，其属性默认值与 <see cref="FromXmlElement"/> 相同：缺失或无法解析的数值属性取 <c>0</c>，
    /// 缺失的文本属性取空字符串。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.
    /// <para><paramref name="reader"/> 为 <see langword="null"/>。</para></exception>
    public static FileEntry FromXmlAttributes(XmlReader reader)
    {
        return reader is null
            ? throw new ArgumentNullException(nameof(reader))
            : new FileEntry
            {
                Name = reader.GetAttribute("Name") ?? string.Empty,
                OriginalLength = long.TryParse(reader.GetAttribute("OriginalLength"), out var origLen) ? origLen : 0,
                Length = long.TryParse(reader.GetAttribute("Length"), out var len) ? len : 0,
                FileSha256Hash = reader.GetAttribute("FileSha256Hash") ?? string.Empty,
                KeyIndex = int.TryParse(reader.GetAttribute("KeyIndex"), out var keyIdx) ? keyIdx : 0,
                Offset = long.TryParse(reader.GetAttribute("Offset"), out var offset) ? offset : 0
            };
    }

    /// <summary>
    /// Converts the current FileEntry instance to an XML element.
    /// <para>将当前 FileEntry 实例转换为 XML 元素。</para>
    /// </summary>
    /// <returns>
    /// A fresh in-memory <c>file</c> element carrying all six values as attributes. Nothing is written to a stream,
    /// and the values are copied, so later edits to the element do not affect this instance.
    /// <para>新建的内存 <c>file</c> 元素，六个值均作为属性写入。不会写入任何流，且值已被复制，因此后续修改该元素
    /// 不会影响本实例。</para>
    /// </returns>
    public XElement ToXmlElement()
    {
        return new XElement("file",
            new XAttribute("Name", Name),
            new XAttribute("OriginalLength", OriginalLength),
            new XAttribute("Length", Length),
            new XAttribute("FileSha256Hash", FileSha256Hash),
            new XAttribute("KeyIndex", KeyIndex),
            new XAttribute("Offset", Offset)
        );
    }
}