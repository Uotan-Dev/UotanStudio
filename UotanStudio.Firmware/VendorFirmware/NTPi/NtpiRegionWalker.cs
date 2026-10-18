using System.Buffers;
using System.Xml;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.VendorFirmware.NTPi;

internal class NtpiRegionWalker(ICryptoProvider cryptoProvider)
{
    private const long MaximumStreamingRegionSize = 512L * 1024 * 1024;
    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));

    /// <summary>Walks the region chain, decrypting each region and invoking callbacks (in-memory mode).
    /// <para>遍历区域链,对每个区域解密并调用回调(内存模式)。</para></summary>
    /// <param name="fileData">Complete NTPI file bytes; Region6 is handed to <paramref name="onRegion"/> as this array itself, while every other region is delivered as a freshly allocated decrypted payload.
    /// <para>完整的 NTPI 文件字节；Region6 会把该数组本身交给 <paramref name="onRegion"/>，其余区域交付新分配的解密载荷。</para></param>
    /// <param name="fileLength">Byte count treated as the end of the file; every region range must fit inside it, so a value below <paramref name="fileData"/>.Length restricts the walk to that prefix.
    /// <para>视为文件末尾的字节数；每个区域范围都必须落在其内，因此小于 <paramref name="fileData"/>.Length 的取值会把遍历限制在该前缀。</para></param>
    /// <param name="header">Validated NTPI header supplying the first region header for the walk.
    /// <para>已验证的 NTPI 头部，提供遍历起点的第一个区域头。</para></param>
    /// <param name="keyProvider">Provider returning the decryption key and IV per region type; a missing key for a non-Region6 type fails the walk.
    /// <para>按区域类型返回解密密钥与 IV 的提供者；非 Region6 类型缺少密钥会使遍历失败。</para></param>
    /// <param name="onRegion">Callback invoked once per region with the region type, the encrypted region size, the delivered bytes and the index within them where the payload starts; for Region6 that index is the absolute offset of the region inside <paramref name="fileData"/>, and it is 0 for every decrypted region.
    /// <para>每个区域调用一次的回调，参数为区域类型、密文区域大小、交付的字节以及载荷在其中的起始索引；Region6 时该索引是该区域在 <paramref name="fileData"/> 内的绝对偏移，已解密区域时为 0。</para></param>
    public void WalkRegionChain(byte[] fileData, int fileLength, NtpiHeader header, IKeyProvider keyProvider,
        Action<RegionType, long, byte[], int> onRegion)
    {
        long currentOffset = NtpiHeader.Size;
        var currentRegion = header.FirstRegionHeader;

        while (currentRegion.RegionSize > 0 || currentRegion.RegionType == (ulong)RegionType.Region6)
        {
            if (currentOffset + (long)currentRegion.RegionSize > fileLength)
            {
                throw new InvalidFormatException(
                    $"Region data exceeds file boundaries: offset {currentOffset} + size {currentRegion.RegionSize} > file length {fileLength}.");
            }

            var regionType = (RegionType)currentRegion.RegionType;
            var regionSize = (long)currentRegion.RegionSize;

            if (regionType == RegionType.Region6)
            {
                onRegion(regionType, regionSize, fileData, checked((int)currentOffset));
                break;
            }

            var (key, iv) = keyProvider.GetKey(regionType)
                ?? throw new InvalidFormatException(
                    $"No decryption key found for region type {regionType} ({regionType.GetRegionName()}).");

            if (regionSize > int.MaxValue)
            {
                throw new InvalidFormatException(
                    $"Region {regionType} is too large for in-memory walking: {regionSize} bytes.");
            }

            var regionSizeInt = (int)regionSize;
            var regionDataRent = ArrayPool<byte>.Shared.Rent(regionSizeInt);
            try
            {
                Buffer.BlockCopy(fileData, (int)currentOffset, regionDataRent, 0, regionSizeInt);

                var dataToDecrypt = regionSizeInt == regionDataRent.Length
                    ? regionDataRent
                    : regionDataRent.AsSpan(0, regionSizeInt).ToArray();
                var decrypted = _cryptoProvider.Decrypt(dataToDecrypt, key, iv);

                var blockHeader = StructHelper.ReadRegionBlockHeader(decrypted);

                var realSize = (long)blockHeader.RealSize;
                const int blockHeaderSize = 40;

                if (decrypted.Length < blockHeaderSize + realSize)
                {
                    throw new InvalidFormatException(
                        $"Decrypted region data is too small for region {regionType}: " +
                        $"expected {blockHeaderSize + realSize} bytes, got {decrypted.Length} bytes.");
                }

                var payload = decrypted.AsSpan(blockHeaderSize, (int)realSize).ToArray();

                onRegion(regionType, regionSize, payload, 0);

                if (blockHeader.NextHeader.RegionSize == 0 &&
                    blockHeader.NextHeader.RegionType != (ulong)RegionType.Region6)
                {
                    break;
                }

                currentOffset += regionSize;
                currentRegion = blockHeader.NextHeader;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(regionDataRent);
            }
        }
    }

    /// <summary>Walks the region chain from a stream (streaming, avoids full load).
    /// <para>从流中遍历区域链(流式读取,避免全量加载)。</para></summary>
    /// <param name="stream">NTPI file stream positioned just after the header and read region by region; its length bounds every region range.
    /// <para>位于头部之后、按区域顺序读取的 NTPI 文件流；其长度为所有区域范围提供上界。</para></param>
    /// <param name="header">Validated NTPI header supplying the first region header for the walk.
    /// <para>已验证的 NTPI 头部，提供遍历起点的第一个区域头。</para></param>
    /// <param name="keyProvider">Provider returning the decryption key and IV per region type; a missing key for a non-Region6 type fails the walk.
    /// <para>按区域类型返回解密密钥与 IV 的提供者；非 Region6 类型缺少密钥会使遍历失败。</para></param>
    /// <param name="onRegion">Callback invoked for each decrypted region with its type, encrypted size and payload.
    /// <para>对每个已解密区域调用的回调，参数为区域类型、密文大小与载荷。</para></param>
    /// <param name="onRegion6Offset">Callback invoked with the absolute offset and size when the walk reaches Region6 instead of delivering a payload.
    /// <para>遍历到达 Region6 时调用的回调，给出其绝对偏移与大小，而不是交付载荷。</para></param>
    /// <param name="cancellationToken">Token observed between regions; the default (none) token cannot cancel.
    /// <para>在区域之间检查的取消令牌；默认（无）令牌不可取消。</para></param>
    public void WalkRegionChainStreaming(Stream stream, NtpiHeader header, IKeyProvider keyProvider,
        Action<RegionType, long, byte[]> onRegion, Action<long, long> onRegion6Offset, CancellationToken cancellationToken = default)
    {
        long currentOffset = NtpiHeader.Size;
        var currentRegion = header.FirstRegionHeader;

        while (currentRegion.RegionSize > 0 || currentRegion.RegionType == (ulong)RegionType.Region6)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var regionType = (RegionType)currentRegion.RegionType;
            var regionSize = (long)currentRegion.RegionSize;

            if (regionSize < 0 || currentOffset > stream.Length || regionSize > stream.Length - currentOffset)
            {
                throw new InvalidFormatException(
                    $"Region data exceeds file boundaries: offset {currentOffset} + size {regionSize} > file length {stream.Length}.");
            }

            if (regionType == RegionType.Region6)
            {
                onRegion6Offset(currentOffset, regionSize);
                break;
            }

            var (key, iv) = keyProvider.GetKey(regionType)
                ?? throw new InvalidFormatException(
                    $"No decryption key found for region type {regionType} ({regionType.GetRegionName()}).");

            if (regionSize > MaximumStreamingRegionSize || regionSize > int.MaxValue)
            {
                throw new InvalidFormatException($"Region {regionType} exceeds the streaming region limit of {MaximumStreamingRegionSize} bytes.");
            }

            var regionSizeInt = (int)regionSize;
            var regionDataRent = ArrayPool<byte>.Shared.Rent(regionSizeInt);
            try
            {
                try
                {
                    NtpiStreamHelper.ReadExact(stream, regionDataRent, 0, regionSizeInt);
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidFormatException(
                        $"Region {regionType} is truncated: expected {regionSizeInt} bytes, but the file ended early.", ex);
                }

                var dataToDecrypt = regionSizeInt == regionDataRent.Length
                    ? regionDataRent
                    : regionDataRent.AsSpan(0, regionSizeInt).ToArray();
                var decrypted = _cryptoProvider.Decrypt(dataToDecrypt, key, iv);

                var blockHeader = StructHelper.ReadRegionBlockHeader(decrypted);

                var realSize = (long)blockHeader.RealSize;
                const int blockHeaderSize = 40;

                if (decrypted.Length < blockHeaderSize + realSize)
                {
                    throw new InvalidFormatException(
                        $"Decrypted region data is too small for region {regionType}: " +
                        $"expected {blockHeaderSize + realSize} bytes, got {decrypted.Length} bytes.");
                }

                var payload = decrypted.AsSpan(blockHeaderSize, (int)realSize).ToArray();

                onRegion(regionType, regionSize, payload);

                if (blockHeader.NextHeader.RegionSize == 0 &&
                    blockHeader.NextHeader.RegionType != (ulong)RegionType.Region6)
                {
                    break;
                }

                currentOffset += regionSize;
                currentRegion = blockHeader.NextHeader;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(regionDataRent);
            }
        }
    }

    /// <summary>Validates and reads the NTPI header from byte data.
    /// <para>验证并从字节数据中读取 NTPI 头部。</para></summary>
    /// <param name="fileData">Leading bytes of an NTPI file; it must hold at least <c>NtpiHeader.Size</c> bytes, and bytes past the header are ignored.
    /// <para>NTPI 文件的起始字节；至少要包含 <c>NtpiHeader.Size</c> 个字节，头部之后的字节会被忽略。</para></param>
    /// <returns>The parsed header whose magic has been checked.<para>已解析并通过魔数校验的头部。</para></returns>
    public static NtpiHeader ValidateAndReadHeader(byte[] fileData)
    {
        if (fileData.Length < NtpiHeader.Size)
        {
            throw new InvalidFormatException($"NTPI file is too small: {fileData.Length} bytes.");
        }

        var header = StructHelper.ReadNtpiHeader(fileData);
        return !header.IsValid()
            ? throw new InvalidFormatException($"Invalid NTPI magic: expected 'NTPI', got '{header.GetMagicString()}'.")
            : header;
    }

    /// <summary>
    /// Reads and validates the NTPI header from a stream.
    /// <para>从流中读取并验证 NTPI 头部。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable stream positioned at the first header byte; exactly <c>NtpiHeader.Size</c> bytes are consumed from it.
    /// <para>定位在首个头部字节处的可读流；从中精确读取 <c>NtpiHeader.Size</c> 个字节。</para>
    /// </param>
    /// <returns>
    /// The parsed header whose magic has been checked; the stream position ends just after the header.
    /// <para>已解析并通过魔数检查的头部；流位置停在头部之后。</para>
    /// </returns>
    public static NtpiHeader ReadHeaderFromStream(Stream stream)
    {
        var buffer = new byte[NtpiHeader.Size];
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidFormatException("NTPI file is truncated: the header is incomplete.", ex);
        }
        return ValidateAndReadHeader(buffer);
    }

    /// <summary>Parses the file entry list from FileIndex XML data.
    /// <para>从文件索引 XML 数据中解析文件条目列表。</para></summary>
    /// <param name="fileIndexData">Raw bytes of the decrypted FileIndex document, read without seeking; a document with no <c>file</c> elements yields an empty list and document type declarations are rejected.
    /// <para>已解密的 FileIndex 文档原始字节，读取时不会寻址；不含 <c>file</c> 元素的文档返回空列表，文档类型声明会被拒绝。</para></param>
    /// <returns>One entry per <c>file</c> element in document order; absent or unparsable attributes fall back to empty text or zero.
    /// <para>每个 <c>file</c> 元素对应一个条目，按文档顺序排列；缺失或无法解析的属性回退为空文本或 0。</para></returns>
    public static List<FileEntry> ParseFileEntries(byte[] fileIndexData)
    {
        try
        {
            var entries = new List<FileEntry>();
            using var stream = new MemoryStream(fileIndexData, writable: false);
            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                DtdProcessing = DtdProcessing.Prohibit
            };

            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "file")
                {
                    entries.Add(FileEntry.FromXmlAttributes(reader));
                }
            }

            return entries;
        }
        catch (XmlException ex)
        {
            throw new InvalidFormatException("Failed to parse FileIndex XML.", ex);
        }
    }
}