using System.Text;
using System.Xml;
using System.Xml.Linq;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Parsers;

internal static class OppOpsParser
{
    private const int OpsSectorSize = 0x200;

    /// <summary>
    /// Parses metadata from a stream containing an OPS format firmware file.
    /// <para>从指定流中解析 OPS 格式固件文件的元信息。</para>
    /// </summary>
    internal static OppMetadata ParseMetadata(Stream stream, long fileSize)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        try
        {
            var tailPage = ReadTailPage(stream, fileSize);


            var decryptResult = TryDecryptXml(stream, tailPage);

            if (decryptResult != null)
            {
                var entries = ParseEntries(decryptResult.Value.Xml);
                return new OppMetadata
                {
                    FormatName = "OPS",
                    Format = OppFormat.Ops,
                    FileSize = fileSize,
                    EntryCount = entries.Count,
                    KeyId = decryptResult.Value.KeyId,
                    ProjectName = tailPage.ProjectId,
                    ProjectId = tailPage.ProjectId,
                    FirmwareName = tailPage.FirmwareName,
                    IsZipWrapped = false
                };
            }


            return new OppMetadata
            {
                FormatName = "OPS",
                Format = OppFormat.Ops,
                FileSize = fileSize,
                EntryCount = 0,
                ProjectName = tailPage.ProjectId,
                ProjectId = tailPage.ProjectId,
                FirmwareName = tailPage.FirmwareName,
                IsZipWrapped = false
            };
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Reads the OPS tail configuration page from the file stream.
    /// <para>从文件流中读取 OPS 尾部配置页。</para>
    /// </summary>
    internal static OpsTailPage ReadTailPage(Stream stream, long fileSize)
    {
        Guard.ThrowIfNull(stream);

        if (fileSize < OpsTailPage.Size)
        {
            throw new InvalidOppFormatException("File too small for OPS tail page.");
        }

        stream.Position = fileSize - OpsTailPage.Size;
        var tailBuffer = new byte[OpsTailPage.Size];
        var bytesRead = stream.Read(tailBuffer, 0, OpsTailPage.Size);
        return bytesRead != OpsTailPage.Size
            ? throw new InvalidOppFormatException("Failed to read OPS tail page.")
            : OpsTailPage.Parse(tailBuffer);
    }

    /// <summary>
    /// Tries to decrypt the XML configuration data in the OPS firmware using auto key detection.
    /// <para>尝试使用自动密钥检测解密 OPS 固件中的 XML 配置数据。</para>
    /// </summary>
    internal static (string Xml, string KeyId)? TryDecryptXml(
        Stream stream,
        OpsTailPage tailPage)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        try
        {
            var xmlOffset = (long)tailPage.ConfigSectorPos * OpsTailPage.Size;
            if (tailPage.XmlLength > int.MaxValue)
            {
                return null;
            }

            var xmlLength = (int)tailPage.XmlLength;


            const int opsBlockSize = 16;
            var alignedLength = (xmlLength + opsBlockSize - 1) / opsBlockSize * opsBlockSize;


            var maxAvailable = (int)(stream.Length - xmlOffset);
            if (alignedLength > maxAvailable)
            {
                alignedLength = maxAvailable;
            }

            if (xmlOffset < 0 || xmlLength <= 0 || xmlOffset + alignedLength > stream.Length)
            {
                return null;
            }

            stream.Position = xmlOffset;
            var xmlData = new byte[alignedLength];
            var bytesRead = stream.Read(xmlData, 0, alignedLength);
            if (bytesRead != alignedLength)
            {
                return null;
            }


            var header = new ReadOnlySpan<byte>(xmlData, 0, Math.Min(5, xmlData.Length));
            if (header.Length >= 5 && header[0] == '<' && header[1] == '?' && header[2] == 'x' && header[3] == 'm' && header[4] == 'l')
            {
                return (Encoding.UTF8.GetString(xmlData), "plaintext");
            }


            var opsProvider = new OpsFormatCryptoProvider();
            return opsProvider.AutoDecryptOpsXml(xmlData);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Parses OPS entry list from decrypted XML content.
    /// <para>从解密的 XML 内容中解析 OPS 条目列表。</para>
    /// </summary>
    internal static IReadOnlyList<OppEntry> ParseEntries(string xml)
    {
        Guard.ThrowIfNullOrWhiteSpace(xml);

        var entries = new List<OppEntry>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
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
            if (child.Name.LocalName == "SAHARA")
            {
                ParseSaharaElements(child, entries);
            }
            else if (child.Name.LocalName == "UFS_PROVISION")
            {
                ParseUfsProvisionElements(child, entries);
            }
            else if (child.Name.LocalName.Contains("Program"))
            {
                ParseProgramElements(child, entries);
            }
        }

        return entries;
    }

    private static void ParseSaharaElements(XElement saharaElement, List<OppEntry> entries)
    {
        foreach (var item in saharaElement.Elements("File"))
        {
            var path = item.Attribute("Path")?.Value;
            var fileOffsetStr = item.Attribute("FileOffsetInSrc")?.Value;
            var sizeInByteStr = item.Attribute("SizeInByteInSrc")?.Value;
            var sizeInSectorStr = item.Attribute("SizeInSectorInSrc")?.Value;

            var sectorOffset = ParseInt64(fileOffsetStr);
            var byteSize = ParseInt64(sizeInByteStr);
            var sectorSize = ParseInt64(sizeInSectorStr);

            var name = !string.IsNullOrWhiteSpace(path) ? path : $"sahara_sector_{sectorOffset}";
            var offset = sectorOffset * OpsSectorSize;
            var size = byteSize > 0 ? byteSize : sectorSize * OpsSectorSize;

            entries.Add(new OppEntry
            {
                Name = name,
                Offset = offset,
                Size = size,
                EncryptedSize = byteSize,
                IsCompressed = false,
                PartitionType = "sahara",
                SectorOffset = sectorOffset,
                SectorSize = sectorSize
            });
        }
    }

    private static void ParseUfsProvisionElements(XElement ufsElement, List<OppEntry> entries)
    {
        foreach (var item in ufsElement.Elements("File"))
        {
            var path = item.Attribute("Path")?.Value;
            var fileOffsetStr = item.Attribute("FileOffsetInSrc")?.Value;
            var sizeInByteStr = item.Attribute("SizeInByteInSrc")?.Value;

            var sectorOffset = ParseInt64(fileOffsetStr);
            var byteSize = ParseInt64(sizeInByteStr);

            var name = !string.IsNullOrWhiteSpace(path) ? path : $"ufs_provision_sector_{sectorOffset}";
            var offset = sectorOffset * OpsSectorSize;

            entries.Add(new OppEntry
            {
                Name = name,
                Offset = offset,
                Size = byteSize,
                EncryptedSize = 0,
                IsCompressed = false,
                PartitionType = "ufs_provision",
                SectorOffset = sectorOffset,
                SectorSize = 0
            });
        }
    }

    private static void ParseProgramElements(XElement programParent, List<OppEntry> entries)
    {
        foreach (var item in programParent.Elements())
        {
            if (item.Attribute("filename") != null)
            {
                AddProgramEntry(item, entries, parentLabel: null);
            }
            else
            {

                var parentLabel = item.Attribute("label")?.Value;
                foreach (var subitem in item.Elements())
                {
                    if (subitem.Attribute("filename") != null)
                    {
                        AddProgramEntry(subitem, entries, parentLabel);
                    }
                }
            }
        }
    }

    private static void AddProgramEntry(XElement element, List<OppEntry> entries, string? parentLabel)
    {
        var filename = element.Attribute("filename")?.Value;
        var fileOffsetStr = element.Attribute("FileOffsetInSrc")?.Value;
        var sizeInByteStr = element.Attribute("SizeInByteInSrc")?.Value;
        var sizeInSectorStr = element.Attribute("SizeInSectorInSrc")?.Value;
        var sha256 = element.Attribute("Sha256")?.Value;
        var sparseStr = element.Attribute("sparse")?.Value;

        var sectorOffset = ParseInt64(fileOffsetStr);
        var byteSize = ParseInt64(sizeInByteStr);
        var sectorSize = ParseInt64(sizeInSectorStr);
        var isSparse = string.Equals(sparseStr, "true", StringComparison.OrdinalIgnoreCase);

        var name = !string.IsNullOrWhiteSpace(filename) ? filename
            : !string.IsNullOrWhiteSpace(parentLabel) ? parentLabel
            : $"program_sector_{sectorOffset}";
        var offset = sectorOffset * OpsSectorSize;
        var size = byteSize > 0 ? byteSize : sectorSize * OpsSectorSize;

        entries.Add(new OppEntry
        {
            Name = name,
            Offset = offset,
            Size = size,
            EncryptedSize = 0,
            IsCompressed = false,
            PartitionType = "program",
            Sha256 = sha256,
            IsSparse = isSparse,
            SectorOffset = sectorOffset,
            SectorSize = sectorSize
        });
    }

    private static long ParseInt64(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;
}