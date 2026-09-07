using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Writer;

internal static class NtpiXmlGenerator
{
    /// <summary>
    /// Generates FileIndex XML data from the file entry list.
    /// <para>从文件条目列表生成 FileIndex XML 数据。</para>
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
    /// Generates minimal XML data.
    /// <para>生成最小化的 XML 数据。</para>
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
