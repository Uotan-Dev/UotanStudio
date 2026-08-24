using System.Xml;
using System.Xml.Linq;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

/// <summary>
/// <para>OFP QC 文件解析器，提供纯解析功能（不包含加密操作）。</para>
/// OFP QC file parser providing pure parsing functionality (no crypto operations).
/// </summary>
internal static class OfpQcParser
{
    /// <summary>
    /// <para>从解密的 XML 内容中解析 OFP QC 条目。</para>
    /// Parses OFP QC entries from decrypted XML content.
    /// </summary>
    /// <param name="xmlContent"><para>解密后的 XML 字符串。</para>Decrypted XML string.</param>
    /// <param name="pageSize"><para>页面大小。</para>Page size.</param>
    /// <returns><para>解析后的条目列表。</para>The parsed entry list.</returns>
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
    /// <para>在文件流中查找有效的 OFP QC 尾部页。</para>
    /// Finds a valid OFP QC tail page in the file stream.
    /// </summary>
    /// <param name="stream"><para>可读取的文件流。</para>A readable file stream.</param>
    /// <param name="fileSize"><para>文件大小（字节）。</para>File size in bytes.</param>
    /// <returns><para>尾部页结构，若未找到返回 null。</para>The tail page structure, or null if not found.</returns>
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
    /// <para>从文件流中读取加密的 XML 数据。</para>
    /// Reads encrypted XML data from the file stream.
    /// </summary>
    /// <param name="stream"><para>可读取的文件流。</para>A readable file stream.</param>
    /// <param name="tailPage"><para>尾部页结构。</para>The tail page structure.</param>
    /// <param name="pageSize"><para>页面大小。</para>Page size.</param>
    /// <param name="fileSize"><para>文件大小（字节），用于 A57 hack 长度修正。</para>File size in bytes, used for A57 hack length correction.</param>
    /// <returns><para>加密的 XML 字节数据，若无法读取则返回 null。</para>The encrypted XML byte data, or null if unable to read.</returns>
    internal static byte[]? ReadEncryptedXml(Stream stream, OfpQcTailPage tailPage, int pageSize, long fileSize)
    {
        var xmlOffset = tailPage.XmlSectorOffset * pageSize;
        if (tailPage.XmlLength > int.MaxValue)
        {
            return null;
        }

        var xmlLength = (int)tailPage.XmlLength;

        // A57 hack: when xmlLength < 200, recalculate the actual XML length
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

        // Check if the data is already plaintext XML
        var header = new ReadOnlySpan<byte>(xmlData, 0, Math.Min(5, xmlData.Length));
        if (header.Length >= 5 && header[0] == '<' && header[1] == '?' && header[2] == 'x' && header[3] == 'm' && header[4] == 'l')
        {
            return null; // Already plaintext, caller should handle this
        }

        return xmlData;
    }

    /// <summary>
    /// <para>检查数据是否为明文 XML。</para>
    /// Checks whether the data is plaintext XML.
    /// </summary>
    /// <param name="data"><para>要检查的数据。</para>The data to check.</param>
    /// <returns><para>如果是明文 XML 返回 true；否则返回 false。</para>True if the data is plaintext XML; otherwise, false.</returns>
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