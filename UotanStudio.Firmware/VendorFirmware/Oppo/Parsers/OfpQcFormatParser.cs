using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;

internal sealed class OfpQcFormatParser : IOppFormatParser
{
    private readonly OfpQcCryptoProvider _cryptoProvider;

    /// <summary>
    /// Initializes a new instance of the OfpQcFormatParser class.
    /// <para>初始化 OfpQcFormatParser 类的新实例。</para>
    /// </summary>
    /// <param name="cryptoProvider">QC provider used to read the tail-page XML and to decrypt an entry when the caller
    /// supplies no provider of its own. <para>用于读取尾页 XML、以及在调用方未提供自己的提供者时解密条目的 QC 提供者。</para></param>
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
    /// <param name="stream">Readable, seekable OFP stream owned by the caller; the tail page is read from the end of
    /// <paramref name="fileSize"/> and the XML body from the sector offset the tail declares, after which the original
    /// position is restored. It is never closed.
    /// <para>调用方持有的可读、可寻址 OFP 流；尾页从 <paramref name="fileSize"/> 末尾读取，XML 主体从尾页声明的扇区偏移读取，
    /// 完成后恢复原位置，且不会关闭该流。</para></param>
    /// <param name="fileSize">Total container byte length defining where the tail page search starts; recorded as
    /// <see cref="OppMetadata.FileSize"/> and used to bound the XML read.
    /// <para>容器总字节长度，决定尾页搜索的起点；直接记入 <see cref="OppMetadata.FileSize"/>，并用于约束 XML 读取范围。</para></param>
    /// <param name="cryptoProvider">Not consulted; the embedded XML is decrypted by the QC provider given to the
    /// constructor. <para>本参数不被使用；内嵌 XML 由构造时传入的 QC 提供者解密。</para></param>
    /// <returns>Metadata naming the format <c>OFP QC</c> with the entry count parsed from the XML, the key identifier
    /// that decrypted it (or <c>plaintext</c> when no decryption was needed), <paramref name="fileSize"/>, and
    /// <see cref="OppMetadata.IsZipWrapped"/> set to <see langword="false"/>. XML that no known key decrypts yields an
    /// empty entry list and a <see langword="null"/> identifier.
    /// <para>返回格式名为 <c>OFP QC</c> 的元数据，含从 XML 解析出的条目数、解密该 XML 所用密钥标识符（无需解密时为
    /// <c>plaintext</c>）、<paramref name="fileSize"/>，且 <see cref="OppMetadata.IsZipWrapped"/> 为 <see langword="false"/>。
    /// 已知密钥都无法解密的 XML 得到空条目列表与 <see langword="null"/> 标识符。</para></returns>
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
    /// <param name="stream">Readable, seekable OFP stream owned by the caller; it is seeked to
    /// <see cref="OppEntry.Offset"/> for the duration of the copy, its original position is restored afterwards, and it
    /// is never closed. <para>调用方持有的可读、可寻址 OFP 流；复制期间寻址到 <see cref="OppEntry.Offset"/>，结束后恢复原始位置，且不会关闭该流。</para></param>
    /// <param name="output">Writable stream receiving <see cref="OppEntry.Size"/> bytes, the decrypted prefix followed by
    /// the stored remainder; it stays open and its current position advances.
    /// <para>接收 <see cref="OppEntry.Size"/> 字节的可写流，先写入解密前缀再写入其余原文；流保持打开，其当前位置随之前进。</para></param>
    /// <param name="entry">Entry descriptor whose offset, total size and encrypted prefix size define the region; its
    /// range must fit in <paramref name="stream"/> and its encrypted size must not exceed its size.
    /// <para>条目描述符，其偏移、总大小与加密前缀大小确定了读取区域；该区域必须落在 <paramref name="stream"/> 内，且加密大小不得超过条目大小。</para></param>
    /// <param name="metadata">Not consulted; the entry record carries the layout this format needs.
    /// <para>本参数不被使用；条目记录已带有该格式所需的布局信息。</para></param>
    /// <param name="keyId">Key identifier for the prefix decryption, or <c>plaintext</c> to copy the whole entry without
    /// decrypting; <see langword="null"/> or empty on an entry with a nonzero encrypted prefix fails the extraction.
    /// <para>用于解密前缀部分的密钥标识符，取值为 <c>plaintext</c> 时不解密而直接整条复制；加密前缀非零而该值为 <see langword="null"/> 或空字符串时提取失败。</para></param>
    /// <param name="cryptoProvider">Provider used for the prefix decryption, or <see langword="null"/> to fall back to the
    /// QC provider given to the constructor. <para>用于解密前缀部分的提供者；为 <see langword="null"/> 时回退到构造时传入的 QC 提供者。</para></param>
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