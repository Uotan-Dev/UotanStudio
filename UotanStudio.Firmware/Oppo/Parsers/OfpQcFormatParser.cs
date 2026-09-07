using System.Text;
using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Common.Streams;

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
            var tailPage = OfpQcParser.FindTailPage(stream, fileSize)
                ?? throw new InvalidOppFormatException("Cannot find valid OFP QC tail page.");
            var (xml, keyId) = ReadXmlContent(stream, tailPage, fileSize);
            var entries = xml is null ? [] : OfpQcParser.ParseEntries(xml, tailPage.PageSize);
            return new OppMetadata
            {
                FormatName = "OFP QC",
                Format = OppFormat.OfpQc,
                FileSize = fileSize,
                EntryCount = entries.Count,
                KeyId = keyId,
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
            if (tailPage is null)
                return [];
            var (xml, _) = ReadXmlContent(stream, tailPage, fileSize);
            return xml is null ? [] : OfpQcParser.ParseEntries(xml, tailPage.PageSize);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private (string? Xml, string? KeyId) ReadXmlContent(Stream stream, OfpQcTailPage tailPage, long fileSize)
    {
        var data = OfpQcParser.ReadXmlData(stream, tailPage, fileSize);
        if (OfpQcParser.IsPlaintextXml(data))
            return (Encoding.UTF8.GetString(data), "plaintext");
        var result = _cryptoProvider.AutoDecryptQcXml(data);
        return result is null ? (null, null) : (result.Value.Xml, result.Value.KeyId);
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
        FileExtraction.ValidateRange(entry.Offset, entry.Size, stream.Length, "OFP entry");
        if (entry.EncryptedSize < 0 || entry.EncryptedSize > entry.Size)
            throw new InvalidOppFormatException($"Invalid encrypted length for '{entry.Name}'.");
        var originalPosition = stream.Position;
        try
        {
            stream.Position = entry.Offset;
            if (entry.EncryptedSize > 0 && keyId != "plaintext")
            {
                if (string.IsNullOrEmpty(keyId))
                    throw new OppDecryptionException($"No key is available for encrypted entry '{entry.Name}'.");
                using var encrypted = new BoundedReadStream(stream, entry.Offset, entry.EncryptedSize, leaveOpen: true);
                using var counted = new CountingWriteStream(output, entry.EncryptedSize, leaveOpen: true);
                if (cryptoProvider is not null)
                    cryptoProvider.Decrypt(encrypted, counted, keyId);
                else
                    _cryptoProvider.Decrypt(encrypted, counted, keyId, CancellationToken.None);
                if (counted.BytesWritten != entry.EncryptedSize || encrypted.Position != encrypted.Length)
                    throw new InvalidOppFormatException("OFP decryption did not consume and produce the declared length.");
                stream.Position = checked(entry.Offset + entry.EncryptedSize);
                StreamCopy.CopyExactly(stream, output, entry.Size - entry.EncryptedSize);
            }
            else
            {
                StreamCopy.CopyExactly(stream, output, entry.Size);
            }
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}