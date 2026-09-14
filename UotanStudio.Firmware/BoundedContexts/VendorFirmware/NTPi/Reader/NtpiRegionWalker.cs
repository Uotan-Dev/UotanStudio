using System.Buffers;
using System.Xml;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Exceptions;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Helpers;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Reader;

internal class NtpiRegionWalker(ICryptoProvider cryptoProvider)
{
    private const long MaximumStreamingRegionSize = 512L * 1024 * 1024;
    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));

    /// <summary>
    /// Walks the region chain, decrypting each region and invoking callbacks (in-memory mode).
    /// <para>遍历区域链,对每个区域解密并调用回调(内存模式)。</para>
    /// </summary>
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

    /// <summary>
    /// Walks the region chain from a stream (streaming, avoids full load).
    /// <para>从流中遍历区域链(流式读取,避免全量加载)。</para>
    /// </summary>
    public void WalkRegionChainStreaming(Stream stream, NtpiHeader header, IKeyProvider keyProvider,
        Action<RegionType, long, byte[]> onRegion, Action<long, long> onRegion6Offset)
    {
        long currentOffset = NtpiHeader.Size;
        var currentRegion = header.FirstRegionHeader;

        while (currentRegion.RegionSize > 0 || currentRegion.RegionType == (ulong)RegionType.Region6)
        {
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

    /// <summary>
    /// Validates and reads the NTPI header from byte data.
    /// <para>验证并从字节数据中读取 NTPI 头部。</para>
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
    /// Reads and validates the NTPI header from a stream.
    /// <para>从流中读取并验证 NTPI 头部。</para>
    /// </summary>
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

    /// <summary>
    /// Parses the file entry list from FileIndex XML data.
    /// <para>从文件索引 XML 数据中解析文件条目列表。</para>
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