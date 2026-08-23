using System.Text;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Writer;

/// <summary>
/// <para>提供 NTPI 打包过程中的 XML 数据生成功能,包括 FileIndex XML 和最小化 XML。</para>
/// Provides XML data generation functionality during NTPI packing, including FileIndex XML and minimal XML.
/// </summary>
internal static class NtpiXmlGenerator
{
    /// <summary>
    /// <para>从文件条目列表生成 FileIndex XML 数据。</para>
    /// Generates FileIndex XML data from the file entry list.
    /// </summary>
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
    /// <para>生成最小化的 XML 数据。</para>
    /// Generates minimal XML data.
    /// </summary>
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