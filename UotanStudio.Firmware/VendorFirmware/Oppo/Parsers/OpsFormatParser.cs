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
    /// <param name="stream">Readable, seekable OPS stream owned by the caller; the OPS reader seeks within it and
    /// restores its position, and never closes it. <para>调用方持有的可读、可寻址 OPS 流；OPS 读取器在其中寻址并恢复位置，且不会关闭该流。</para></param>
    /// <param name="fileSize">Total container byte length used to locate the trailing configuration page.
    /// <para>用于定位尾部配置页的容器总字节长度。</para></param>
    /// <param name="cryptoProvider">Not consulted; metadata decoding uses the built-in OPS key detection.
    /// <para>本参数不被使用；解析元数据依靠内置的 OPS 密钥探测。</para></param>
    /// <returns>The metadata read from the OPS tail page and its XML configuration.
    /// <para>从 OPS 尾页及其 XML 配置读出的元数据。</para></returns>
    public OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);
        return OppOpsParser.ParseMetadata(stream, fileSize);
    }

    /// <summary>Parses the entry list from a stream containing an OPS format firmware file.
    /// <para>从流中解析 OPS 格式固件文件的条目列表。</para></summary>
    /// <param name="stream">Readable, seekable OPS stream owned by the caller; the tail page and the XML region it points at are read from it and it is never closed.
    /// <para>调用方持有的可读、可寻址 OPS 流；从中读取尾页及其指向的 XML 区域，且不会关闭该流。</para></param>
    /// <param name="fileSize">Total container byte length used to locate the trailing configuration page.
    /// <para>用于定位尾部配置页的容器总字节长度。</para></param>
    /// <param name="metadata">Not consulted; the layout comes from the tail page read here.
    /// <para>本参数不被使用；布局信息由本遍读取的尾页给出。</para></param>
    /// <param name="cryptoProvider">Not consulted; the XML is decrypted by the built-in OPS key detection.
    /// <para>本参数不被使用；XML 由内置 OPS 密钥探测解密。</para></param>
    /// <returns>The entries declared by the decrypted XML, or an empty list when no known key decrypts it.
    /// <para>返回已解密 XML 声明的条目；已知密钥都无法解密时返回空列表。</para></returns>
    public IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);
        Guard.ThrowIfNull(metadata);

        if (metadata.ParsedEntries is { } cached)
        {
            return cached;
        }

        var tailPage = OppOpsParser.ReadTailPage(stream, fileSize);
        var decryptResult = OppOpsParser.TryDecryptXml(stream, tailPage);
        return decryptResult == null ? [] : OppOpsParser.ParseEntries(decryptResult.Value.Xml);
    }

    /// <summary>Extracts a single OPS format entry to an output stream.
    /// <para>提取单个 OPS 格式条目到输出流。</para></summary>
    /// <param name="stream">Readable, seekable OPS stream owned by the caller; it is seeked to <see cref="OppEntry.Offset"/> and left at the end of the copied region, and it is never closed. A source that ends early simply yields fewer written bytes.
    /// <para>调用方持有的可读、可寻址 OPS 流；寻址到 <see cref="OppEntry.Offset"/>，结束后停留在已复制区域末尾，且不会被该调用 关闭。源提前结束时只会写出更少的字节。</para></param>
    /// <param name="output">Writable stream receiving the entry bytes; it stays open and its position advances.
    /// <para>接收条目字节的可写流；保持打开，其位置随之前进。</para></param>
    /// <param name="entry">Entry descriptor supplying the offset, the byte size and the partition type that selects the handling; a size of 0 or less writes nothing.
    /// <para>提供偏移、字节大小与决定处理方式的分区类型的条目描述符；大小为 0 或更小时不写入任何字节。</para></param>
    /// <param name="metadata">Fallback source of the key identifier for a <c>sahara</c> entry when <paramref name="keyId"/> is <see langword="null"/>.
    /// <para>当 <paramref name="keyId"/> 为 <see langword="null"/> 时，为 <c>sahara</c> 条目提供后备密钥标识符。</para></param>
    /// <param name="keyId">Key identifier used only for <c>sahara</c> entries; <see langword="null"/> falls back to <paramref name="metadata"/>, and an empty value or <c>plaintext</c> copies the region without decrypting it.
    /// <para>仅用于 <c>sahara</c> 条目的密钥标识符；为 <see langword="null"/> 时回退到 <paramref name="metadata"/>， 为空字符串或 <c>plaintext</c> 时不解密而直接复制该区域。</para></param>
    /// <param name="cryptoProvider">Provider used for the <c>sahara</c> decryption, or <see langword="null"/> to create a fresh OPS provider for the call.
    /// <para>用于 <c>sahara</c> 解密的提供者；为 <see langword="null"/> 时本次调用新建一个 OPS 提供者。</para></param>
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