using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;

internal sealed class OpsFormatParser : IOppFormatParser
{
    private const int DefaultBufferSize = 81920;

    public OppFormat SupportedFormat => OppFormat.Ops;

    /// <summary>
    /// Parses metadata from a stream containing an OPS format firmware file.
    /// <para>从流中解析 OPS 格式固件文件的元数据。</para>
    /// </summary>
    public OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);
        return OppOpsParser.ParseMetadata(stream, fileSize);
    }

    /// <summary>
    /// Parses the entry list from a stream containing an OPS format firmware file.
    /// <para>从流中解析 OPS 格式固件文件的条目列表。</para>
    /// </summary>
    public IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var tailPage = OppOpsParser.ReadTailPage(stream, fileSize);
        var decryptResult = OppOpsParser.TryDecryptXml(stream, tailPage);
        return decryptResult == null ? [] : OppOpsParser.ParseEntries(decryptResult.Value.Xml);
    }

    /// <summary>
    /// Extracts a single OPS format entry to an output stream.
    /// <para>提取单个 OPS 格式条目到输出流。</para>
    /// </summary>
    public void ExtractEntry(Stream stream, Stream output, OppEntry entry, OppMetadata metadata, string? keyId, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);
        Guard.ThrowIfNull(output);

        if (entry.Size <= 0)
        {
            return;
        }

        stream.Position = entry.Offset;

        var partitionType = entry.PartitionType;

        if (string.Equals(partitionType, "sahara", StringComparison.OrdinalIgnoreCase))
        {

            DecryptAndCopy(stream, output, entry.Size, keyId ?? metadata.KeyId, cryptoProvider);
            return;
        }

        if (string.Equals(partitionType, "ufs_provision", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(partitionType, "program", StringComparison.OrdinalIgnoreCase))
        {

            CopyStream(stream, output, entry.Size);
            return;
        }


        CopyStream(stream, output, entry.Size);
    }

    private static void DecryptAndCopy(Stream input, Stream output, long encryptedSize, string? keyId, IOppCryptoProvider? cryptoProvider)
    {
        try
        {

            if (string.IsNullOrEmpty(keyId) || string.Equals(keyId, "plaintext", StringComparison.OrdinalIgnoreCase))
            {
                CopyStream(input, output, encryptedSize);
                return;
            }

            if (cryptoProvider != null)
            {

                var subStream = new LengthLimitedReadStream(input, encryptedSize, leaveOpen: true);
                cryptoProvider.Decrypt(subStream, output, keyId);
            }
            else
            {

                var opsProvider = new OpsFormatCryptoProvider();
                var subStream = new LengthLimitedReadStream(input, encryptedSize, leaveOpen: true);
                opsProvider.Decrypt(subStream, output, keyId, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OppDecryptionException)
        {
            throw new OppDecryptionException("Failed to decrypt OPS entry data.", ex);
        }
    }

    private static void CopyStream(Stream input, Stream output, long count)
    {
        var buffer = new byte[DefaultBufferSize];
        var remaining = count;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var bytesRead = input.Read(buffer, 0, toRead);
            if (bytesRead <= 0)
            {
                break;
            }

            output.Write(buffer, 0, bytesRead);
            remaining -= bytesRead;
        }
    }
}