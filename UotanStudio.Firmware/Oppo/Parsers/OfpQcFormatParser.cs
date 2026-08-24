using System.Text;
using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

/// <summary>
/// <para>OFP QC 格式的解析器，实现 <see cref="IOppFormatParser"/> 接口。</para>
/// Parser for the OFP QC format, implementing the <see cref="IOppFormatParser"/> interface.
/// </summary>
internal sealed class OfpQcFormatParser : IOppFormatParser
{
    private readonly OfpQcCryptoProvider _cryptoProvider;

    /// <summary>
    /// <para>初始化 <see cref="OfpQcFormatParser"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="OfpQcFormatParser"/> class.
    /// </summary>
    /// <param name="cryptoProvider"><para>QC 加密提供者实例。</para>The QC crypto provider instance.</param>
    public OfpQcFormatParser(OfpQcCryptoProvider cryptoProvider)
    {
        Guard.ThrowIfNull(cryptoProvider);
        _cryptoProvider = cryptoProvider;
    }

    /// <summary>
    /// <para>获取此解析器支持的固件格式。</para>
    /// Gets the firmware format supported by this parser.
    /// </summary>
    public OppFormat SupportedFormat => OppFormat.OfpQc;

    /// <summary>
    /// <para>从流中解析 OFP QC 元数据。</para>
    /// Parses OFP QC metadata from a stream.
    /// </summary>
    public OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        try
        {
            var tailPage = OfpQcParser.FindTailPage(stream, fileSize) ?? throw new InvalidOppFormatException("Cannot find valid OFP QC tail page.");

            // Try to decrypt XML to get detailed metadata
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
                // Data might be plaintext XML - try reading directly
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

            // Fallback: return basic metadata without decrypted XML
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
    /// <para>从流中解析 OFP QC 条目列表。</para>
    /// Parses OFP QC entry list from a stream.
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
                // Try plaintext XML
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
    /// <para>提取单个条目到输出流。</para>
    /// Extracts a single entry to an output stream.
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
                // Read the encrypted portion
                var encryptedBytes = new byte[entry.EncryptedSize];
                var bytesRead = stream.Read(encryptedBytes, 0, (int)entry.EncryptedSize);
                if (bytesRead != (int)entry.EncryptedSize)
                {
                    throw new InvalidOppFormatException("Failed to read encrypted entry data.");
                }

                // Decrypt the encrypted portion
                var decrypted = _cryptoProvider.Decrypt(encryptedBytes, keyId);
                output.Write(decrypted, 0, decrypted.Length);

                // Copy the remaining unencrypted portion
                var remaining = entry.Size - entry.EncryptedSize;
                if (remaining > 0)
                {
                    CopyRange(stream, output, remaining);
                }
            }
            else
            {
                // No encryption, just copy
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