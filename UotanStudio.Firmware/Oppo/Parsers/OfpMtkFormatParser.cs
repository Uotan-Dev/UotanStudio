using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Common.Streams;

namespace UotanStudio.Firmware.Oppo.Parsers;

internal sealed class OfpMtkFormatParser : IOppFormatParser
{
    private readonly OfpMtkCryptoProvider _cryptoProvider;

    /// <summary>
    /// Initializes a new instance of the OfpMtkFormatParser class.
    /// <para>初始化 OfpMtkFormatParser 类的新实例。</para>
    /// </summary>
    public OfpMtkFormatParser(OfpMtkCryptoProvider cryptoProvider)
    {
        Guard.ThrowIfNull(cryptoProvider);
        _cryptoProvider = cryptoProvider;
    }

    public OppFormat SupportedFormat => OppFormat.OfpMtk;

    /// <summary>
    /// Parses OFP MTK metadata from a stream.
    /// <para>从流中解析 OFP MTK 元数据。</para>
    /// </summary>
    public OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        try
        {
            var header = OfpMtkParser.ReadMainHeader(stream, fileSize);
            var entryCount = header.FileTableEntryCount;


            string? keyId = null;
            if (stream.Length >= 16)
            {
                stream.Position = 0;
                var firstBytes = new byte[16];
                var bytesRead = stream.Read(firstBytes, 0, 16);
                if (bytesRead == 16)
                {
                    var mtkKey = _cryptoProvider.AutoDetectMtkKey(firstBytes);
                    if (mtkKey != null)
                    {
                        keyId = mtkKey.KeyId;
                    }
                }
            }

            return new OppMetadata
            {
                FormatName = "OFP MTK",
                Format = OppFormat.OfpMtk,
                FileSize = fileSize,
                EntryCount = entryCount,
                ProjectName = header.ProjectName,
                CpuModel = header.CpuModel,
                FlashType = header.FlashType,
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
    /// Parses OFP MTK entry list from a stream.
    /// <para>从流中解析 OFP MTK 条目列表。</para>
    /// </summary>
    public IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        return OfpMtkParser.ParseEntries(stream, fileSize, metadata.EntryCount);
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