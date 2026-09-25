using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Parsers;

/// <summary>
/// Reads one Oppo firmware container generation: metadata, entry list and per-entry extraction.
/// <para>读取一种 Oppo 固件容器：元数据、条目列表与逐条目提取。</para>
/// </summary>
public interface IOppFormatParser
{
    /// <summary>The container format this parser handles, used as its registration key. <para>本解析器处理的容器格式，用作其注册键。</para></summary>
    /// <value>The single <see cref="OppFormat"/> this parser reads. <para>本解析器读取的唯一 <see cref="OppFormat"/>。</para></value>
    OppFormat SupportedFormat { get; }

    /// <summary>
    /// Parses metadata from a stream.
    /// <para>从流中解析元数据。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable, seekable firmware stream owned by the caller; the parser must not close it.
    /// <para>由调用方持有的可读、可寻址固件流；解析器不得将其关闭。</para>
    /// </param>
    /// <param name="fileSize">
    /// Total byte length of the container the stream represents.
    /// <para>流所表示容器的总字节长度。</para>
    /// </param>
    /// <param name="cryptoProvider">
    /// Provider used to decrypt protected regions, or <see langword="null"/> when the archive needs no decryption.
    /// <para>用于解密受保护区域的提供者；归档无需解密时为 <see langword="null"/>。</para>
    /// </param>
    /// <returns>The parsed metadata describing the container.
    /// <para>描述该容器的已解析元数据。</para></returns>
    OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider);

    /// <summary>
    /// Parses the entry list from a stream.
    /// <para>从流中解析条目列表。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable, seekable firmware stream owned by the caller; the parser must not close it.
    /// <para>由调用方持有的可读、可寻址固件流；解析器不得将其关闭。</para>
    /// </param>
    /// <param name="fileSize">
    /// Total byte length of the container the stream represents.
    /// <para>流所表示容器的总字节长度。</para>
    /// </param>
    /// <param name="metadata">
    /// Metadata for the same container, supplying the layout the entries are read against.
    /// <para>同一容器的元数据，提供读取条目所依据的布局。</para>
    /// </param>
    /// <param name="cryptoProvider">
    /// Provider used to decrypt protected regions, or <see langword="null"/> when the archive needs no decryption.
    /// <para>用于解密受保护区域的提供者；归档无需解密时为 <see langword="null"/>。</para>
    /// </param>
    /// <returns>Every entry declared by the archive, in container order.
    /// <para>归档声明的每个条目，按容器中的顺序排列。</para></returns>
    IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider);

    /// <summary>
    /// Extracts a single entry to an output stream.
    /// <para>提取单个条目到输出流。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable, seekable firmware stream positioned so the entry can be located; owned by the caller.
    /// <para>可读、可寻址的固件流，定位到能够找到该条目的位置；由调用方持有。</para>
    /// </param>
    /// <param name="output">
    /// Writable stream that receives the decrypted, decompressed entry bytes; owned by the caller.
    /// <para>接收已解密、已解压条目数据的可写流；由调用方持有。</para>
    /// </param>
    /// <param name="entry">
    /// The entry descriptor identifying which region of the archive to read.
    /// <para>标识应读取归档中哪个区域的目标条目描述符。</para>
    /// </param>
    /// <param name="metadata">
    /// Metadata for the containing archive, supplying format-specific decode parameters.
    /// <para>所在归档的元数据，提供与格式相关的解码参数。</para>
    /// </param>
    /// <param name="keyId">
    /// Identifier of the key to decrypt the entry, or <see langword="null"/> to let the provider infer it.
    /// <para>用于解密该条目的密钥标识符；为 <see langword="null"/> 时由提供者自行推断。</para>
    /// </param>
    /// <param name="cryptoProvider">
    /// Provider used to decrypt protected regions, or <see langword="null"/> when the entry is stored in the clear.
    /// <para>用于解密受保护区域的提供者；条目以明文存储时为 <see langword="null"/>。</para>
    /// </param>
    void ExtractEntry(Stream stream, Stream output, OppEntry entry, OppMetadata metadata, string? keyId, IOppCryptoProvider? cryptoProvider);
}