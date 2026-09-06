using System.Xml;
using System.Xml.Linq;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

internal static class OfpQcParser
{
    /// <summary>
    /// Parses OFP QC entries from decrypted XML content.
    /// <para>从解密的 XML 内容中解析 OFP QC 条目。</para>
    /// </summary>
    internal static IReadOnlyList<OppEntry> ParseEntries(string xmlContent, int pageSize)
    {
        Guard.ThrowIfNullOrWhiteSpace(xmlContent);

        var entries = new List<OppEntry>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlContent);
        }
        catch (XmlException ex)
        {
            throw new InvalidOppFormatException("Failed to parse decrypted XML content.", ex);
        }

        var root = doc.Root;
        if (root == null)
        {
            return entries;
        }

        foreach (var child in root.Elements())
        {
            var partitionType = GetPartitionType(child.Name.LocalName);

            foreach (var item in child.Elements())
            {
                var hasPath = item.Attribute("Path") != null || item.Attribute("filename") != null;

                if (!hasPath)
                {
                    foreach (var subItem in item.Elements())
                    {
                        var entry = ParseItemElement(subItem, pageSize, partitionType);
                        if (entry != null)
                        {
                            entries.Add(entry);
                        }
                    }
                }

                var itemEntry = ParseItemElement(item, pageSize, partitionType);
                if (itemEntry != null)
                {
                    entries.Add(itemEntry);
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Finds a valid OFP QC tail page in the file stream.
    /// <para>在文件流中查找有效的 OFP QC 尾部页。</para>
    /// </summary>
    internal static OfpQcTailPage? FindTailPage(Stream stream, long fileSize)
    {
        var pageSizes = new[] { 0x200, 0x1000 };

        foreach (var pageSize in pageSizes)
        {
            if (fileSize < pageSize)
            {
                continue;
            }

            stream.Position = fileSize - pageSize;
            var pageBuffer = new byte[pageSize];
            var bytesRead = stream.Read(pageBuffer, 0, pageSize);
            if (bytesRead != pageSize)
            {
                continue;
            }

            var pageData = new ReadOnlySpan<byte>(pageBuffer);
            if (OfpQcTailPage.ValidateMagic(pageData, 0x10))
            {
                return OfpQcTailPage.Parse(pageData, pageSize);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads encrypted XML data from the file stream.
    /// <para>从文件流中读取加密的 XML 数据。</para>
    /// </summary>
    internal static byte[]? ReadEncryptedXml(Stream stream, OfpQcTailPage tailPage, int pageSize, long fileSize)
    {
        var xmlOffset = tailPage.XmlSectorOffset * pageSize;
        if (tailPage.XmlLength > int.MaxValue)
        {
            return null;
        }

        var xmlLength = (int)tailPage.XmlLength;


        if (xmlLength < 200)
        {
            var tailPageOffset = fileSize - tailPage.PageSize;
            xmlLength = (int)(tailPageOffset - xmlOffset - 0x57);
        }

        if (xmlOffset < 0 || xmlLength <= 0 || xmlOffset + xmlLength > stream.Length)
        {
            return null;
        }

        stream.Position = xmlOffset;
        var xmlData = new byte[xmlLength];
        var bytesRead = stream.Read(xmlData, 0, xmlLength);
        if (bytesRead != xmlLength)
        {
            return null;
        }


        var header = new ReadOnlySpan<byte>(xmlData, 0, Math.Min(5, xmlData.Length));
        if (header.Length >= 5 && header[0] == '<' && header[1] == '?' && header[2] == 'x' && header[3] == 'm' && header[4] == 'l')
        {
            return null;
        }

        return xmlData;
    }

    /// <summary>
    /// Checks whether the data is plaintext XML.
    /// <para>检查数据是否为明文 XML。</para>
    /// </summary>
    internal static bool IsPlaintextXml(byte[] data) => data.Length >= 5 && data[0] == '<' && data[1] == '?' && data[2] == 'x' && data[3] == 'm' && data[4] == 'l';

    private static OppEntry? ParseItemElement(XElement element, int pageSize, string parentTag)
    {
        var (wfilename, start, length, rlength, checksums) = DecryptItem(element, pageSize);

        if (string.IsNullOrEmpty(wfilename) || start < 0)
        {
            return null;
        }

        var encryptedSize = ComputeEncryptedSize(parentTag, length, rlength);
        var isSparse = element.Attribute("sparse")?.Value is "1" or "true";

        return new OppEntry
        {
            Name = wfilename,
            Offset = start,
            Size = length,
            EncryptedSize = encryptedSize,
            IsCompressed = false,
            PartitionType = parentTag,
            Sha256 = checksums.Sha256,
            Md5 = checksums.Md5,
            SectorOffset = start / pageSize,
            SectorSize = rlength > 0 ? rlength / pageSize : 0,
            FileName = wfilename,
            IsSparse = isSparse
        };
    }

    private static (string FileName, long Start, long Length, long RealLength, (string? Sha256, string? Md5) Checksums) DecryptItem(XElement item, int pageSize)
    {
        var sha256sum = item.Attribute("sha256")?.Value;
        var md5sum = item.Attribute("md5")?.Value;
        var wfilename = item.Attribute("Path")?.Value ?? item.Attribute("filename")?.Value ?? string.Empty;

        long start = -1;
        var fileOffsetAttr = item.Attribute("FileOffsetInSrc")?.Value;
        var sizeInSectorAttr = item.Attribute("SizeInSectorInSrc")?.Value;

        if (long.TryParse(fileOffsetAttr, out var fileOffset))
        {
            start = fileOffset * pageSize;
        }
        else if (long.TryParse(sizeInSectorAttr, out var sectorOffset))
        {
            start = sectorOffset * pageSize;
        }

        long rlength = 0;
        var sizeInByteAttr = item.Attribute("SizeInByteInSrc")?.Value;
        if (long.TryParse(sizeInByteAttr, out var byteSize))
        {
            rlength = byteSize;
        }

        var length = long.TryParse(sizeInSectorAttr, out var sectorSize) ? sectorSize * pageSize : rlength;
        return (wfilename, start, length, rlength, (sha256sum, md5sum));
    }

    private static long ComputeEncryptedSize(string parentTag, long length, long realLength)
    {
        return parentTag switch
        {
            "digests_to_sign" or "chained_table_of_digests" or "firmware" => 0,
            "sahara" => realLength,
            "config" or "provision" => Math.Min(0x40000, realLength),
            _ => Math.Min(0x40000, length),
        };
    }

    private static string GetPartitionType(string tagName)
    {
        return tagName switch
        {
            "Sahara" => "sahara",
            "Config" => "config",
            "Provision" => "provision",
            "ChainedTableOfDigests" => "chained_table_of_digests",
            "DigestsToSign" => "digests_to_sign",
            "Firmware" => "firmware",
            _ => tagName.ToLowerInvariant()
        };
    }
}
