using System.Text;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.VendorFirmware.NTPi.Writer;

internal static class NtpiXmlGenerator
{
    /// <summary>
    /// Generates FileIndex XML data from the file entry list.
    /// <para>从文件条目列表生成 FileIndex XML 数据。</para>
    /// </summary>
    /// <param name="entries">
    /// Entries written in iteration order, one <c>file</c> element each with the name, both lengths, the SHA-256 hex, the
    /// key index and the offset as attributes; an empty sequence produces a document with no <c>file</c> elements.
    /// <para>按迭代顺序写入的条目，每个生成一个 <c>file</c> 元素，其名称、两个长度、SHA-256 十六进制、密钥索引与偏移作为属性；空序列得到不含 <c>file</c> 元素的文档。</para>
    /// </param>
    /// <returns>
    /// UTF-8 bytes of a <c>root</c> element without an XML declaration or indentation; numeric attributes use the
    /// current culture's number formatting.
    /// <para>不含 XML 声明与缩进的 <c>root</c> 元素的 UTF-8 字节；数值属性按当前区域的数字格式写入。</para>
    /// </returns>
    public static byte[] GenerateFileIndexXml(IEnumerable<FileEntry> entries)
    {
        using var stream = new MemoryStream(4096);
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = true,
            ConformanceLevel = System.Xml.ConformanceLevel.Auto
        };

        using (var writer = System.Xml.XmlWriter.Create(stream, settings))
        {
            writer.WriteStartElement("root");
            foreach (var entry in entries)
            {
                writer.WriteStartElement("file");
                writer.WriteAttributeString("Name", entry.Name);
                writer.WriteAttributeString("OriginalLength", entry.OriginalLength.ToString());
                writer.WriteAttributeString("Length", entry.Length.ToString());
                writer.WriteAttributeString("FileSha256Hash", entry.FileSha256Hash);
                writer.WriteAttributeString("KeyIndex", entry.KeyIndex.ToString());
                writer.WriteAttributeString("Offset", entry.Offset.ToString());
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Generates minimal XML data.
    /// <para>生成最小化的 XML 数据。</para>
    /// </summary>
    /// <param name="rootElement">
    /// Name of the single empty element to emit. <para>要输出的唯一空元素的名称。</para>
    /// </param>
    /// <returns>
    /// UTF-8 bytes holding only that start and end tag, without an XML declaration, indentation or child elements.
    /// <para>仅含该元素起始与结束标签的 UTF-8 字节，不含 XML 声明、缩进或子元素。</para>
    /// </returns>
    public static byte[] GenerateMinimalXml(string rootElement)
    {
        using var stream = new MemoryStream(64);
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = true,
            ConformanceLevel = System.Xml.ConformanceLevel.Auto
        };

        using (var writer = System.Xml.XmlWriter.Create(stream, settings))
        {
            writer.WriteStartElement(rootElement);
            writer.WriteEndElement();
        }

        return stream.ToArray();
    }
}