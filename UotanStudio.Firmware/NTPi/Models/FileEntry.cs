using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>表示 NTPI 固件包中文件索引条目的类，包含文件名、大小、哈希和加密信息。</para>
/// Represents a file index entry within an NTPI firmware package, containing file name, size, hash, and encryption information.
/// </summary>
public class FileEntry
{
    /// <summary>
    /// <para>文件名称。</para>
    /// The file name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// <para>文件原始长度（解密解压后），以字节为单位。</para>
    /// The original length of the file (after decryption and decompression) in bytes.
    /// </summary>
    public long OriginalLength { get; init; }

    /// <summary>
    /// <para>文件在区域中的长度（加密/压缩后），以字节为单位。</para>
    /// The length of the file in the region (encrypted/compressed) in bytes.
    /// </summary>
    public long Length { get; init; }

    /// <summary>
    /// <para>文件的 SHA-256 哈希值（十六进制字符串）。</para>
    /// The SHA-256 hash of the file as a hexadecimal string.
    /// </summary>
    public string FileSha256Hash { get; init; } = string.Empty;

    /// <summary>
    /// <para>加密密钥在密钥映射中的索引。</para>
    /// The index of the encryption key in the key map.
    /// </summary>
    public int KeyIndex { get; init; }

    /// <summary>
    /// <para>文件数据在区域中的偏移量（以字节为单位）。</para>
    /// The offset of the file data within the region in bytes.
    /// </summary>
    public long Offset { get; init; }

    /// <summary>
    /// <para>从 XML 元素解析并创建 <see cref="FileEntry"/> 实例。</para>
    /// Parses an XML element and creates a <see cref="FileEntry"/> instance.
    /// </summary>
    /// <param name="element">
    /// <para>包含文件条目属性的 XML 元素。</para>
    /// The XML element containing file entry attributes.
    /// </param>
    /// <returns>
    /// <para>从 XML 元素解析得到的 <see cref="FileEntry"/> 实例。</para>
    /// A <see cref="FileEntry"/> instance parsed from the XML element.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <para>当 <paramref name="element"/> 为 null 时抛出。</para>
    /// Thrown when <paramref name="element"/> is null.
    /// </exception>
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
    /// <para>从 XmlReader 当前位置读取属性并创建 <see cref="FileEntry"/> 实例，避免 XDocument 中间分配。</para>
    /// Reads attributes from the current XmlReader position and creates a <see cref="FileEntry"/> instance, avoiding XDocument intermediate allocations.
    /// </summary>
    /// <param name="reader">
    /// <para>定位在 file 元素上的 XML 读取器。</para>
    /// The XML reader positioned on a file element.
    /// </param>
    /// <returns>
    /// <para>从 XML 属性解析得到的 <see cref="FileEntry"/> 实例。</para>
    /// A <see cref="FileEntry"/> instance parsed from the XML attributes.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <para>当 <paramref name="reader"/> 为 null 时抛出。</para>
    /// Thrown when <paramref name="reader"/> is null.
    /// </exception>
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
    /// <para>将当前 <see cref="FileEntry"/> 实例转换为 XML 元素。</para>
    /// Converts the current <see cref="FileEntry"/> instance to an XML element.
    /// </summary>
    /// <returns>
    /// <para>包含文件条目属性的 <see cref="XElement"/> 实例。</para>
    /// An <see cref="XElement"/> instance containing the file entry attributes.
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