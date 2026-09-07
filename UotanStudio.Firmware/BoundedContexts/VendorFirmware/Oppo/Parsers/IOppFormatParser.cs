using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Parsers;

public interface IOppFormatParser
{
    OppFormat SupportedFormat { get; }

    /// <summary>
    /// Parses metadata from a stream.
    /// <para>从流中解析元数据。</para>
    /// </summary>
    OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider);

    /// <summary>
    /// Parses the entry list from a stream.
    /// <para>从流中解析条目列表。</para>
    /// </summary>
    IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider);

    /// <summary>
    /// Extracts a single entry to an output stream.
    /// <para>提取单个条目到输出流。</para>
    /// </summary>
    void ExtractEntry(Stream stream, Stream output, OppEntry entry, OppMetadata metadata, string? keyId, IOppCryptoProvider? cryptoProvider);
}
