using System.Text;
using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

internal sealed class OfpQcFormatParser : IOppFormatParser
{
    private readonly OfpQcCryptoProvider _cryptoProvider;

    /// <summary>
    /// Initializes a new instance of the OfpQcFormatParser class.
    /// <para>初始化 OfpQcFormatParser 类的新实例。</para>
    /// </summary>
    public OfpQcFormatParser(OfpQcCryptoProvider cryptoProvider)
    {
        Guard.ThrowIfNull(cryptoProvider);
        _cryptoProvider = cryptoProvider;
    }

    public OppFormat SupportedFormat => OppFormat.OfpQc;

    /// <summary>
    /// Parses OFP QC metadata from a stream.
    /// <para>从流中解析 OFP QC 元数据。</para>
    /// </summary>
    public OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        try
        {
            var tailPage = OfpQcParser.FindTailPage(stream, fileSize) ?? throw new InvalidOppFormatException("Cannot find valid OFP QC tail page.");


            var xmlData = OfpQcParser.ReadEncryptedXml(stream, tailPage, tailPage.PageSize, fileSize);

            if (xmlData != null)
            {
                var decryptResult = _cryptoProvider.AutoDecryptQcXml(xmlData);

                if (decryptResult != null)
                {
                    var entries = OfpQcParser.ParseEntries(decryptResult.Value.Xml, tailPage.PageSize);
                    return new OppMetadata
                    {
                        FormatName = "OFP QC",
                        Format = OppFormat.OfpQc,
                        FileSize = fileSize,
                        EntryCount = entries.Count,
                        KeyId = decryptResult.Value.KeyId,
                        IsZipWrapped = false
                    };
                }
            }
            else
            {

                var plainXmlOffset = tailPage.XmlSectorOffset * tailPage.PageSize;
                if (plainXmlOffset >= 0 && plainXmlOffset < stream.Length)
                {
                    stream.Position = plainXmlOffset;
                    var xmlLength = (int)tailPage.XmlLength;
                    if (xmlLength < 200)
                    {
                        var tailPageOffset = fileSize - tailPage.PageSize;
                        xmlLength = (int)(tailPageOffset - plainXmlOffset - 0x57);
                    }

                    if (xmlLength > 0 && plainXmlOffset + xmlLength <= stream.Length)
                    {
                        var plainData = new byte[xmlLength];
                        var bytesRead = stream.Read(plainData, 0, xmlLength);
                        if (bytesRead == xmlLength && OfpQcParser.IsPlaintextXml(plainData))
                        {
                            var xmlText = Encoding.UTF8.GetString(plainData);
                            var entries = OfpQcParser.ParseEntries(xmlText, tailPage.PageSize);
                            return new OppMetadata
                            {
                                FormatName = "OFP QC",
                                Format = OppFormat.OfpQc,
                                FileSize = fileSize,
                                EntryCount = entries.Count,
                                KeyId = "plaintext",
                                IsZipWrapped = false
                            };
                        }
                    }
                }
            }


            return new OppMetadata
            {
                FormatName = "OFP QC",
                Format = OppFormat.OfpQc,
                FileSize = fileSize,
                EntryCount = 0,
                IsZipWrapped = false
            };
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Parses OFP QC entry list from a stream.
    /// <para>从流中解析 OFP QC 条目列表。</para>
    /// </summary>
    public IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        try
        {
            var tailPage = OfpQcParser.FindTailPage(stream, fileSize);
            if (tailPage == null)
            {
                return [];
            }

            var xmlData = OfpQcParser.ReadEncryptedXml(stream, tailPage, tailPage.PageSize, fileSize);

            string? xmlContent = null;

            if (xmlData != null)
            {
                var decryptResult = _cryptoProvider.AutoDecryptQcXml(xmlData);
                if (decryptResult != null)
                {
                    xmlContent = decryptResult.Value.Xml;
                }
            }
            else
            {

                var plainXmlOffset = tailPage.XmlSectorOffset * tailPage.PageSize;
                if (plainXmlOffset >= 0 && plainXmlOffset < stream.Length)
                {
                    stream.Position = plainXmlOffset;
                    var xmlLength = (int)tailPage.XmlLength;
                    if (xmlLength < 200)
                    {
                        var tailPageOffset = fileSize - tailPage.PageSize;
                        xmlLength = (int)(tailPageOffset - plainXmlOffset - 0x57);
                    }

                    if (xmlLength > 0 && plainXmlOffset + xmlLength <= stream.Length)
                    {
                        var plainData = new byte[xmlLength];
                        var bytesRead = stream.Read(plainData, 0, xmlLength);
                        if (bytesRead == xmlLength && OfpQcParser.IsPlaintextXml(plainData))
                        {
                            xmlContent = Encoding.UTF8.GetString(plainData);
                        }
                    }
                }
            }

            return xmlContent != null ? OfpQcParser.ParseEntries(xmlContent, tailPage.PageSize) : [];
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Extracts a single entry to an output stream.
    /// <para>提取单个条目到输出流。</para>
    /// </summary>
    public void ExtractEntry(Stream stream, Stream output, OppEntry entry, OppMetadata metadata, string? keyId, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);
        Guard.ThrowIfNull(output);
        Guard.ThrowIfNull(entry);

        var originalPosition = stream.Position;
        try
        {
            stream.Position = entry.Offset;

            if (entry.EncryptedSize > 0 && !string.IsNullOrEmpty(keyId) && keyId != "plaintext")
            {

                var encryptedBytes = new byte[entry.EncryptedSize];
                var bytesRead = stream.Read(encryptedBytes, 0, (int)entry.EncryptedSize);
                if (bytesRead != (int)entry.EncryptedSize)
                {
                    throw new InvalidOppFormatException("Failed to read encrypted entry data.");
                }


                var decrypted = _cryptoProvider.Decrypt(encryptedBytes, keyId);
                output.Write(decrypted, 0, decrypted.Length);


                var remaining = entry.Size - entry.EncryptedSize;
                if (remaining > 0)
                {
                    CopyRange(stream, output, remaining);
                }
            }
            else
            {

                CopyRange(stream, output, entry.Size);
            }
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static void CopyRange(Stream stream, Stream output, long count)
    {
        var buffer = new byte[81920];
        var leftToRead = count;
        while (leftToRead > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, leftToRead);
            var read = stream.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            leftToRead -= read;
        }
    }
}
