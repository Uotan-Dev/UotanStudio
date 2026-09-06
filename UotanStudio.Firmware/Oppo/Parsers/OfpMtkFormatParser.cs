using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

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

        var originalPosition = stream.Position;
        try
        {
            stream.Position = entry.Offset;

            if (entry.EncryptedSize > 0 && !string.IsNullOrEmpty(keyId))
            {

                if (entry.EncryptedSize is > int.MaxValue or < 0)
                {
                    throw new InvalidOppFormatException($"Encrypted size {entry.EncryptedSize} is out of range for entry '{entry.Name}'.");
                }


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
