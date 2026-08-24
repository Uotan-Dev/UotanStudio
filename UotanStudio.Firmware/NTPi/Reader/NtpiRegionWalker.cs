using System.Buffers;
using System.Xml;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Exceptions;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Reader;

/// <summary>
/// <para>遍历 NTPI 固件包的区域链,对每个区域解密并调用回调。</para>
/// Walks the region chain of an NTPI firmware package, decrypting each region and invoking callbacks.
/// </summary>
/// <remarks>
/// <para>使用指定的加密提供者初始化 NtpiRegionWalker 的新实例。</para>
/// Initializes a new instance of NtpiRegionWalker with the specified crypto provider.
/// </remarks>
internal class NtpiRegionWalker(ICryptoProvider cryptoProvider)
{
    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));

    /// <summary>
    /// <para>遍历区域链,对每个区域解密并调用回调(内存模式)。</para>
    /// Walks the region chain, decrypting each region and invoking callbacks (in-memory mode).
    /// </summary>
    public void WalkRegionChain(byte[] fileData, int fileLength, NtpiHeader header, IKeyProvider keyProvider,
        Action<RegionType, long, byte[], int> onRegion)
    {
        long currentOffset = NtpiHeader.Size;
        var currentRegion = header.FirstRegionHeader;

        while (currentRegion.RegionSize > 0)
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
                onRegion(regionType, regionSize, fileData, (int)currentOffset);
                break;
            }

            var (key, iv) = keyProvider.GetKey(regionType)
                ?? throw new InvalidFormatException(
                    $"No decryption key found for region type {regionType} ({regionType.GetRegionName()}).");

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

                if (blockHeader.NextHeader.RegionSize == 0)
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

    /// <summary>
    /// <para>从流中遍历区域链(流式读取,避免全量加载)。</para>
    /// Walks the region chain from a stream (streaming, avoids full load).
    /// </summary>
    public void WalkRegionChainStreaming(Stream stream, NtpiHeader header, IKeyProvider keyProvider,
        Action<RegionType, long, byte[]> onRegion, Action<long, long> onRegion6Offset)
    {
        long currentOffset = NtpiHeader.Size;
        var currentRegion = header.FirstRegionHeader;

        while (currentRegion.RegionSize > 0)
        {
            var regionType = (RegionType)currentRegion.RegionType;
            var regionSize = (long)currentRegion.RegionSize;

            if (regionType == RegionType.Region6)
            {
                onRegion6Offset(currentOffset, regionSize);
                break;
            }

            var (key, iv) = keyProvider.GetKey(regionType)
                ?? throw new InvalidFormatException(
                    $"No decryption key found for region type {regionType} ({regionType.GetRegionName()}).");

            var regionSizeInt = (int)regionSize;
            var regionDataRent = ArrayPool<byte>.Shared.Rent(regionSizeInt);
            try
            {
                NtpiStreamHelper.ReadExact(stream, regionDataRent, 0, regionSizeInt);

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

                if (blockHeader.NextHeader.RegionSize == 0)
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

    /// <summary>
    /// <para>验证并从字节数据中读取 NTPI 头部。</para>
    /// Validates and reads the NTPI header from byte data.
    /// </summary>
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
    /// <para>从流中读取并验证 NTPI 头部。</para>
    /// Reads and validates the NTPI header from a stream.
    /// </summary>
    public static NtpiHeader ReadHeaderFromStream(Stream stream)
    {
        var buffer = new byte[NtpiHeader.Size];
        stream.ReadExactly(buffer);
        return ValidateAndReadHeader(buffer);
    }

    /// <summary>
    /// <para>从文件索引 XML 数据中解析文件条目列表。</para>
    /// Parses the file entry list from FileIndex XML data.
    /// </summary>
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