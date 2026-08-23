using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

/// <summary>
/// <para>定义特定格式的固件解析操作接口，由各格式解析器实现。</para>
/// Defines format-specific firmware parsing operations, implemented by each format parser.
/// </summary>
public interface IOppFormatParser
{
    /// <summary>
    /// <para>获取此解析器支持的固件格式。</para>
    /// Gets the firmware format supported by this parser.
    /// </summary>
    OppFormat SupportedFormat { get; }

    /// <summary>
    /// <para>从流中解析元数据。</para>
    /// Parses metadata from a stream.
    /// </summary>
    OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider);

    /// <summary>
    /// <para>从流中解析条目列表。</para>
    /// Parses the entry list from a stream.
    /// </summary>
    IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider);

    /// <summary>
    /// <para>提取单个条目到输出流。</para>
    /// Extracts a single entry to an output stream.
    /// </summary>
    void ExtractEntry(Stream stream, Stream output, OppEntry entry, OppMetadata metadata, string? keyId, IOppCryptoProvider? cryptoProvider);
}