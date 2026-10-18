using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;

internal sealed class OfpMtkFormatParser : IOppFormatParser
{
    private readonly OfpMtkCryptoProvider _cryptoProvider;

    /// <summary>
    /// Initializes a new instance of the OfpMtkFormatParser class.
    /// <para>初始化 OfpMtkFormatParser 类的新实例。</para>
    /// </summary>
    /// <param name="cryptoProvider">MTK provider used for key auto-detection and for decryption when the caller supplies
    /// no provider of its own. <para>用于密钥自动探测、以及在调用方未提供自己的提供者时执行解密的 MTK 提供者。</para></param>
    public OfpMtkFormatParser(OfpMtkCryptoProvider cryptoProvider)
    {
        Guard.ThrowIfNull(cryptoProvider);
        _cryptoProvider = cryptoProvider;
    }

    public OppFormat SupportedFormat => OppFormat.OfpMtk;

    /// <summary>Parses OFP MTK metadata from a stream.<para>从流中解析 OFP MTK 元数据。</para></summary>
    /// <param name="stream">Readable, seekable OFP stream owned by the caller; it is seeked to the trailing main header, then to offset 0 for key detection, and its original position is restored before returning. It is never closed.
    /// <para>调用方持有的可读、可寻址 OFP 流；先寻址到文件末尾的主头部，再寻址到偏移 0 探测密钥，返回前恢复其原始位置，且不会关闭该流。</para></param>
    /// <param name="fileSize">Total container byte length; the main header is read from <c>fileSize</c> minus the header size, and a value below that header size is rejected. Recorded as <see cref="OppMetadata.FileSize"/> without re-deriving it from the stream.
    /// <para>容器总字节长度；主头部从 <c>fileSize</c> 减去头部大小的位置读取，小于该头部大小的取值会被拒绝。该值直接记入 <see cref="OppMetadata.FileSize"/>，不会由流重新推导。</para></param>
    /// <param name="cryptoProvider">Not consulted; key detection always uses the MTK provider given to the constructor.
    /// <para>本参数不被使用；密钥探测始终采用构造时传入的 MTK 提供者。</para></param>
    /// <returns>Metadata naming the format <c>OFP MTK</c> with the header's project, CPU model and flash type, the declared entry count, <paramref name="fileSize"/>, the auto-detected key identifier when the first 16 bytes decrypt to the <c>MMM</c> marker, and <see cref="OppMetadata.IsZipWrapped"/> set to <see langword="false"/>.
    /// <para>返回格式名为 <c>OFP MTK</c> 的元数据，含头部的工程名、CPU 型号与闪存类型、声明的条目数、 <paramref name="fileSize"/>；前 16 字节解出 <c>MMM</c> 标记时带上探测到的密钥标识符，且 <see cref="OppMetadata.IsZipWrapped"/> 为 <see langword="false"/>。</para></returns>
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

    /// <summary>Parses OFP MTK entry list from a stream.<para>从流中解析 OFP MTK 条目列表。</para></summary>
    /// <param name="stream">Readable, seekable OFP stream owned by the caller; the entry table is read from it and the stream is never closed.
    /// <para>调用方持有的可读、可寻址 OFP 流；从中读取条目表，且不会关闭该流。</para></param>
    /// <param name="fileSize">Total container byte length; the table is located at <c>fileSize</c> minus the header size and minus <c>EntryCount</c> record widths, so a value too small for that layout yields no entries.
    /// <para>容器总字节长度；条目表位于 <c>fileSize</c> 减去头部大小与 <c>EntryCount</c> 条记录宽度之处，取值小于该布局时不返回条目。</para></param>
    /// <param name="metadata">Metadata of the same container whose <see cref="OppMetadata.EntryCount"/> selects how many table records are read; a count of 0 or more than 10000 stops the read.
    /// <para>同一容器的元数据，其 <see cref="OppMetadata.EntryCount"/> 决定读取多少条表记录；计数为 0 或超过 10000 时停止读取。</para></param>
    /// <param name="cryptoProvider">Not consulted; reading the table needs no decryption.
    /// <para>本参数不被使用；读取条目表无需解密。</para></param>
    /// <returns>The declared entries in container order, or an empty list when the count is outside the supported range or the table does not fit in the container.
    /// <para>按容器顺序返回声明的条目；计数超出支持范围或条目表放不进容器时返回空列表。</para></returns>
    public IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        return OfpMtkParser.ParseEntries(stream, fileSize, metadata.EntryCount);
    }

    /// <summary>Extracts a single entry to an output stream.<para>提取单个条目到输出流。</para></summary>
    /// <param name="stream">Readable, seekable OFP stream owned by the caller; it is seeked to <see cref="OppEntry.Offset"/> for the duration of the copy and its original position is restored afterwards, and it is never closed.
    /// <para>调用方持有的可读、可寻址 OFP 流；复制期间寻址到 <see cref="OppEntry.Offset"/>，结束后恢复原始位置，且不会关闭该流。</para></param>
    /// <param name="output">Writable stream receiving <see cref="OppEntry.Size"/> bytes, the decrypted prefix followed by the stored remainder; it stays open and its current position advances.
    /// <para>接收 <see cref="OppEntry.Size"/> 字节的可写流，先写入解密前缀再写入其余原文；流保持打开，其当前位置随之前进。</para></param>
    /// <param name="entry">Entry descriptor whose offset, total size and encrypted prefix size define the region; its range must fit in <paramref name="stream"/> and its encrypted size must not exceed its size.
    /// <para>条目描述符，其偏移、总大小与加密前缀大小确定了读取区域；该区域必须落在 <paramref name="stream"/> 内，且加密大小不得超过条目大小。</para></param>
    /// <param name="metadata">Not consulted; the entry record carries the layout this format needs.
    /// <para>本参数不被使用；条目记录已带有该格式所需的布局信息。</para></param>
    /// <param name="keyId">Key identifier for the prefix decryption, or <c>plaintext</c> to copy the whole entry without decrypting; <see langword="null"/> or empty on an entry with a nonzero encrypted prefix fails the extraction.
    /// <para>用于解密前缀部分的密钥标识符，取值为 <c>plaintext</c> 时不解密而直接整条复制；加密前缀非零而该值为 <see langword="null"/> 或空字符串时提取失败。</para></param>
    /// <param name="cryptoProvider">Provider used for the prefix decryption, or <see langword="null"/> to fall back to the MTK provider given to the constructor.
    /// <para>用于解密前缀部分的提供者；为 <see langword="null"/> 时回退到构造时传入的 MTK 提供者。</para></param>
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