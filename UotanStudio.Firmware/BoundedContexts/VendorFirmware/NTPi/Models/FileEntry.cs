using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

public class FileEntry
{
    public string Name { get; init; } = string.Empty;

    public long OriginalLength { get; init; }

    public long Length { get; init; }

    public string FileSha256Hash { get; init; } = string.Empty;

    public int KeyIndex { get; init; }

    public long Offset { get; init; }

    /// <summary>
    /// Parses an XML element and creates a FileEntry instance.
    /// <para>从 XML 元素解析并创建 FileEntry 实例。</para>
    /// </summary>
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